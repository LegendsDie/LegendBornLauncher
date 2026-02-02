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

public sealed class LoaderInstaller
{
    // =========================
    // Maven / Mirrors
    // =========================

    // Official base (держим с /)
    public const string OfficialNeoForgedMavenBase = "https://maven.neoforged.net/";

    // Primary mirror (Cloudflare Worker + R2 cache)
    public const string CloudBucketNeoForgeMavenBaseUrl = "https://maven.legendborn.ru/";

    // Optional mirror (если появится) — укажи базу с /
    public const string SelectelNeoForgeMavenBaseUrl = ""; // TODO: set when ready

    public static readonly string[] DefaultNeoForgeMavenMirrors =
    {
        CloudBucketNeoForgeMavenBaseUrl,
        SelectelNeoForgeMavenBaseUrl
    };

    // =========================
    // SourceForge (installer.jar only)
    // =========================

    public const string SourceForgeProjectSlug = "legendborn-neoforge";

    private static string SourceForgeLatestDownloadUrl =>
        $"https://sourceforge.net/projects/{SourceForgeProjectSlug}/files/latest/download";

    // Direct CDN
    private static string SourceForgeDirectCdnUrl(string loaderVersion) =>
        $"https://downloads.sourceforge.net/project/{SourceForgeProjectSlug}/neoforge/neoforge-{loaderVersion}-installer.jar";

    // Web redirect
    private static string SourceForgeWebFileDownloadUrl(string loaderVersion) =>
        $"https://sourceforge.net/projects/{SourceForgeProjectSlug}/files/neoforge/neoforge-{loaderVersion}-installer.jar/download";

    // =========================
    // Limits / Timeouts
    // =========================

    private const long MaxInstallerBytes = 100L * 1024 * 1024; // 100 MB safety

    // общий таймаут на загрузку (зеркала/SourceForge)
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);

    // Для официального maven (часто "висит" в РФ) — очень короткий таймаут и без ретраев
    private static readonly TimeSpan OfficialMavenDownloadTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan InstallOverallTimeout = TimeSpan.FromMinutes(25);

    // Stall: если нет вывода — считаем зависшим
    private static readonly TimeSpan InstallStallTimeout = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan InstallerHeartbeatEvery = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan MirrorProbeTimeout = TimeSpan.FromSeconds(3);

    // ретраи только для НЕ-официальных источников
    private const int DownloadRetryCount = 2;

    // ограничение вывода процесса, чтобы не раздувать память
    private const int ProcessOutputCapChars = 512 * 1024; // 512 KB stdout/stderr

    // Ограничим размер текстовых JSON в jar для патча (на всякий случай)
    private const long JarPatchMaxTextEntryBytes = 2L * 1024 * 1024; // 2 MB

    // =========================
    // Fields
    // =========================

    private readonly MinecraftPath _path;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    private readonly string[] _neoForgeMavenMirrors;
    private readonly bool _rewriteNeoForgeUrlsToMirror;

    // Кеш выбранного зеркала (base + releasesRoot)
    private readonly SemaphoreSlim _mirrorSelectLock = new(1, 1);
    private (string MirrorBase, string ReleasesRoot)? _preferredMirror;

    // Глобальный замок на установку по versionId
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _installLocks =
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

        _neoForgeMavenMirrors = (neoForgeMavenMirrors ?? DefaultNeoForgeMavenMirrors)
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _rewriteNeoForgeUrlsToMirror = rewriteNeoForgeUrlsToMirror;
    }

    // =========================
    // Public API
    // =========================

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

        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException("minecraftVersion is required", nameof(minecraftVersion));

        if (loaderType == "vanilla")
            return minecraftVersion;

        if (loaderType != "neoforge")
            throw new NotSupportedException($"Loader '{loaderType}' не поддерживается. Поддерживается только NeoForge.");

        if (string.IsNullOrWhiteSpace(loaderVersion))
            throw new InvalidOperationException("NeoForge требует версию (loader.version).");

        var officialInstallerUrl = GetOfficialNeoForgeInstallerUrl(loaderVersion);
        if (string.IsNullOrWhiteSpace(installerUrl))
            installerUrl = officialInstallerUrl;

        if (string.IsNullOrWhiteSpace(installerUrl))
            throw new InvalidOperationException("NeoForge требует installerUrl (или должна строиться официальная ссылка).");

        var expectedId = GetExpectedNeoForgeVersionId(minecraftVersion, loaderVersion);

        var sem = _installLocks.GetOrAdd(expectedId, _ => new SemaphoreSlim(1, 1));
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
                minecraftVersion: minecraftVersion,
                loaderVersion: loaderVersion,
                primaryInstallerUrl: installerUrl,
                officialInstallerUrl: officialInstallerUrl,
                ct: ct).ConfigureAwait(false);

            var installedId = await InstallNeoForgeIntoGameDirAsync(
                installerPath: installerPath,
                minecraftVersion: minecraftVersion,
                loaderVersion: loaderVersion,
                expectedId: expectedId,
                ct: ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(installedId))
                throw new InvalidOperationException("NeoForge installer отработал, но версия лоадера не найдена в versions/.");

            await TryRewriteNeoForgeVersionJsonUrlsAsync(installedId!, loaderVersion, ct).ConfigureAwait(false);
            return installedId!;
        }
        finally
        {
            try { sem.Release(); } catch { }
        }
    }

    // =========================
    // Loader / Version helpers
    // =========================

    private static string NormalizeLoaderType(string? loaderType)
    {
        var t = (loaderType ?? "vanilla").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(t)) return "vanilla";
        if (t == "neoforge") return "neoforge";
        if (t == "vanilla") return "vanilla";
        return t;
    }

    private static string GetOfficialNeoForgeInstallerUrl(string loaderVersion)
    {
        loaderVersion = (loaderVersion ?? "").Trim();
        if (string.IsNullOrWhiteSpace(loaderVersion)) return "";
        return $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar";
    }

    private static string GetExpectedNeoForgeVersionId(string mc, string loaderVersion)
        => $"{mc}-neoforge-{loaderVersion}".Trim();

    private bool IsVersionPresent(string versionId)
    {
        var baseDir = _path.BasePath ?? "";
        if (string.IsNullOrWhiteSpace(baseDir)) return false;

        var json = Path.Combine(baseDir, "versions", versionId, versionId + ".json");
        return File.Exists(json);
    }

    // =========================
    // Mirror selection (base + releases root)
    // =========================

    private async Task<(string MirrorBase, string ReleasesRoot)?> GetPreferredMirrorAsync(string loaderVersion, CancellationToken ct)
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

            loaderVersion = (loaderVersion ?? "").Trim();
            if (string.IsNullOrWhiteSpace(loaderVersion))
                return null;

            var candidates = new List<(string MirrorBase, string ReleasesRoot)>();

            foreach (var b in _neoForgeMavenMirrors)
            {
                var mirrorBase = NormalizeAbsoluteBaseUrl(b);
                if (string.IsNullOrWhiteSpace(mirrorBase))
                    continue;

                var releasesRootA = mirrorBase;
                var releasesRootB = NormalizeAbsoluteBaseUrl(mirrorBase + "releases/");

                candidates.Add((mirrorBase, releasesRootA));
                candidates.Add((mirrorBase, releasesRootB));
            }

            candidates = candidates
                .GroupBy(x => (x.MirrorBase.ToLowerInvariant(), x.ReleasesRoot.ToLowerInvariant()))
                .Select(g => g.First())
                .ToList();

            var probeTasks = candidates.Select(async c =>
            {
                var testUrl = BuildInstallerUrlFromReleasesRoot(c.ReleasesRoot, loaderVersion);
                var sw = Stopwatch.StartNew();
                var ok = await IsUrlReachableForArtifactAsync(testUrl, ct).ConfigureAwait(false);
                sw.Stop();
                return (Candidate: c, Ok: ok, ElapsedMs: sw.ElapsedMilliseconds, TestUrl: testUrl);
            }).ToArray();

            var results = await Task.WhenAll(probeTasks).ConfigureAwait(false);

            var best = results
                .Where(r => r.Ok)
                .OrderBy(r => r.ElapsedMs)
                .Select(r => r.Candidate)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(best.MirrorBase) && !string.IsNullOrWhiteSpace(best.ReleasesRoot))
            {
                _preferredMirror = best;
                _log?.Invoke($"NeoForge: выбрано зеркало Maven: base={best.MirrorBase} releasesRoot={best.ReleasesRoot}");
                return _preferredMirror.Value;
            }

            _log?.Invoke("NeoForge: зеркала Maven недоступны (probe failed). Использую SourceForge/официальный URL.");
            _preferredMirror = null;
            return null;
        }
        finally
        {
            _mirrorSelectLock.Release();
        }
    }

    private static string BuildInstallerUrlFromReleasesRoot(string releasesRoot, string loaderVersion)
    {
        releasesRoot = NormalizeAbsoluteBaseUrl(releasesRoot);
        loaderVersion = (loaderVersion ?? "").Trim();

        return $"{releasesRoot}net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar";
    }

    // =========================
    // Download installer
    // =========================

    private async Task<string> DownloadInstallerAsync(
        string minecraftVersion,
        string loaderVersion,
        string primaryInstallerUrl,
        string officialInstallerUrl,
        CancellationToken ct)
    {
        var tries = new List<string>();

        if (!string.IsNullOrWhiteSpace(primaryInstallerUrl))
            tries.Add(primaryInstallerUrl.Trim());

        var preferred = await GetPreferredMirrorAsync(loaderVersion, ct).ConfigureAwait(false);

        // зеркала: preferred -> остальные
        var mirrorBases = _neoForgeMavenMirrors.ToList();
        if (preferred.HasValue)
        {
            mirrorBases.RemoveAll(x =>
                string.Equals(
                    NormalizeAbsoluteBaseUrl(x),
                    NormalizeAbsoluteBaseUrl(preferred.Value.MirrorBase),
                    StringComparison.OrdinalIgnoreCase));

            mirrorBases.Insert(0, preferred.Value.MirrorBase);
        }

        foreach (var mirrorBase in mirrorBases)
        {
            var mb = NormalizeAbsoluteBaseUrl(mirrorBase);
            if (string.IsNullOrWhiteSpace(mb)) continue;

            var releasesRootA = mb;
            var releasesRootB = NormalizeAbsoluteBaseUrl(mb + "releases/");

            foreach (var releasesRoot in new[] { releasesRootA, releasesRootB })
            {
                var mirrorUrl1 = RewriteUrlPrefix(primaryInstallerUrl, "https://maven.neoforged.net/releases/", releasesRoot);
                if (!string.IsNullOrWhiteSpace(mirrorUrl1)) tries.Add(mirrorUrl1);

                var mirrorUrl2 = RewriteUrlPrefix(officialInstallerUrl, "https://maven.neoforged.net/releases/", releasesRoot);
                if (!string.IsNullOrWhiteSpace(mirrorUrl2)) tries.Add(mirrorUrl2);
            }

            var mirrorUrl3 = RewriteUrlPrefix(primaryInstallerUrl, OfficialNeoForgedMavenBase, mb);
            if (!string.IsNullOrWhiteSpace(mirrorUrl3)) tries.Add(mirrorUrl3);

            var mirrorUrl4 = RewriteUrlPrefix(officialInstallerUrl, OfficialNeoForgedMavenBase, mb);
            if (!string.IsNullOrWhiteSpace(mirrorUrl4)) tries.Add(mirrorUrl4);

            var mirrorUrl5 = RewriteUrlPrefix(primaryInstallerUrl, "https://maven.neoforged.net", mb.TrimEnd('/'));
            if (!string.IsNullOrWhiteSpace(mirrorUrl5)) tries.Add(mirrorUrl5);

            var mirrorUrl6 = RewriteUrlPrefix(officialInstallerUrl, "https://maven.neoforged.net", mb.TrimEnd('/'));
            if (!string.IsNullOrWhiteSpace(mirrorUrl6)) tries.Add(mirrorUrl6);
        }

        // SourceForge fallback
        tries.Add(SourceForgeDirectCdnUrl(loaderVersion));
        tries.Add(SourceForgeWebFileDownloadUrl(loaderVersion));
        tries.Add(SourceForgeLatestDownloadUrl);

        // официальный — только самым последним (для РФ критично)
        if (!string.IsNullOrWhiteSpace(officialInstallerUrl))
            tries.Add(officialInstallerUrl.Trim());

        tries = tries
            .Select(NormalizeAbsoluteUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Exception? last = null;

        foreach (var urlTry in tries)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!Uri.TryCreate(urlTry, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                {
                    throw new InvalidOperationException($"installerUrl is not a valid http(s) absolute url: {urlTry}");
                }

                var baseDir = _path.BasePath ?? "";
                if (string.IsNullOrWhiteSpace(baseDir))
                    throw new InvalidOperationException("MinecraftPath.BasePath пустой.");

                var cacheDir = Path.Combine(baseDir, "launcher", "installers", "neoforge", minecraftVersion, loaderVersion);
                Directory.CreateDirectory(cacheDir);

                var fileName = MakeInstallerFileName(uri, loaderVersion);
                var local = Path.Combine(cacheDir, fileName);

                // кеш
                if (File.Exists(local))
                {
                    try
                    {
                        var fi = new FileInfo(local);
                        if (fi.Length > 0 && fi.Length <= MaxInstallerBytes && LooksLikeJar(local))
                            return local;
                    }
                    catch { }
                }

                var tmp = local + ".tmp";
                TryDeleteQuiet(tmp);

                _log?.Invoke($"NeoForge: скачиваю installer: {urlTry}");

                await DownloadJarWithRetriesAsync(uri, tmp, ct).ConfigureAwait(false);

                if (!LooksLikeJar(tmp))
                    throw new InvalidOperationException("Скачанный файл не похож на JAR (нет сигнатуры ZIP 'PK').");

                var ok = await TryMoveOrReplaceWithRetryAsync(tmp, local, ct, attempts: 20, delayMs: 200)
                    .ConfigureAwait(false);

                TryDeleteQuiet(tmp);

                if (!ok)
                    throw new IOException("Не удалось сохранить installer.jar (файл занят/нет доступа).");

                return local;
            }
            catch (Exception ex)
            {
                last = ex;
                _log?.Invoke($"NeoForge: не удалось скачать installer ({urlTry}) — {ex.Message}");
            }
        }

        throw new InvalidOperationException("Не удалось скачать NeoForge installer ни по одному URL.", last);
    }

    private static string MakeInstallerFileName(Uri uri, string loaderVersion)
    {
        var fileName = Path.GetFileName(uri.AbsolutePath);

        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Equals("download", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            fileName = $"neoforge-{loaderVersion}-installer.jar";
        }

        foreach (var c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '_');

        if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            fileName += ".jar";

        return fileName;
    }

    private async Task DownloadJarWithRetriesAsync(Uri uri, string tmpPath, CancellationToken ct)
    {
        var isOfficial = IsOfficialNeoForgedHost(uri);
        var maxAttempts = isOfficial ? 1 : (DownloadRetryCount + 1);

        Exception? last = null;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var timeout = isOfficial ? OfficialMavenDownloadTimeout : DownloadTimeout;

                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(timeout);

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
                    var code = (int)resp.StatusCode;
                    var msg = $"HTTP {code} {resp.ReasonPhrase}";

                    // ретраи только не-официальным
                    if (!isOfficial && IsTransient(resp.StatusCode) && attempt < maxAttempts - 1)
                        throw new HttpRequestException(msg, null, resp.StatusCode);

                    resp.EnsureSuccessStatusCode();
                }

                var media = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (media.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Сервер вернул HTML вместо JAR (возможна блокировка/страница ошибки).");

                var len = resp.Content.Headers.ContentLength;
                if (len.HasValue && len.Value > MaxInstallerBytes)
                    throw new InvalidOperationException($"Installer слишком большой ({len.Value} bytes).");

                await using (var input = await resp.Content.ReadAsStreamAsync(reqCts.Token).ConfigureAwait(false))
                await using (var output = new FileStream(
                    tmpPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await CopyWithLimitAsync(input, output, MaxInstallerBytes, reqCts.Token).ConfigureAwait(false);
                    await output.FlushAsync(reqCts.Token).ConfigureAwait(false);
                }

                if (LooksLikeHtml(tmpPath))
                    throw new InvalidOperationException("Скачанный файл похож на HTML (страница ошибки/капча), а не на JAR.");

                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException("Таймаут скачивания installer.jar.");
            }
            catch (HttpRequestException ex) when (!isOfficial && attempt < maxAttempts - 1)
            {
                last = ex;
                var delay = TimeSpan.FromMilliseconds(400 + attempt * 700);
                _log?.Invoke($"NeoForge: transient-ошибка загрузки, повтор через {delay.TotalMilliseconds:0}ms...");
                TryDeleteQuiet(tmpPath);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!isOfficial && attempt < maxAttempts - 1)
            {
                last = ex;
                var delay = TimeSpan.FromMilliseconds(400 + attempt * 700);
                _log?.Invoke($"NeoForge: ошибка загрузки, повтор через {delay.TotalMilliseconds:0}ms...");
                TryDeleteQuiet(tmpPath);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Не удалось скачать installer.jar.", last);
    }

    private static bool IsOfficialNeoForgedHost(Uri uri)
        => uri.Host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(HttpStatusCode code)
    {
        var c = (int)code;
        return code == (HttpStatusCode)429 ||
               code == HttpStatusCode.RequestTimeout ||
               code == HttpStatusCode.BadGateway ||
               code == HttpStatusCode.ServiceUnavailable ||
               code == HttpStatusCode.GatewayTimeout ||
               (c >= 500 && c <= 599);
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
                ct.ThrowIfCancellationRequested();

                var read = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (read <= 0) break;

                total += read;
                if (total > maxBytes)
                    throw new InvalidOperationException($"Превышен лимит размера файла ({maxBytes} bytes).");

                await output.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    private static bool LooksLikeJar(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 2) return false;

            var b1 = fs.ReadByte();
            var b2 = fs.ReadByte();
            return b1 == 0x50 && b2 == 0x4B; // 'P''K'
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeHtml(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = (int)Math.Min(4096, fs.Length);
            if (len <= 0) return false;

            byte[]? buf = null;
            try
            {
                buf = ArrayPool<byte>.Shared.Rent(len);
                var read = fs.Read(buf, 0, len);
                if (read <= 0) return false;

                var head = Encoding.UTF8.GetString(buf, 0, read).TrimStart();

                return head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
                       head.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                       head.Contains("<title>", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (buf is not null) ArrayPool<byte>.Shared.Return(buf, clearArray: false);
            }
        }
        catch
        {
            return false;
        }
    }

    // =========================
    // Install NeoForge
    // =========================

    private async Task<string?> InstallNeoForgeIntoGameDirAsync(
        string installerPath,
        string minecraftVersion,
        string loaderVersion,
        string expectedId,
        CancellationToken ct)
    {
        var baseDir = _path.BasePath ?? "";
        if (string.IsNullOrWhiteSpace(baseDir))
            throw new InvalidOperationException("MinecraftPath.BasePath пустой.");

        Directory.CreateDirectory(Path.Combine(baseDir, "versions"));

        var javaExe = FindJavaExecutable();

        // РФ-полировка: готовим mirrored installer.jar, чтобы сам installer не зависал на maven.neoforged.net
        var execInstallerPath = await PrepareInstallerJarForExecutionAsync(installerPath, loaderVersion, ct)
            .ConfigureAwait(false);

        var before = SnapshotVersionIds(baseDir);

        // installDir может не поддерживаться. Пробуем разные варианты аргументов.
        var argTries = new List<string[]>
        {
            new[] { "-jar", execInstallerPath, "--installClient", "--installDir", baseDir },
            new[] { "-jar", execInstallerPath, "--installClient", "--install-dir", baseDir },
            new[] { "-jar", execInstallerPath, "--install-client", "--installDir", baseDir },
            new[] { "-jar", execInstallerPath, "--install-client", "--install-dir", baseDir },
        };

        foreach (var args in argTries)
        {
            _log?.Invoke($"NeoForge: запускаю installer (installDir): {javaExe} {string.Join(" ", args)}");

            var res = await RunJavaStreamingAsync(
                javaExe: javaExe,
                args: args,
                workingDir: baseDir,
                ct: ct,
                env: null,
                overallTimeout: InstallOverallTimeout,
                stallTimeout: InstallStallTimeout).ConfigureAwait(false);

            if (res.ExitCode == 0)
            {
                if (IsVersionPresent(expectedId))
                    return expectedId;

                var after = SnapshotVersionIds(baseDir);
                var created = after.Except(before, StringComparer.OrdinalIgnoreCase).ToList();

                var picked = PickNeoForgeVersionId(created, loaderVersion);
                if (!string.IsNullOrWhiteSpace(picked))
                    return picked;

                return FindNeoForgeVersionIdInBase(baseDir, loaderVersion);
            }

            // если опция не распознана — не крутим дальше, сразу fallback
            if (LooksLikeUnrecognizedOption(res.StdErr) || LooksLikeUnrecognizedOption(res.StdOut))
                break;
        }

        // fallback: installer иногда пишет в %APPDATA%\.minecraft
        var tempAppData = Path.Combine(baseDir, "launcher", "tmp", "appdata");
        var tempMc = Path.Combine(tempAppData, ".minecraft");

        try
        {
            Directory.CreateDirectory(tempMc);
            EnsureLauncherProfileStub(tempMc);

            var beforeTemp = SnapshotVersionIds(tempMc);

            _log?.Invoke("NeoForge: installer требует .minecraft, ставлю во временный APPDATA и переношу в gameDir...");

            var env = new Dictionary<string, string>
            {
                ["APPDATA"] = tempAppData,
                ["LOCALAPPDATA"] = tempAppData
            };

            _log?.Invoke("NeoForge: запускаю installer (APPDATA fallback): java -jar <installer> --installClient");

            var res2 = await RunJavaStreamingAsync(
                javaExe: javaExe,
                args: new[] { "-jar", execInstallerPath, "--installClient" },
                workingDir: baseDir,
                ct: ct,
                env: env,
                overallTimeout: InstallOverallTimeout,
                stallTimeout: InstallStallTimeout).ConfigureAwait(false);

            if (res2.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"NeoForge installer завершился с ошибкой (code {res2.ExitCode}).\n" +
                    $"{(string.IsNullOrWhiteSpace(res2.StdErr) ? res2.StdOut : res2.StdErr)}");
            }

            var afterTemp = SnapshotVersionIds(tempMc);
            var createdTemp = afterTemp.Except(beforeTemp, StringComparer.OrdinalIgnoreCase).ToList();

            var tempPicked =
                PickNeoForgeVersionId(createdTemp, loaderVersion)
                ?? FindNeoForgeVersionIdInBase(tempMc, loaderVersion);

            MergeDir(Path.Combine(tempMc, "versions"), Path.Combine(baseDir, "versions"));
            MergeDir(Path.Combine(tempMc, "libraries"), Path.Combine(baseDir, "libraries"));
            MergeDir(Path.Combine(tempMc, "assets"), Path.Combine(baseDir, "assets"));

            if (!string.IsNullOrWhiteSpace(tempPicked) && IsVersionPresent(tempPicked!))
                return tempPicked;

            if (IsVersionPresent(expectedId))
                return expectedId;

            return FindNeoForgeVersionIdInBase(baseDir, loaderVersion);
        }
        finally
        {
            TryDeleteDirQuiet(tempAppData);
        }
    }

    private static HashSet<string> SnapshotVersionIds(string baseDir)
    {
        var versionsDir = Path.Combine(baseDir, "versions");
        if (!Directory.Exists(versionsDir))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return Directory.EnumerateDirectories(versionsDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? PickNeoForgeVersionId(List<string> candidates, string loaderVersion)
    {
        loaderVersion = (loaderVersion ?? "").Trim();

        foreach (var id in candidates)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;

            if (id.Contains("neoforge", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(loaderVersion) || id.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase)))
                return id;
        }

        return null;
    }

    private static string? FindNeoForgeVersionIdInBase(string baseDir, string loaderVersion)
    {
        loaderVersion = (loaderVersion ?? "").Trim();
        var versionsDir = Path.Combine(baseDir, "versions");
        if (!Directory.Exists(versionsDir))
            return null;

        foreach (var dir in Directory.EnumerateDirectories(versionsDir))
        {
            var id = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (id.Contains("neoforge", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(loaderVersion) || id.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase)))
                return id;
        }

        return null;
    }

    private static bool LooksLikeUnrecognizedOption(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;

        return s.Contains("UnrecognizedOptionException", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("is not a recognized option", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("Unknown option", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("Unknown argument", StringComparison.OrdinalIgnoreCase);
    }

    private string FindJavaExecutable()
    {
        var baseDir = _path.BasePath ?? "";

        // 1) runtime внутри gameDir
        var runtimeDir = Path.Combine(baseDir, "runtime");
        if (Directory.Exists(runtimeDir))
        {
            var javaExe = Directory.EnumerateFiles(runtimeDir, "java.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(javaExe)) return javaExe!;

            var javaw = Directory.EnumerateFiles(runtimeDir, "javaw.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(javaw)) return javaw!;

            var java = Directory.EnumerateFiles(runtimeDir, "java", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(java)) return java!;
        }

        // 2) JAVA_HOME
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var p1 = Path.Combine(javaHome!, "bin", "java.exe");
            if (File.Exists(p1)) return p1;

            var p2 = Path.Combine(javaHome!, "bin", "java");
            if (File.Exists(p2)) return p2;
        }

        // 3) PATH
        return "java";
    }

    private static void MergeDir(string src, string dst)
    {
        if (!Directory.Exists(src))
            return;

        Directory.CreateDirectory(dst);

        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }

        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            CopyFileIfDifferent(file, target);
        }
    }

    private static void CopyFileIfDifferent(string src, string dst)
    {
        try
        {
            if (File.Exists(dst))
            {
                var a = new FileInfo(src);
                var b = new FileInfo(dst);
                if (a.Length == b.Length)
                    return;
            }

            File.Copy(src, dst, overwrite: true);
        }
        catch
        {
            // не роняем установку из-за единичного файла
        }
    }

    private static void EnsureLauncherProfileStub(string mcDir)
    {
        try
        {
            Directory.CreateDirectory(mcDir);

            var stub = new
            {
                profiles = new Dictionary<string, object>(),
                settings = new Dictionary<string, object>(),
                selectedProfile = "",
                authenticationDatabase = new Dictionary<string, object>(),
                launcherVersion = new { name = "LegendBorn", format = 21 }
            };

            var json = JsonSerializer.Serialize(stub, new JsonSerializerOptions { WriteIndented = true });

            var p1 = Path.Combine(mcDir, "launcher_profiles.json");
            if (!File.Exists(p1))
                File.WriteAllText(p1, json);

            var p2 = Path.Combine(mcDir, "launcher_profiles_microsoft_store.json");
            if (!File.Exists(p2))
                File.WriteAllText(p2, json);
        }
        catch { }
    }

    // =========================
    // Patch installer.jar to mirror (critical for РФ)
    // =========================

    private async Task<string> PrepareInstallerJarForExecutionAsync(string installerPath, string loaderVersion, CancellationToken ct)
    {
        try
        {
            var preferred = await GetPreferredMirrorAsync(loaderVersion, ct).ConfigureAwait(false);
            if (!preferred.HasValue)
                return installerPath;

            var mirrorBase = NormalizeAbsoluteBaseUrl(preferred.Value.MirrorBase);
            var releasesRoot = NormalizeAbsoluteBaseUrl(preferred.Value.ReleasesRoot);

            if (string.IsNullOrWhiteSpace(mirrorBase) || string.IsNullOrWhiteSpace(releasesRoot))
                return installerPath;

            if (!JarLikelyNeedsMavenPatch(installerPath))
                return installerPath;

            var dir = Path.GetDirectoryName(installerPath) ?? "";
            var file = Path.GetFileNameWithoutExtension(installerPath);
            var patchedPath = Path.Combine(dir, file + ".mirrored.jar");

            // кеш патченного jar
            try
            {
                if (File.Exists(patchedPath))
                {
                    var srcInfo = new FileInfo(installerPath);
                    var dstInfo = new FileInfo(patchedPath);
                    if (dstInfo.Length > 0 &&
                        dstInfo.LastWriteTimeUtc >= srcInfo.LastWriteTimeUtc &&
                        LooksLikeJar(patchedPath))
                        return patchedPath;
                }
            }
            catch { }

            _log?.Invoke($"NeoForge: патчу installer.jar под зеркало (чтобы не зависать на maven.neoforged.net): {releasesRoot}");

            var tmp = patchedPath + ".tmp";
            TryDeleteQuiet(tmp);

            await CreateMirroredInstallerJarAsync(installerPath, tmp, mirrorBase, releasesRoot, ct).ConfigureAwait(false);

            if (!LooksLikeJar(tmp))
                throw new InvalidOperationException("Mirrored installer.jar получился невалидным (не ZIP/JAR).");

            var ok = await TryMoveOrReplaceWithRetryAsync(tmp, patchedPath, ct, attempts: 20, delayMs: 150)
                .ConfigureAwait(false);

            TryDeleteQuiet(tmp);

            if (ok && File.Exists(patchedPath))
                return patchedPath;

            return installerPath;
        }
        catch (Exception ex)
        {
            _log?.Invoke("NeoForge: не удалось подготовить mirrored installer.jar, запускаю оригинал. " + ex.Message);
            return installerPath;
        }
    }

    private static bool JarLikelyNeedsMavenPatch(string installerPath)
    {
        try
        {
            using var fs = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

            var entry = zip.GetEntry("install_profile.json") ??
                        zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("install_profile.json", StringComparison.OrdinalIgnoreCase));

            if (entry is null) return false;
            if (entry.Length <= 0 || entry.Length > JarPatchMaxTextEntryBytes) return true;

            using var s = entry.Open();
            using var sr = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = sr.ReadToEnd();
            return text.Contains("maven.neoforged.net", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static bool IsSignatureEntry(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return false;
        if (!fullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) return false;

        return fullName.EndsWith(".SF", StringComparison.OrdinalIgnoreCase)
               || fullName.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase)
               || fullName.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase)
               || fullName.EndsWith(".EC", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains("/SIG-", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains("\\SIG-", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CreateMirroredInstallerJarAsync(
        string sourceJarPath,
        string destJarPath,
        string mirrorBase,
        string releasesRoot,
        CancellationToken ct)
    {
        await using var srcFs = new FileStream(sourceJarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var srcZip = new ZipArchive(srcFs, ZipArchiveMode.Read);

        await using var dstFs = new FileStream(destJarPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var dstZip = new ZipArchive(dstFs, ZipArchiveMode.Create);

        foreach (var e in srcZip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (IsSignatureEntry(e.FullName))
                continue;

            if (e.FullName.EndsWith("/", StringComparison.Ordinal))
                continue;

            // speed > size: это исполняемый jar, не архив для хранения
            var dstEntry = dstZip.CreateEntry(e.FullName, CompressionLevel.Fastest);
            dstEntry.LastWriteTime = e.LastWriteTime;

            if (e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && e.Length > 0 && e.Length <= JarPatchMaxTextEntryBytes)
            {
                string? patched = null;

                try
                {
                    using var srcStream = e.Open();
                    using var sr = new StreamReader(srcStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    var text = sr.ReadToEnd();

                    if (text.Contains("maven.neoforged.net", StringComparison.OrdinalIgnoreCase))
                        patched = RewriteAllOfficialUrls(text, mirrorBase, releasesRoot);
                }
                catch
                {
                    patched = null;
                }

                if (patched is not null)
                {
                    await using var outStream = dstEntry.Open();
                    await using var sw = new StreamWriter(outStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    await sw.WriteAsync(patched).ConfigureAwait(false);
                    await sw.FlushAsync().ConfigureAwait(false);
                    continue;
                }
            }

            await using (var src = e.Open())
            await using (var dst = dstEntry.Open())
            {
                await src.CopyToAsync(dst, 128 * 1024, ct).ConfigureAwait(false);
            }
        }
    }

    private static string RewriteAllOfficialUrls(string text, string mirrorBase, string releasesRoot)
    {
        mirrorBase = NormalizeAbsoluteBaseUrl(mirrorBase);
        releasesRoot = NormalizeAbsoluteBaseUrl(releasesRoot);

        text = text.Replace("https://maven.neoforged.net/releases/", releasesRoot, StringComparison.OrdinalIgnoreCase);
        text = text.Replace("http://maven.neoforged.net/releases/", releasesRoot, StringComparison.OrdinalIgnoreCase);

        text = text.Replace("https://maven.neoforged.net/", mirrorBase, StringComparison.OrdinalIgnoreCase);
        text = text.Replace("http://maven.neoforged.net/", mirrorBase, StringComparison.OrdinalIgnoreCase);

        text = text.Replace("https://maven.neoforged.net", mirrorBase.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        text = text.Replace("http://maven.neoforged.net", mirrorBase.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        return text;
    }

    // =========================
    // Process runner with real-time logs + stall timeout
    // =========================

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

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        if (env is not null)
        {
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var sync = new object();

        long lastOutputMs = Environment.TickCount64;

        void Touch() => Interlocked.Exchange(ref lastOutputMs, Environment.TickCount64);

        static void AppendCapped(StringBuilder sb, string line)
        {
            if (sb.Length >= ProcessOutputCapChars)
                return;

            if (sb.Length + line.Length + 1 > ProcessOutputCapChars)
            {
                var remaining = ProcessOutputCapChars - sb.Length - 1;
                if (remaining > 0)
                    sb.Append(line.AsSpan(0, Math.Min(remaining, line.Length)));
                sb.AppendLine();
                return;
            }

            sb.AppendLine(line);
        }

        using var p = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (sync) AppendCapped(stdout, e.Data);
            Touch();
            _log?.Invoke(e.Data);
        };

        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (sync) AppendCapped(stderr, e.Data);
            Touch();
            _log?.Invoke(e.Data);
        };

        if (!p.Start())
            throw new InvalidOperationException("Не удалось запустить java для NeoForge installer.");

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var heartbeatCts = new CancellationTokenSource();

        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (!heartbeatCts.IsCancellationRequested && !overallCts.IsCancellationRequested && !p.HasExited)
                {
                    await Task.Delay(InstallerHeartbeatEvery, heartbeatCts.Token).ConfigureAwait(false);
                    if (p.HasExited) break;

                    var silentFor = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref lastOutputMs));
                    if (silentFor >= InstallerHeartbeatEvery)
                        _log?.Invoke("NeoForge: installer всё ещё работает...");
                }
            }
            catch { }
        });

        try
        {
            while (!p.HasExited)
            {
                overallCts.Token.ThrowIfCancellationRequested();

                var silentFor = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref lastOutputMs));
                if (silentFor >= stallTimeout)
                {
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }

                    var mirrorHint = (_preferredMirror?.ReleasesRoot) ?? (_neoForgeMavenMirrors.FirstOrDefault() ?? "(зеркало не задано)");
                    throw new TimeoutException(
                        "NeoForge installer: нет вывода слишком долго (stall-timeout). " +
                        "Частая причина: блокируется загрузка зависимостей/антивирус/сеть. " +
                        $"Проверь доступ к Maven-зеркалу: {mirrorHint}");
                }

                await Task.Delay(250, overallCts.Token).ConfigureAwait(false);
            }

            try { p.WaitForExit(); } catch { }
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }

            if (ct.IsCancellationRequested)
                throw;

            throw new TimeoutException("NeoForge installer: превышен общий таймаут выполнения.");
        }
        finally
        {
            try { heartbeatCts.Cancel(); } catch { }
            try { await heartbeatTask.ConfigureAwait(false); } catch { }
        }

        string outText, errText;
        lock (sync)
        {
            outText = stdout.ToString().Trim();
            errText = stderr.ToString().Trim();
        }

        return (p.ExitCode, outText, errText);
    }

    // =========================
    // Patch version json URLs
    // =========================

    private async Task TryRewriteNeoForgeVersionJsonUrlsAsync(string versionId, string loaderVersion, CancellationToken ct)
    {
        if (!_rewriteNeoForgeUrlsToMirror)
            return;

        var baseDir = _path.BasePath ?? "";
        if (string.IsNullOrWhiteSpace(baseDir))
            return;

        var jsonPath = Path.Combine(baseDir, "versions", versionId, versionId + ".json");
        if (!File.Exists(jsonPath))
            return;

        var preferred = await GetPreferredMirrorAsync(loaderVersion, ct).ConfigureAwait(false);
        if (!preferred.HasValue)
        {
            _log?.Invoke("NeoForge: зеркало Maven не выбрано — оставляю ссылки как есть.");
            return;
        }

        var mirrorBase = NormalizeAbsoluteBaseUrl(preferred.Value.MirrorBase);
        var releasesRoot = NormalizeAbsoluteBaseUrl(preferred.Value.ReleasesRoot);

        if (string.IsNullOrWhiteSpace(mirrorBase) || string.IsNullOrWhiteSpace(releasesRoot))
            return;

        try
        {
            var text = File.ReadAllText(jsonPath);
            if (string.IsNullOrWhiteSpace(text))
                return;

            // важно: не парсим JSON вообще, если нет строки-мишени
            if (!text.Contains("maven.neoforged.net", StringComparison.OrdinalIgnoreCase))
                return;

            var node = JsonNode.Parse(text);
            if (node is null)
                return;

            var replaced = ReplaceStringsRecursive(node, s =>
            {
                var r = RewriteUrlPrefix(s, "https://maven.neoforged.net/releases/", releasesRoot);
                if (!string.IsNullOrWhiteSpace(r)) return r;

                r = RewriteUrlPrefix(s, OfficialNeoForgedMavenBase, mirrorBase);
                if (!string.IsNullOrWhiteSpace(r)) return r;

                r = RewriteUrlPrefix(s, "https://maven.neoforged.net", mirrorBase.TrimEnd('/'));
                if (!string.IsNullOrWhiteSpace(r)) return r;

                r = RewriteUrlPrefix(s, "http://maven.neoforged.net/", mirrorBase);
                if (!string.IsNullOrWhiteSpace(r)) return r;

                return s;
            });

            if (!replaced)
                return;

            var tmp = jsonPath + ".tmp";
            var bak = jsonPath + ".bak";

            var output = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, output);

            try
            {
                TryDeleteQuiet(bak);
                File.Replace(tmp, jsonPath, bak, ignoreMetadataErrors: true);
            }
            finally
            {
                TryDeleteQuiet(tmp);
                TryDeleteQuiet(bak);
            }

            _log?.Invoke($"NeoForge: пропатчил URLs в {versionId}.json -> {releasesRoot}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"NeoForge: не удалось пропатчить version json ({versionId}) — {ex.Message}");
        }
    }

    // Важно: для выбора зеркала по КОНКРЕТНОМУ артефакту 404/403 считаем провалом.
    private async Task<bool> IsUrlReachableForArtifactAsync(string url, CancellationToken ct)
    {
        if (await ProbeArtifactAsync(HttpMethod.Head, url, ct).ConfigureAwait(false))
            return true;

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
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            TrySetUa(req);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            var code = (int)resp.StatusCode;
            return code < 400; // 2xx/3xx OK; 4xx/5xx fail for artifact probe
        }
        catch
        {
            return false;
        }
    }

    private static bool ReplaceStringsRecursive(JsonNode node, Func<string, string> replacer)
    {
        if (node is JsonObject obj)
        {
            bool any = false;

            foreach (var key in obj.Select(k => k.Key).ToList())
            {
                var child = obj[key];
                if (child is null) continue;

                if (child is JsonValue v && v.TryGetValue<string>(out var s) && s is not null)
                {
                    var ns = replacer(s);
                    if (!string.Equals(ns, s, StringComparison.Ordinal))
                    {
                        obj[key] = JsonValue.Create(ns);
                        any = true;
                    }
                    continue;
                }

                if (ReplaceStringsRecursive(child, replacer))
                    any = true;
            }

            return any;
        }

        if (node is JsonArray arr)
        {
            bool any = false;

            for (int i = 0; i < arr.Count; i++)
            {
                var child = arr[i];
                if (child is null) continue;

                if (child is JsonValue v && v.TryGetValue<string>(out var s) && s is not null)
                {
                    var ns = replacer(s);
                    if (!string.Equals(ns, s, StringComparison.Ordinal))
                    {
                        arr[i] = JsonValue.Create(ns);
                        any = true;
                    }
                    continue;
                }

                if (ReplaceStringsRecursive(child, replacer))
                    any = true;
            }

            return any;
        }

        return false;
    }

    // =========================
    // Utilities
    // =========================

    private static void TrySetUa(HttpRequestMessage req)
    {
        try
        {
            req.Headers.UserAgent.Clear();
            req.Headers.UserAgent.ParseAdd(LauncherUa);
        }
        catch
        {
            // ignore
        }
    }

    private static string? RewriteUrlPrefix(string input, string fromPrefix, string toPrefix)
    {
        input = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(input)) return null;

        fromPrefix = (fromPrefix ?? "").Trim();
        toPrefix = (toPrefix ?? "").Trim();

        if (string.IsNullOrWhiteSpace(fromPrefix) || string.IsNullOrWhiteSpace(toPrefix))
            return null;

        // поддерживаем сравнение и с "/", и без "/"
        var fromA = fromPrefix.EndsWith("/") ? fromPrefix : fromPrefix + "/";
        var toA = toPrefix.EndsWith("/") ? toPrefix : toPrefix + "/";

        if (input.StartsWith(fromA, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = input.Substring(fromA.Length);
            return toA + suffix;
        }

        var fromB = fromA.TrimEnd('/');
        var toB = toA.TrimEnd('/');

        if (input.StartsWith(fromB, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = input.Substring(fromB.Length);
            if (suffix.StartsWith("/")) suffix = suffix.Substring(1);
            return toB + "/" + suffix;
        }

        return null;
    }

    private static string NormalizeAbsoluteUrl(string? url)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url)) return "";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "";

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return "";

        var b = new UriBuilder(uri) { Query = "", Fragment = "" };
        return b.Uri.ToString();
    }

    private static string NormalizeAbsoluteBaseUrl(string? url)
    {
        url = NormalizeAbsoluteUrl(url);
        if (string.IsNullOrWhiteSpace(url)) return "";

        if (!url.EndsWith("/")) url += "/";
        return url;
    }

    private static async Task<bool> TryMoveOrReplaceWithRetryAsync(string source, string dest, CancellationToken ct, int attempts, int delayMs)
    {
        for (int i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                ReplaceOrMoveAtomic(source, dest);
                return true;
            }
            catch (IOException)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    private static void ReplaceOrMoveAtomic(string sourceTmp, string destPath)
    {
        if (OperatingSystem.IsWindows() && File.Exists(destPath))
        {
            var backup = destPath + ".bak";
            try
            {
                TryDeleteQuiet(backup);
                File.Replace(sourceTmp, destPath, backup, ignoreMetadataErrors: true);
            }
            finally
            {
                TryDeleteQuiet(backup);
            }

            return;
        }

        File.Move(sourceTmp, destPath, overwrite: true);
    }

    private static void TryDeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirQuiet(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    return;
                }
                catch
                {
                    Thread.Sleep(150);
                }
            }
        }
        catch { }
    }
}
