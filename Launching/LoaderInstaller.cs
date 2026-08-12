// File: Launching/LoaderInstaller.cs
using CmlLib.Core;
using LegendBorn.Services;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Launching;

/// <summary>
/// Installs NeoForge without requiring the end-user to open or directly reach neoforged.net.
/// The official Maven is deliberately the final emergency source, never the first one.
/// </summary>
public sealed class LoaderInstaller
{
    public const string OfficialNeoForgedMavenBase = "https://maven.neoforged.net/";
    public const string CloudBucketNeoForgeMavenBaseUrl = "https://maven.legendborn.ru/";
    public const string SourceForgeProjectSlug = "legendborn-neoforge";

    private const string EnvMavenMirrors = "LEGENDBORN_NEOFORGE_MAVEN_MIRRORS";
    private const long MaxInstallerBytes = 100L * 1024 * 1024;
    private const long JarPatchMaxTextEntryBytes = 4L * 1024 * 1024;
    private const int ProcessOutputCapChars = 512 * 1024;
    private const int DownloadRetryCount = 2;

    private static readonly TimeSpan MirrorProbeTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan OfficialMavenDownloadTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan InstallOverallTimeout = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan InstallStallTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan InstallerHeartbeatEvery = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan JavaProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly MinecraftPath _path;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly string[] _neoForgeMavenMirrors;
    private readonly bool _rewriteNeoForgeUrlsToMirror;

    private readonly SemaphoreSlim _mirrorSelectLock = new(1, 1);
    private (string MirrorBase, string ReleasesRoot)? _preferredMirror;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static string LauncherUa =>
        !string.IsNullOrWhiteSpace(LauncherIdentity.UserAgent)
            ? LauncherIdentity.UserAgent
            : $"LegendBornLauncher/{LauncherIdentity.InformationalVersion}";

    public LoaderInstaller(MinecraftPath path, HttpClient http, Action<string>? log = null)
        : this(path, http, neoForgeMavenMirrors: null, rewriteNeoForgeUrlsToMirror: true, log: log)
    {
    }

    public LoaderInstaller(
        MinecraftPath path,
        HttpClient http,
        IEnumerable<string>? neoForgeMavenMirrors,
        bool rewriteNeoForgeUrlsToMirror,
        Action<string>? log = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log;

        var configured = neoForgeMavenMirrors?.ToArray();
        if (configured is null || configured.Length == 0)
            configured = ResolveDefaultMavenMirrors();

        _neoForgeMavenMirrors = configured
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Where(static x => !x.Contains("maven.neoforged.net", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _rewriteNeoForgeUrlsToMirror = rewriteNeoForgeUrlsToMirror;
    }

    public async Task<string> EnsureInstalledAsync(
        string minecraftVersion,
        string loaderType,
        string loaderVersion,
        string installerUrl,
        CancellationToken ct)
    {
        minecraftVersion = (minecraftVersion ?? "").Trim();
        loaderType = NormalizeLoaderType(loaderType);
        loaderVersion = (loaderVersion ?? "").Trim();
        installerUrl = (installerUrl ?? "").Trim();

        if (minecraftVersion.Length == 0)
            throw new ArgumentException("minecraftVersion is required", nameof(minecraftVersion));

        if (loaderType == "vanilla")
            return minecraftVersion;

        if (loaderType != "neoforge")
            throw new NotSupportedException($"Loader '{loaderType}' не поддерживается. Поддерживается только NeoForge.");

        if (loaderVersion.Length == 0)
            throw new InvalidOperationException("NeoForge требует loader.version.");

        var officialInstallerUrl = GetOfficialNeoForgeInstallerUrl(loaderVersion);
        var expectedId = GetExpectedNeoForgeVersionId(minecraftVersion, loaderVersion);

        var sem = InstallLocks.GetOrAdd(expectedId, static _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsVersionPresent(expectedId))
            {
                _log?.Invoke($"NeoForge: уже установлен -> {expectedId}");
                await TryRewriteNeoForgeVersionJsonUrlsAsync(expectedId, loaderVersion, ct).ConfigureAwait(false);
                return expectedId;
            }

            var installerPath = await DownloadInstallerAsync(
                minecraftVersion,
                loaderVersion,
                installerUrl,
                officialInstallerUrl,
                ct).ConfigureAwait(false);

            if (!ValidateInstallerJar(installerPath, loaderVersion, out var validationError))
                throw new InvalidOperationException("NeoForge installer не прошёл проверку: " + validationError);

            var installedId = await InstallNeoForgeIntoGameDirAsync(
                installerPath,
                minecraftVersion,
                loaderVersion,
                expectedId,
                ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(installedId))
                throw new InvalidOperationException("NeoForge installer завершился, но установленная версия не найдена.");

            await TryRewriteNeoForgeVersionJsonUrlsAsync(installedId, loaderVersion, ct).ConfigureAwait(false);
            return installedId;
        }
        finally
        {
            try { sem.Release(); } catch { }
        }
    }

    private static string[] ResolveDefaultMavenMirrors()
    {
        var result = new List<string> { CloudBucketNeoForgeMavenBaseUrl };

        try
        {
            var env = Environment.GetEnvironmentVariable(EnvMavenMirrors) ?? "";
            result.AddRange(env.Split(
                new[] { ';', ',', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        catch { }

        return result
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(static x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeLoaderType(string? loaderType)
    {
        var value = (loaderType ?? "vanilla").Trim().ToLowerInvariant();
        return value.Length == 0 ? "vanilla" : value;
    }

    private static string GetOfficialNeoForgeInstallerUrl(string loaderVersion) =>
        $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar";

    private static string SourceForgeDirectCdnUrl(string loaderVersion) =>
        $"https://downloads.sourceforge.net/project/{SourceForgeProjectSlug}/neoforge/neoforge-{loaderVersion}-installer.jar";

    private static string SourceForgeWebFileDownloadUrl(string loaderVersion) =>
        $"https://sourceforge.net/projects/{SourceForgeProjectSlug}/files/neoforge/neoforge-{loaderVersion}-installer.jar/download";

    private static string GetExpectedNeoForgeVersionId(string minecraftVersion, string loaderVersion) =>
        $"{minecraftVersion}-neoforge-{loaderVersion}";

    private bool IsVersionPresent(string versionId)
    {
        var baseDir = _path.BasePath ?? "";
        if (baseDir.Length == 0) return false;
        return File.Exists(Path.Combine(baseDir, "versions", versionId, versionId + ".json"));
    }

    private async Task<(string MirrorBase, string ReleasesRoot)?> GetPreferredMirrorAsync(
        string loaderVersion,
        CancellationToken ct)
    {
        if (_neoForgeMavenMirrors.Length == 0)
            return null;

        if (_preferredMirror.HasValue)
            return _preferredMirror.Value;

        await _mirrorSelectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_preferredMirror.HasValue)
                return _preferredMirror.Value;

            var candidates = _neoForgeMavenMirrors
                .SelectMany(static mirror => new[]
                {
                    (MirrorBase: NormalizeAbsoluteBaseUrl(mirror), ReleasesRoot: NormalizeAbsoluteBaseUrl(mirror)),
                    (MirrorBase: NormalizeAbsoluteBaseUrl(mirror), ReleasesRoot: NormalizeAbsoluteBaseUrl(mirror + "releases/"))
                })
                .Where(static c => c.MirrorBase.Length > 0 && c.ReleasesRoot.Length > 0)
                .DistinctBy(static c => c.ReleasesRoot, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var probes = await Task.WhenAll(candidates.Select(async candidate =>
            {
                var artifactUrl = BuildInstallerUrlFromReleasesRoot(candidate.ReleasesRoot, loaderVersion);
                var sw = Stopwatch.StartNew();
                var ok = await IsUrlReachableForArtifactAsync(artifactUrl, ct).ConfigureAwait(false);
                sw.Stop();
                return (candidate, ok, sw.ElapsedMilliseconds);
            })).ConfigureAwait(false);

            var best = probes
                .Where(static p => p.ok)
                .OrderBy(static p => p.ElapsedMilliseconds)
                .Select(static p => p.candidate)
                .FirstOrDefault();

            if (best.MirrorBase.Length > 0 && best.ReleasesRoot.Length > 0)
            {
                _preferredMirror = best;
                _log?.Invoke($"NeoForge: Maven-зеркало выбрано -> {best.ReleasesRoot}");
                return best;
            }

            _log?.Invoke("NeoForge: собственные Maven-зеркала не ответили на exact artifact probe.");
            return null;
        }
        finally
        {
            try { _mirrorSelectLock.Release(); } catch { }
        }
    }

    private static string BuildInstallerUrlFromReleasesRoot(string releasesRoot, string loaderVersion) =>
        $"{NormalizeAbsoluteBaseUrl(releasesRoot)}net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar";

    private async Task<string> DownloadInstallerAsync(
        string minecraftVersion,
        string loaderVersion,
        string primaryInstallerUrl,
        string officialInstallerUrl,
        CancellationToken ct)
    {
        var preferred = await GetPreferredMirrorAsync(loaderVersion, ct).ConfigureAwait(false);
        var candidates = new List<string>();

        // 1. Own exact Maven mirrors first. This is the normal path for users in Russia.
        var mirrorRoots = new List<string>();
        if (preferred.HasValue)
            mirrorRoots.Add(preferred.Value.ReleasesRoot);

        foreach (var mirror in _neoForgeMavenMirrors)
        {
            mirrorRoots.Add(NormalizeAbsoluteBaseUrl(mirror));
            mirrorRoots.Add(NormalizeAbsoluteBaseUrl(mirror + "releases/"));
        }

        foreach (var root in mirrorRoots.Where(static x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            candidates.Add(BuildInstallerUrlFromReleasesRoot(root, loaderVersion));

        // 2. Explicit non-official catalog URL, if the server supplied one (for example Selectel).
        var normalizedPrimary = NormalizeAbsoluteUrl(primaryInstallerUrl);
        if (normalizedPrimary.Length > 0 && !IsOfficialNeoForgedUrl(normalizedPrimary))
            candidates.Add(normalizedPrimary);

        // 3. Exact versioned SourceForge copies. Never use /latest/download.
        candidates.Add(SourceForgeDirectCdnUrl(loaderVersion));
        candidates.Add(SourceForgeWebFileDownloadUrl(loaderVersion));

        // 4. Official Maven is the final emergency fallback and receives one short attempt.
        var normalizedOfficial = NormalizeAbsoluteUrl(officialInstallerUrl);
        if (normalizedOfficial.Length > 0)
            candidates.Add(normalizedOfficial);

        candidates = candidates
            .Select(NormalizeAbsoluteUrl)
            .Where(static url => url.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static url => IsOfficialNeoForgedUrl(url) ? 1 : 0)
            .ToList();

        var baseDir = _path.BasePath ?? "";
        if (baseDir.Length == 0)
            throw new InvalidOperationException("MinecraftPath.BasePath пустой.");

        var cacheDir = Path.Combine(baseDir, "launcher", "installers", "neoforge", minecraftVersion, loaderVersion);
        Directory.CreateDirectory(cacheDir);
        var local = Path.Combine(cacheDir, $"neoforge-{loaderVersion}-installer.jar");

        if (File.Exists(local))
        {
            if (ValidateInstallerJar(local, loaderVersion, out _))
            {
                _log?.Invoke($"NeoForge: использую проверенный installer из кеша ({loaderVersion}).");
                return local;
            }

            _log?.Invoke("NeoForge: installer в кеше не прошёл проверку и будет скачан заново.");
            TryDeleteQuiet(local);
        }

        Exception? last = null;
        foreach (var url in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var tmp = local + ".tmp";
            TryDeleteQuiet(tmp);

            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    continue;

                _log?.Invoke($"NeoForge: installer source -> {uri.Host}");
                await DownloadJarWithRetriesAsync(uri, tmp, ct).ConfigureAwait(false);

                if (!ValidateInstallerJar(tmp, loaderVersion, out var validationError))
                    throw new InvalidOperationException("получен неверный installer: " + validationError);

                var moved = await TryMoveOrReplaceWithRetryAsync(tmp, local, ct, attempts: 20, delayMs: 200)
                    .ConfigureAwait(false);
                if (!moved)
                    throw new IOException("Не удалось атомарно сохранить installer.jar.");

                _log?.Invoke($"NeoForge: installer {loaderVersion} проверен и сохранён.");
                return local;
            }
            catch (Exception ex)
            {
                last = ex;
                _log?.Invoke($"NeoForge: источник {url} не подошёл — {ex.Message}");
                TryDeleteQuiet(tmp);
            }
        }

        throw new InvalidOperationException(
            "Не удалось получить точный NeoForge installer ни с собственного Maven-зеркала, ни с SourceForge, ни с официального Maven.",
            last);
    }

    private async Task DownloadJarWithRetriesAsync(Uri uri, string tmpPath, CancellationToken ct)
    {
        var isOfficial = IsOfficialNeoForgedHost(uri);
        var maxAttempts = isOfficial ? 1 : DownloadRetryCount + 1;
        Exception? last = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(isOfficial ? OfficialMavenDownloadTimeout : DownloadTimeout);

                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/java-archive"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                TrySetUa(req);

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token)
                    .ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    var msg = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                    if (!isOfficial && IsTransient(resp.StatusCode) && attempt < maxAttempts - 1)
                        throw new HttpRequestException(msg, null, resp.StatusCode);
                    resp.EnsureSuccessStatusCode();
                }

                var media = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (media.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("вместо JAR получена HTML-страница/капча");

                var length = resp.Content.Headers.ContentLength;
                if (length.HasValue && (length.Value <= 0 || length.Value > MaxInstallerBytes))
                    throw new InvalidOperationException($"некорректный размер installer: {length.Value}");

                await using var input = await resp.Content.ReadAsStreamAsync(reqCts.Token).ConfigureAwait(false);
                await using var output = new FileStream(
                    tmpPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                await CopyWithLimitAsync(input, output, MaxInstallerBytes, reqCts.Token).ConfigureAwait(false);
                await output.FlushAsync(reqCts.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                TryDeleteQuiet(tmpPath);
                if (attempt >= maxAttempts - 1) break;
                await Task.Delay(TimeSpan.FromMilliseconds(400 + attempt * 700), ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Не удалось скачать installer.jar.", last);
    }

    private static bool ValidateInstallerJar(string path, string loaderVersion, out string error)
    {
        error = "";
        try
        {
            if (!File.Exists(path))
            {
                error = "файл отсутствует";
                return false;
            }

            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxInstallerBytes)
            {
                error = "некорректный размер";
                return false;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

            if (zip.Entries.Count == 0)
            {
                error = "пустой ZIP/JAR";
                return false;
            }

            var candidates = zip.Entries
                .Where(static entry =>
                    entry.FullName.EndsWith("install_profile.json", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.EndsWith("version.json", StringComparison.OrdinalIgnoreCase))
                .Where(static entry => entry.Length > 0 && entry.Length <= JarPatchMaxTextEntryBytes)
                .ToArray();

            foreach (var entry in candidates)
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();

                if (text.Contains("neoforge", StringComparison.OrdinalIgnoreCase) &&
                    text.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            error = $"в metadata JAR не найдена запрошенная версия NeoForge {loaderVersion}";
            return false;
        }
        catch (InvalidDataException)
        {
            error = "файл не является валидным ZIP/JAR";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsOfficialNeoForgedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsOfficialNeoForgedHost(uri);

    private static bool IsOfficialNeoForgedHost(Uri uri) =>
        uri.Host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(HttpStatusCode code)
    {
        var number = (int)code;
        return code == (HttpStatusCode)429 ||
               code == HttpStatusCode.RequestTimeout ||
               code == HttpStatusCode.BadGateway ||
               code == HttpStatusCode.ServiceUnavailable ||
               code == HttpStatusCode.GatewayTimeout ||
               number is >= 500 and <= 599;
    }

    private static async Task CopyWithLimitAsync(Stream input, Stream output, long maxBytes, CancellationToken ct)
    {
        byte[]? buffer = null;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0) break;
                total += read;
                if (total > maxBytes)
                    throw new InvalidOperationException($"Превышен лимит размера {maxBytes} bytes.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<string?> InstallNeoForgeIntoGameDirAsync(
        string installerPath,
        string minecraftVersion,
        string loaderVersion,
        string expectedId,
        CancellationToken ct)
    {
        var baseDir = _path.BasePath ?? "";
        if (baseDir.Length == 0)
            throw new InvalidOperationException("MinecraftPath.BasePath пустой.");

        Directory.CreateDirectory(Path.Combine(baseDir, "versions"));

        var javaExe = await FindCompatibleJavaAsync(ct).ConfigureAwait(false);
        var execInstallerPath = await PrepareInstallerJarForExecutionAsync(installerPath, loaderVersion, ct)
            .ConfigureAwait(false);
        var before = SnapshotVersionIds(baseDir);

        var argTries = new List<string[]>
        {
            new[] { "-jar", execInstallerPath, "--installClient", "--installDir", baseDir },
            new[] { "-jar", execInstallerPath, "--installClient", "--install-dir", baseDir },
            new[] { "-jar", execInstallerPath, "--install-client", "--installDir", baseDir },
            new[] { "-jar", execInstallerPath, "--install-client", "--install-dir", baseDir }
        };

        foreach (var args in argTries)
        {
            _log?.Invoke("NeoForge: запуск installer через Java 21+...");
            var res = await RunJavaStreamingAsync(
                javaExe,
                args,
                baseDir,
                ct,
                env: null,
                InstallOverallTimeout,
                InstallStallTimeout).ConfigureAwait(false);

            if (res.ExitCode == 0)
            {
                if (IsVersionPresent(expectedId)) return expectedId;
                var created = SnapshotVersionIds(baseDir).Except(before, StringComparer.OrdinalIgnoreCase).ToList();
                return PickNeoForgeVersionId(created, loaderVersion) ?? FindNeoForgeVersionIdInBase(baseDir, loaderVersion);
            }

            if (LooksLikeUnrecognizedOption(res.StdErr) || LooksLikeUnrecognizedOption(res.StdOut))
                break;
        }

        var tempAppData = Path.Combine(baseDir, "launcher", "tmp", "appdata");
        var tempMc = Path.Combine(tempAppData, ".minecraft");
        try
        {
            Directory.CreateDirectory(tempMc);
            EnsureLauncherProfileStub(tempMc);
            var beforeTemp = SnapshotVersionIds(tempMc);

            var env = new Dictionary<string, string>
            {
                ["APPDATA"] = tempAppData,
                ["LOCALAPPDATA"] = tempAppData
            };

            var res = await RunJavaStreamingAsync(
                javaExe,
                new[] { "-jar", execInstallerPath, "--installClient" },
                baseDir,
                ct,
                env,
                InstallOverallTimeout,
                InstallStallTimeout).ConfigureAwait(false);

            if (res.ExitCode != 0)
                throw new InvalidOperationException(
                    $"NeoForge installer завершился с code {res.ExitCode}: " +
                    (string.IsNullOrWhiteSpace(res.StdErr) ? res.StdOut : res.StdErr));

            var created = SnapshotVersionIds(tempMc).Except(beforeTemp, StringComparer.OrdinalIgnoreCase).ToList();
            var picked = PickNeoForgeVersionId(created, loaderVersion) ?? FindNeoForgeVersionIdInBase(tempMc, loaderVersion);

            MergeDir(Path.Combine(tempMc, "versions"), Path.Combine(baseDir, "versions"));
            MergeDir(Path.Combine(tempMc, "libraries"), Path.Combine(baseDir, "libraries"));
            MergeDir(Path.Combine(tempMc, "assets"), Path.Combine(baseDir, "assets"));

            if (!string.IsNullOrWhiteSpace(picked) && IsVersionPresent(picked)) return picked;
            if (IsVersionPresent(expectedId)) return expectedId;
            return FindNeoForgeVersionIdInBase(baseDir, loaderVersion);
        }
        finally
        {
            TryDeleteDirQuiet(tempAppData);
        }
    }

    private async Task<string> FindCompatibleJavaAsync(CancellationToken ct)
    {
        var candidates = new List<string>();
        var baseDir = _path.BasePath ?? "";
        var runtimeDir = Path.Combine(baseDir, "runtime");

        try
        {
            if (Directory.Exists(runtimeDir))
                candidates.AddRange(Directory.EnumerateFiles(runtimeDir, "java.exe", SearchOption.AllDirectories));
        }
        catch { }

        try
        {
            var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrWhiteSpace(javaHome))
            {
                var path = Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
                if (File.Exists(path)) candidates.Add(path);
            }
        }
        catch { }

        candidates.Add(OperatingSystem.IsWindows() ? "java.exe" : "java");

        foreach (var candidate in candidates.Where(static x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var probe = await ProbeJavaAsync(candidate, ct).ConfigureAwait(false);
            if (probe is { Major: >= 21, Is64Bit: true })
            {
                _log?.Invoke($"Java: найден совместимый runtime {probe.Major} x64 ({candidate}).");
                return candidate;
            }

            if (probe is not null)
                _log?.Invoke($"Java: пропускаю {candidate}: version={probe.Major}, x64={probe.Is64Bit}.");
        }

        throw new InvalidOperationException(
            "Для Minecraft/NeoForge 1.21.1 требуется 64-битная Java 21+. " +
            "Совместимый runtime не найден. Установи Microsoft OpenJDK/Temurin 21 x64 или добавь runtime в инстанс лаунчера.");
    }

    private sealed record JavaProbe(int Major, bool Is64Bit);

    private static async Task<JavaProbe?> ProbeJavaAsync(string javaExe, CancellationToken ct)
    {
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(JavaProbeTimeout);

            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-XshowSettings:properties");
            psi.ArgumentList.Add("-version");

            using var process = Process.Start(psi);
            if (process is null) return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(probeCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(probeCts.Token);
            await process.WaitForExitAsync(probeCts.Token).ConfigureAwait(false);

            var text = (await stdoutTask.ConfigureAwait(false)) + "\n" + (await stderrTask.ConfigureAwait(false));
            if (process.ExitCode != 0) return null;

            var major = ParseJavaMajor(text);
            var is64 =
                text.Contains("sun.arch.data.model = 64", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("amd64", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("x86_64", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ||
                Environment.Is64BitOperatingSystem;

            return major > 0 ? new JavaProbe(major, is64) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static int ParseJavaMajor(string text)
    {
        foreach (var line in (text ?? "").Split('\n'))
        {
            var trimmed = line.Trim();
            var marker = "java.version =";
            if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                var version = trimmed[marker.Length..].Trim();
                return ParseJavaVersionToken(version);
            }

            var quote = trimmed.IndexOf("version \"", StringComparison.OrdinalIgnoreCase);
            if (quote >= 0)
            {
                var start = quote + "version \"".Length;
                var end = trimmed.IndexOf('"', start);
                if (end > start)
                    return ParseJavaVersionToken(trimmed[start..end]);
            }
        }

        return 0;
    }

    private static int ParseJavaVersionToken(string version)
    {
        var first = (version ?? "").Split('.', '-', '+')[0];
        if (!int.TryParse(first, out var major)) return 0;
        if (major != 1) return major;

        var parts = (version ?? "").Split('.');
        return parts.Length > 1 && int.TryParse(parts[1], out var legacy) ? legacy : 0;
    }

    private static HashSet<string> SnapshotVersionIds(string baseDir)
    {
        var versionsDir = Path.Combine(baseDir, "versions");
        if (!Directory.Exists(versionsDir)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return Directory.EnumerateDirectories(versionsDir)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? PickNeoForgeVersionId(IEnumerable<string> candidates, string loaderVersion) =>
        candidates.FirstOrDefault(id =>
            id.Contains("neoforge", StringComparison.OrdinalIgnoreCase) &&
            id.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase));

    private static string? FindNeoForgeVersionIdInBase(string baseDir, string loaderVersion)
    {
        var versionsDir = Path.Combine(baseDir, "versions");
        if (!Directory.Exists(versionsDir)) return null;
        return PickNeoForgeVersionId(Directory.EnumerateDirectories(versionsDir).Select(Path.GetFileName).Where(static x => x is not null)!, loaderVersion);
    }

    private static bool LooksLikeUnrecognizedOption(string? value)
    {
        var text = value ?? "";
        return text.Contains("UnrecognizedOptionException", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("is not a recognized option", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Unknown option", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Unknown argument", StringComparison.OrdinalIgnoreCase);
    }

    private static void MergeDir(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void EnsureLauncherProfileStub(string mcDir)
    {
        Directory.CreateDirectory(mcDir);
        var stub = JsonSerializer.Serialize(new
        {
            profiles = new Dictionary<string, object>(),
            settings = new Dictionary<string, object>(),
            selectedProfile = "",
            authenticationDatabase = new Dictionary<string, object>(),
            launcherVersion = new { name = "LegendBorn", format = 21 }
        }, new JsonSerializerOptions { WriteIndented = true });

        foreach (var name in new[] { "launcher_profiles.json", "launcher_profiles_microsoft_store.json" })
        {
            var path = Path.Combine(mcDir, name);
            if (!File.Exists(path)) File.WriteAllText(path, stub);
        }
    }

    private async Task<string> PrepareInstallerJarForExecutionAsync(
        string installerPath,
        string loaderVersion,
        CancellationToken ct)
    {
        if (!_rewriteNeoForgeUrlsToMirror) return installerPath;

        try
        {
            var preferred = await GetPreferredMirrorAsync(loaderVersion, ct).ConfigureAwait(false);
            if (!preferred.HasValue) return installerPath;
            if (!JarLikelyNeedsMavenPatch(installerPath)) return installerPath;

            var dir = Path.GetDirectoryName(installerPath) ?? "";
            var patchedPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(installerPath) + ".mirrored.jar");

            if (File.Exists(patchedPath) &&
                File.GetLastWriteTimeUtc(patchedPath) >= File.GetLastWriteTimeUtc(installerPath) &&
                ValidateInstallerJar(patchedPath, loaderVersion, out _))
                return patchedPath;

            var tmp = patchedPath + ".tmp";
            TryDeleteQuiet(tmp);

            _log?.Invoke($"NeoForge: переписываю installer dependency URLs -> {preferred.Value.ReleasesRoot}");
            await CreateMirroredInstallerJarAsync(
                installerPath,
                tmp,
                preferred.Value.MirrorBase,
                preferred.Value.ReleasesRoot,
                ct).ConfigureAwait(false);

            if (!ValidateInstallerJar(tmp, loaderVersion, out var error))
                throw new InvalidOperationException("patched installer invalid: " + error);

            if (!await TryMoveOrReplaceWithRetryAsync(tmp, patchedPath, ct, 20, 150).ConfigureAwait(false))
                return installerPath;

            return patchedPath;
        }
        catch (Exception ex)
        {
            _log?.Invoke("NeoForge: не удалось подготовить mirrored installer — " + ex.Message);
            return installerPath;
        }
    }

    private static bool JarLikelyNeedsMavenPatch(string installerPath)
    {
        try
        {
            using var fs = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            return zip.Entries
                .Where(static entry => entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Where(static entry => entry.Length > 0 && entry.Length <= JarPatchMaxTextEntryBytes)
                .Any(entry =>
                {
                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream, Encoding.UTF8, true);
                    var text = reader.ReadToEnd();
                    return text.Contains("neoforged.net", StringComparison.OrdinalIgnoreCase);
                });
        }
        catch
        {
            return true;
        }
    }

    private static bool IsSignatureEntry(string fullName)
    {
        if (!fullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) return false;
        return fullName.EndsWith(".SF", StringComparison.OrdinalIgnoreCase) ||
               fullName.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase) ||
               fullName.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) ||
               fullName.EndsWith(".EC", StringComparison.OrdinalIgnoreCase) ||
               fullName.Contains("/SIG-", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CreateMirroredInstallerJarAsync(
        string sourceJarPath,
        string destinationJarPath,
        string mirrorBase,
        string releasesRoot,
        CancellationToken ct)
    {
        await using var srcFs = new FileStream(sourceJarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var srcZip = new ZipArchive(srcFs, ZipArchiveMode.Read);
        await using var dstFs = new FileStream(destinationJarPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var dstZip = new ZipArchive(dstFs, ZipArchiveMode.Create);

        foreach (var entry in srcZip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (IsSignatureEntry(entry.FullName) || entry.FullName.EndsWith("/", StringComparison.Ordinal))
                continue;

            var dstEntry = dstZip.CreateEntry(entry.FullName, CompressionLevel.Fastest);
            dstEntry.LastWriteTime = entry.LastWriteTime;

            if (entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                entry.Length > 0 && entry.Length <= JarPatchMaxTextEntryBytes)
            {
                using var src = entry.Open();
                using var reader = new StreamReader(src, Encoding.UTF8, true);
                var text = reader.ReadToEnd();
                var patched = RewriteAllOfficialUrls(text, mirrorBase, releasesRoot);

                await using var output = dstEntry.Open();
                await using var writer = new StreamWriter(output, new UTF8Encoding(false));
                await writer.WriteAsync(patched).ConfigureAwait(false);
                continue;
            }

            await using var rawSrc = entry.Open();
            await using var rawDst = dstEntry.Open();
            await rawSrc.CopyToAsync(rawDst, 128 * 1024, ct).ConfigureAwait(false);
        }
    }

    private static string RewriteAllOfficialUrls(string text, string mirrorBase, string releasesRoot)
    {
        mirrorBase = NormalizeAbsoluteBaseUrl(mirrorBase);
        releasesRoot = NormalizeAbsoluteBaseUrl(releasesRoot);

        foreach (var prefix in new[]
        {
            "https://maven.neoforged.net/releases/",
            "http://maven.neoforged.net/releases/",
            "https://mirrors.neoforged.net/releases/",
            "http://mirrors.neoforged.net/releases/"
        })
            text = text.Replace(prefix, releasesRoot, StringComparison.OrdinalIgnoreCase);

        foreach (var prefix in new[]
        {
            "https://maven.neoforged.net/",
            "http://maven.neoforged.net/",
            "https://mirrors.neoforged.net/",
            "http://mirrors.neoforged.net/"
        })
            text = text.Replace(prefix, mirrorBase, StringComparison.OrdinalIgnoreCase);

        return text;
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunJavaStreamingAsync(
        string javaExe,
        IEnumerable<string> args,
        string workingDir,
        CancellationToken ct,
        IDictionary<string, string>? env,
        TimeSpan overallTimeout,
        TimeSpan stallTimeout)
    {
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(overallTimeout);

        var psi = new ProcessStartInfo
        {
            FileName = javaExe,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (env is not null)
            foreach (var pair in env) psi.Environment[pair.Key] = pair.Value;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var sync = new object();
        long lastOutputMs = Environment.TickCount64;

        static void AppendCapped(StringBuilder builder, string line)
        {
            if (builder.Length >= ProcessOutputCapChars) return;
            var remaining = ProcessOutputCapChars - builder.Length;
            if (line.Length + 1 > remaining)
                line = line[..Math.Max(0, remaining - 1)];
            builder.AppendLine(line);
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (sync) AppendCapped(stdout, e.Data);
            Interlocked.Exchange(ref lastOutputMs, Environment.TickCount64);
            _log?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (sync) AppendCapped(stderr, e.Data);
            Interlocked.Exchange(ref lastOutputMs, Environment.TickCount64);
            _log?.Invoke(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить Java для NeoForge installer.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            while (!process.HasExited)
            {
                overallCts.Token.ThrowIfCancellationRequested();
                var silentFor = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref lastOutputMs));
                if (silentFor >= stallTimeout)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    throw new TimeoutException(
                        "NeoForge installer не выдаёт вывод слишком долго. Вероятно, недоступно Maven-зеркало или соединение фильтруется.");
                }

                if (silentFor >= InstallerHeartbeatEvery)
                    _log?.Invoke("NeoForge: installer всё ещё работает...");

                await Task.Delay(500, overallCts.Token).ConfigureAwait(false);
            }
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("NeoForge installer превысил общий таймаут выполнения.");
        }

        lock (sync)
            return (process.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    private async Task TryRewriteNeoForgeVersionJsonUrlsAsync(
        string versionId,
        string loaderVersion,
        CancellationToken ct)
    {
        if (!_rewriteNeoForgeUrlsToMirror) return;

        var preferred = await GetPreferredMirrorAsync(loaderVersion, ct).ConfigureAwait(false);
        if (!preferred.HasValue) return;

        var baseDir = _path.BasePath ?? "";
        var jsonPath = Path.Combine(baseDir, "versions", versionId, versionId + ".json");
        if (!File.Exists(jsonPath)) return;

        try
        {
            var text = File.ReadAllText(jsonPath);
            if (!text.Contains("neoforged.net", StringComparison.OrdinalIgnoreCase)) return;

            var node = JsonNode.Parse(text);
            if (node is null) return;

            var changed = ReplaceStringsRecursive(node, value =>
                RewriteAllOfficialUrls(value, preferred.Value.MirrorBase, preferred.Value.ReleasesRoot));
            if (!changed) return;

            var tmp = jsonPath + ".tmp";
            File.WriteAllText(tmp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            ReplaceOrMoveAtomic(tmp, jsonPath);
            TryDeleteQuiet(tmp);
            _log?.Invoke($"NeoForge: version JSON переведён на зеркало {preferred.Value.ReleasesRoot}");
        }
        catch (Exception ex)
        {
            _log?.Invoke("NeoForge: version JSON mirror rewrite failed — " + ex.Message);
        }
    }

    private async Task<bool> IsUrlReachableForArtifactAsync(string url, CancellationToken ct)
    {
        if (await ProbeArtifactAsync(HttpMethod.Head, url, ct).ConfigureAwait(false)) return true;
        return await ProbeArtifactAsync(HttpMethod.Get, url, ct).ConfigureAwait(false);
    }

    private async Task<bool> ProbeArtifactAsync(HttpMethod method, string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(MirrorProbeTimeout);
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            TrySetUa(req);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            return (int)resp.StatusCode < 400;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReplaceStringsRecursive(JsonNode node, Func<string, string> replacer)
    {
        var any = false;
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(static item => item.Key).ToList())
            {
                var child = obj[key];
                if (child is JsonValue value && value.TryGetValue<string>(out var text) && text is not null)
                {
                    var replacement = replacer(text);
                    if (!string.Equals(text, replacement, StringComparison.Ordinal))
                    {
                        obj[key] = replacement;
                        any = true;
                    }
                }
                else if (child is not null && ReplaceStringsRecursive(child, replacer))
                {
                    any = true;
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var child = array[i];
                if (child is JsonValue value && value.TryGetValue<string>(out var text) && text is not null)
                {
                    var replacement = replacer(text);
                    if (!string.Equals(text, replacement, StringComparison.Ordinal))
                    {
                        array[i] = replacement;
                        any = true;
                    }
                }
                else if (child is not null && ReplaceStringsRecursive(child, replacer))
                {
                    any = true;
                }
            }
        }

        return any;
    }

    private static void TrySetUa(HttpRequestMessage req)
    {
        try
        {
            req.Headers.UserAgent.Clear();
            req.Headers.UserAgent.ParseAdd(LauncherUa);
        }
        catch { }
    }

    private static string NormalizeAbsoluteUrl(string? value)
    {
        var text = (value ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "";
        return new UriBuilder(uri) { Query = "", Fragment = "" }.Uri.ToString();
    }

    private static string NormalizeAbsoluteBaseUrl(string? value)
    {
        var url = NormalizeAbsoluteUrl(value);
        if (url.Length == 0) return "";
        return url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";
    }

    private static async Task<bool> TryMoveOrReplaceWithRetryAsync(
        string source,
        string destination,
        CancellationToken ct,
        int attempts,
        int delayMs)
    {
        for (var i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ReplaceOrMoveAtomic(source, destination);
                return true;
            }
            catch (IOException) when (i < attempts - 1)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (i < attempts - 1)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }
        return false;
    }

    private static void ReplaceOrMoveAtomic(string sourceTmp, string destination)
    {
        if (OperatingSystem.IsWindows() && File.Exists(destination))
        {
            var backup = destination + ".bak";
            try
            {
                TryDeleteQuiet(backup);
                File.Replace(sourceTmp, destination, backup, ignoreMetadataErrors: true);
            }
            finally
            {
                TryDeleteQuiet(backup);
            }
            return;
        }
        File.Move(sourceTmp, destination, overwrite: true);
    }

    private static void TryDeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirQuiet(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}
