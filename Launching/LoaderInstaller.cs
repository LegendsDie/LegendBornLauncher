// LoaderInstaller.cs
using CmlLib.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

/// <summary>
/// Установщик NeoForge (installer.jar) в кастомный gameDir.
/// MinecraftService ставит базовую ваниль через CmlLib, а здесь — только запуск "java -jar neoforge-installer.jar ...".
/// ВАЖНО: в РФ maven.neoforged.net часто недоступен, поэтому:
/// 1) скачиваем installer через зеркало (legendborn.ru/maven) и/или SourceForge fallback;
/// 2) после установки патчим versions/<id>/<id>.json, заменяя ссылки Maven на наше зеркало (legendborn.ru/maven/).
/// </summary>
public sealed class LoaderInstaller
{
    // =========================
    // Maven / Mirrors
    // =========================

    public const string OfficialNeoForgedMavenBase = "https://maven.neoforged.net/";

    public static readonly string[] DefaultNeoForgeMavenMirrors =
    {
        "https://legendborn.ru/maven/",
    };

    // =========================
    // SourceForge (installer.jar only)
    // =========================

    public const string SourceForgeProjectSlug = "legendborn-neoforge";

    // Рабочий вариант (у тебя): latest/download
    private static string SourceForgeLatestDownloadUrl =>
        $"https://sourceforge.net/projects/{SourceForgeProjectSlug}/files/latest/download";

    // Direct CDN (часто стабильнее редиректов SourceForge)
    // ВАЖНО: у тебя файл лежит в /neoforge/ (без подпапки версии)
    private static string SourceForgeDirectCdnUrl(string loaderVersion) =>
        $"https://downloads.sourceforge.net/project/{SourceForgeProjectSlug}/neoforge/neoforge-{loaderVersion}-installer.jar";

    // Web redirect к конкретному файлу (на случай, если direct CDN режут)
    private static string SourceForgeWebFileDownloadUrl(string loaderVersion) =>
        $"https://sourceforge.net/projects/{SourceForgeProjectSlug}/files/neoforge/neoforge-{loaderVersion}-installer.jar/download";

    // =========================
    // Limits / Timeouts
    // =========================

    private const long MaxInstallerBytes = 100L * 1024 * 1024; // 100 MB safety

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);

    // общий таймаут на установку (installer может тянуть зависимости долго)
    private static readonly TimeSpan InstallOverallTimeout = TimeSpan.FromMinutes(25);

    // если installer не пишет в stdout/stderr слишком долго — считаем зависанием
    private static readonly TimeSpan InstallStallTimeout = TimeSpan.FromMinutes(2);

    // как часто писать "installer всё ещё работает..."
    private static readonly TimeSpan InstallerHeartbeatEvery = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan MirrorProbeTimeout = TimeSpan.FromSeconds(2);

    // =========================
    // Fields
    // =========================

    private readonly MinecraftPath _path;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    private readonly string[] _neoForgeMavenMirrors;
    private readonly bool _rewriteNeoForgeUrlsToMirror;

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
            throw new NotSupportedException($"Loader '{loaderType}' не поддерживается. В 0.2.2 поддерживается только NeoForge.");

        if (string.IsNullOrWhiteSpace(loaderVersion))
            throw new InvalidOperationException("NeoForge требует версию (loader.version).");

        var officialInstallerUrl = GetOfficialNeoForgeInstallerUrl(loaderVersion);
        if (string.IsNullOrWhiteSpace(installerUrl))
            installerUrl = officialInstallerUrl;

        if (string.IsNullOrWhiteSpace(installerUrl))
            throw new InvalidOperationException("NeoForge требует installerUrl (или должна строиться официальная ссылка).");

        var expectedId = GetExpectedNeoForgeVersionId(minecraftVersion, loaderVersion);

        if (IsVersionPresent(expectedId))
        {
            _log?.Invoke($"NeoForge: уже установлен -> {expectedId}");
            await TryRewriteNeoForgeVersionJsonUrlsAsync(expectedId, ct).ConfigureAwait(false);
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

        await TryRewriteNeoForgeVersionJsonUrlsAsync(installedId!, ct).ConfigureAwait(false);
        return installedId!;
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

        // 1) primary from config/servers.json
        if (!string.IsNullOrWhiteSpace(primaryInstallerUrl))
            tries.Add(primaryInstallerUrl.Trim());

        // 2) legendborn mirror: если ссылка указывает на официальный домен — подменяем на mirror base
        foreach (var mirrorBase in _neoForgeMavenMirrors)
        {
            var mirrorUrl1 = RewriteUrlPrefix(primaryInstallerUrl, OfficialNeoForgedMavenBase, mirrorBase);
            if (!string.IsNullOrWhiteSpace(mirrorUrl1))
                tries.Add(mirrorUrl1);

            var mirrorUrl2 = RewriteUrlPrefix(officialInstallerUrl, OfficialNeoForgedMavenBase, mirrorBase);
            if (!string.IsNullOrWhiteSpace(mirrorUrl2))
                tries.Add(mirrorUrl2);
        }

        // 3) SourceForge fallback (installer.jar only)
        // ВАЖНО: у тебя работает latest/download — добавляем его обязательно
        tries.Add(SourceForgeDirectCdnUrl(loaderVersion));
        tries.Add(SourceForgeWebFileDownloadUrl(loaderVersion));
        tries.Add(SourceForgeLatestDownloadUrl);

        // 4) официальный в самом конце
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

                // SourceForge latest/download даёт "download" в конце — имя файла надо нормализовать
                var fileName = Path.GetFileName(uri.AbsolutePath);
                if (string.IsNullOrWhiteSpace(fileName) ||
                    fileName.Equals("download", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("latest", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = $"neoforge-{loaderVersion}-installer.jar";
                }

                var local = Path.Combine(cacheDir, fileName);

                if (File.Exists(local) && new FileInfo(local).Length > 0 && LooksLikeJar(local))
                    return local;

                var tmp = local + ".tmp";
                TryDeleteQuiet(tmp);

                _log?.Invoke($"NeoForge: скачиваю installer: {urlTry}");

                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(DownloadTimeout);

                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/java-archive"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

                // SourceForge часто стабильнее с User-Agent
                req.Headers.UserAgent.ParseAdd("LegendBornLauncher/0.2.2 (+https://legendborn.ru)");

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token)
                    .ConfigureAwait(false);

                resp.EnsureSuccessStatusCode();

                var len = resp.Content.Headers.ContentLength;
                if (len.HasValue && len.Value > MaxInstallerBytes)
                    throw new InvalidOperationException($"Installer слишком большой ({len.Value} bytes).");

                await using (var input = await resp.Content.ReadAsStreamAsync(reqCts.Token).ConfigureAwait(false))
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
                {
                    await CopyWithLimitAsync(input, output, MaxInstallerBytes, reqCts.Token).ConfigureAwait(false);
                    await output.FlushAsync(reqCts.Token).ConfigureAwait(false);
                }

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

    private static async Task CopyWithLimitAsync(Stream input, Stream output, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
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

    private static bool LooksLikeJar(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 4) return false;

            var b1 = fs.ReadByte();
            var b2 = fs.ReadByte();
            return b1 == 0x50 && b2 == 0x4B; // 'P''K'
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
        var before = SnapshotVersionIds(baseDir);

        // installDir может не поддерживаться (ты это видишь в логах). Пробуем, затем fallback.
        var argTries = new List<string[]>
        {
            new[] { "-jar", installerPath, "--installClient", "--installDir", baseDir },
            new[] { "-jar", installerPath, "--installClient", "--install-dir", baseDir },
        };

        foreach (var args in argTries)
        {
            _log?.Invoke($"NeoForge: запускаю installer (installDir): java {string.Join(" ", args)}");

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

                // даже если не нашли — попробуем поиск по папкам
                return FindNeoForgeVersionIdInBase(baseDir, loaderVersion);
            }

            // если опция не распознана — не крутим дальше, сразу fallback в APPDATA
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
                args: new[] { "-jar", installerPath, "--installClient" },
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
            try
            {
                if (Directory.Exists(tempAppData))
                    Directory.Delete(tempAppData, recursive: true);
            }
            catch { }
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
               s.Contains("Unknown option", StringComparison.OrdinalIgnoreCase);
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

        // 3) fallback: PATH
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

        var lastOutputUtc = DateTime.UtcNow;

        using var p = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (sync) stdout.AppendLine(e.Data);
            lastOutputUtc = DateTime.UtcNow;
            _log?.Invoke(e.Data);
        };

        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (sync) stderr.AppendLine(e.Data);
            lastOutputUtc = DateTime.UtcNow;
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

                    var silentFor = DateTime.UtcNow - lastOutputUtc;

                    // Heartbeat (чтобы UI не выглядел "замершим")
                    if (silentFor >= InstallerHeartbeatEvery)
                        _log?.Invoke("NeoForge: installer всё ещё работает...");

                    // Stall timeout (реально завис/упёрся в блокировки/антивирус/сеть)
                    if (silentFor >= stallTimeout)
                    {
                        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
            catch { /* ignore */ }
        });

        try
        {
            while (!p.HasExited)
            {
                overallCts.Token.ThrowIfCancellationRequested();

                var silentFor = DateTime.UtcNow - lastOutputUtc;
                if (silentFor >= stallTimeout)
                {
                    throw new TimeoutException(
                        "NeoForge installer: нет вывода слишком долго (stall-timeout). " +
                        "Частая причина: блокируется загрузка зависимостей. " +
                        "Проверь доступ к legendborn.ru/maven/ у пользователя.");
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

        var outText = stdout.ToString().Trim();
        var errText = stderr.ToString().Trim();

        return (p.ExitCode, outText, errText);
    }

    // =========================
    // Patch version json URLs
    // =========================

    private async Task TryRewriteNeoForgeVersionJsonUrlsAsync(string versionId, CancellationToken ct)
    {
        if (!_rewriteNeoForgeUrlsToMirror)
            return;

        var baseDir = _path.BasePath ?? "";
        if (string.IsNullOrWhiteSpace(baseDir))
            return;

        var jsonPath = Path.Combine(baseDir, "versions", versionId, versionId + ".json");
        if (!File.Exists(jsonPath))
            return;

        var mirror = await PickFirstReachableMirrorAsync(_neoForgeMavenMirrors, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(mirror))
        {
            _log?.Invoke("NeoForge: зеркало Maven не доступно/не задано — оставляю ссылки как есть.");
            return;
        }

        try
        {
            var text = File.ReadAllText(jsonPath);
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (!text.Contains(OfficialNeoForgedMavenBase, StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("maven.neoforged.net", StringComparison.OrdinalIgnoreCase))
                return;

            var node = JsonNode.Parse(text);
            if (node is null)
                return;

            var replaced = ReplaceStringsRecursive(node, s =>
            {
                var r = RewriteUrlPrefix(s, OfficialNeoForgedMavenBase, mirror);
                if (!string.IsNullOrWhiteSpace(r))
                    return r;

                r = RewriteUrlPrefix(s, "https://maven.neoforged.net", mirror.TrimEnd('/'));
                if (!string.IsNullOrWhiteSpace(r))
                    return r;

                r = RewriteUrlPrefix(s, "http://maven.neoforged.net/", mirror);
                if (!string.IsNullOrWhiteSpace(r))
                    return r;

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

            _log?.Invoke($"NeoForge: пропатчил URLs в {versionId}.json -> {mirror}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"NeoForge: не удалось пропатчить version json ({versionId}) — {ex.Message}");
        }
    }

    private async Task<string?> PickFirstReachableMirrorAsync(string[] mirrors, CancellationToken ct)
    {
        if (mirrors is null || mirrors.Length == 0)
            return null;

        foreach (var m in mirrors)
        {
            ct.ThrowIfCancellationRequested();

            var url = NormalizeAbsoluteBaseUrl(m);
            if (string.IsNullOrWhiteSpace(url))
                continue;

            if (await IsUrlReachableAsync(url, ct).ConfigureAwait(false))
                return url;
        }

        return null;
    }

    private async Task<bool> IsUrlReachableAsync(string url, CancellationToken ct)
    {
        if (await ProbeAsync(HttpMethod.Head, url, ct).ConfigureAwait(false))
            return true;

        return await ProbeAsync(HttpMethod.Get, url, ct).ConfigureAwait(false);
    }

    private async Task<bool> ProbeAsync(HttpMethod method, string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(MirrorProbeTimeout);

            using var req = new HttpRequestMessage(method, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            req.Headers.UserAgent.ParseAdd("LegendBornLauncher/0.2.2 (+https://legendborn.ru)");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            var code = (int)resp.StatusCode;
            return code < 500;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Рекурсивно заменяет строковые значения в JsonNode.
    /// JsonValue нельзя SetValue — нужно заменять узел в родителе (JsonObject/JsonArray).
    /// </summary>
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

    private static string? RewriteUrlPrefix(string input, string fromPrefix, string toPrefix)
    {
        input = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(input)) return null;

        fromPrefix = (fromPrefix ?? "").Trim();
        toPrefix = (toPrefix ?? "").Trim();

        if (string.IsNullOrWhiteSpace(fromPrefix) || string.IsNullOrWhiteSpace(toPrefix))
            return null;

        if (!fromPrefix.EndsWith("/")) fromPrefix += "/";
        if (!toPrefix.EndsWith("/")) toPrefix += "/";

        if (input.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = input.Substring(fromPrefix.Length);
            return toPrefix + suffix;
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

        return uri.ToString();
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
}
