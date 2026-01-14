// LoaderInstaller.cs
using CmlLib.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

/// <summary>
/// Установщик NeoForge (installer.jar) в кастомный gameDir.
/// MinecraftService ставит базовую ваниль через CmlLib, а здесь — только запуск "java -jar neoforge-installer.jar ...".
/// </summary>
public sealed class LoaderInstaller
{
    private const long MaxInstallerBytes = 100L * 1024 * 1024; // 100 MB safety
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(6);

    private readonly MinecraftPath _path;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    public LoaderInstaller(MinecraftPath path, HttpClient http, Action<string>? log = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log;
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

        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException("minecraftVersion is required", nameof(minecraftVersion));

        // Vanilla — ничего не делаем
        if (loaderType == "vanilla")
            return minecraftVersion;

        // В 0.2.2 поддерживаем только NeoForge
        if (loaderType != "neoforge")
            throw new NotSupportedException($"Loader '{loaderType}' не поддерживается. В 0.2.2 поддерживается только NeoForge.");

        if (string.IsNullOrWhiteSpace(loaderVersion))
            throw new InvalidOperationException("NeoForge требует версию (loader.version).");

        // installerUrl может прийти пустым — тогда используем официальный
        var official = GetOfficialNeoForgeInstallerUrl(loaderVersion);
        if (string.IsNullOrWhiteSpace(installerUrl))
            installerUrl = official;

        if (string.IsNullOrWhiteSpace(installerUrl))
            throw new InvalidOperationException("NeoForge требует installerUrl (или должна строиться официальная ссылка).");

        var expectedId = GetExpectedNeoForgeVersionId(minecraftVersion, loaderVersion);

        if (IsVersionPresent(expectedId))
        {
            _log?.Invoke($"NeoForge: уже установлен -> {expectedId}");
            return expectedId;
        }

        var installerPath = await DownloadInstallerAsync(
            minecraftVersion: minecraftVersion,
            loaderVersion: loaderVersion,
            primaryInstallerUrl: installerUrl,
            officialInstallerUrl: official,
            ct: ct).ConfigureAwait(false);

        var installedId = await InstallNeoForgeIntoGameDirAsync(
            installerPath: installerPath,
            minecraftVersion: minecraftVersion,
            loaderVersion: loaderVersion,
            expectedId: expectedId,
            ct: ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(installedId))
            throw new InvalidOperationException("NeoForge installer отработал, но версия лоадера не найдена в versions/.");

        return installedId!;
    }

    private static string NormalizeLoaderType(string? loaderType)
    {
        var t = (loaderType ?? "vanilla").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(t)) return "vanilla";
        // допускаем "NeoForge" и любые регистры
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

        if (!string.IsNullOrWhiteSpace(primaryInstallerUrl))
            tries.Add(primaryInstallerUrl.Trim());

        if (!string.IsNullOrWhiteSpace(officialInstallerUrl) &&
            !tries.Any(x => x.Equals(officialInstallerUrl, StringComparison.OrdinalIgnoreCase)))
        {
            tries.Add(officialInstallerUrl.Trim());
        }

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

                var cacheDir = Path.Combine(_path.BasePath, "launcher", "installers", "neoforge", minecraftVersion, loaderVersion);
                Directory.CreateDirectory(cacheDir);

                var fileName = Path.GetFileName(uri.AbsolutePath);
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = $"neoforge-{loaderVersion}-installer.jar";

                var local = Path.Combine(cacheDir, fileName);

                // если файл уже есть и выглядит как jar — используем
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

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token)
                    .ConfigureAwait(false);

                resp.EnsureSuccessStatusCode();

                var len = resp.Content.Headers.ContentLength;
                if (len.HasValue && len.Value > MaxInstallerBytes)
                    throw new InvalidOperationException($"Installer слишком большой ({len.Value} bytes).");

                await using (var input = await resp.Content.ReadAsStreamAsync(reqCts.Token).ConfigureAwait(false))
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
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

            // JAR = ZIP => первые байты: 0x50 0x4B ('P''K')
            var b1 = fs.ReadByte();
            var b2 = fs.ReadByte();
            return b1 == 0x50 && b2 == 0x4B;
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

        // Сначала пробуем "нормальные" аргументы с installDir
        var argTries = new List<string[]>
        {
            new[] { "-jar", installerPath, "--installClient", "--installDir", baseDir },
            new[] { "-jar", installerPath, "--installClient", "--install-dir", baseDir },
        };

        foreach (var args in argTries)
        {
            var res = await RunJavaAsync(
                javaExe: javaExe,
                args: args,
                workingDir: baseDir,
                ct: ct,
                env: null,
                timeout: InstallTimeout).ConfigureAwait(false);

            if (res.ExitCode == 0)
            {
                if (IsVersionPresent(expectedId))
                    return expectedId;

                var after = SnapshotVersionIds(baseDir);
                var created = after.Except(before, StringComparer.OrdinalIgnoreCase).ToList();

                var picked = PickNeoForgeVersionId(created, loaderVersion);
                if (!string.IsNullOrWhiteSpace(picked))
                    return picked;
            }

            if (LooksLikeUnrecognizedOption(res.StdErr) || LooksLikeUnrecognizedOption(res.StdOut))
                continue;
        }

        // Если installDir не сработал — fallback: installer часто пишет в %APPDATA%\.minecraft
        // Мы подменим APPDATA на папку внутри gameDir и потом перенесём нужные данные.
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

            var res2 = await RunJavaAsync(
                javaExe: javaExe,
                args: new[] { "-jar", installerPath, "--installClient" },
                workingDir: baseDir,
                ct: ct,
                env: env,
                timeout: InstallTimeout).ConfigureAwait(false);

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

            // переносим только то, что реально может понадобиться клиенту
            MergeDir(Path.Combine(tempMc, "versions"), Path.Combine(baseDir, "versions"));
            MergeDir(Path.Combine(tempMc, "libraries"), Path.Combine(baseDir, "libraries"));
            MergeDir(Path.Combine(tempMc, "assets"), Path.Combine(baseDir, "assets"));

            if (!string.IsNullOrWhiteSpace(tempPicked) && IsVersionPresent(tempPicked!))
                return tempPicked;

            if (IsVersionPresent(expectedId))
                return expectedId;

            // последний шанс: просто поиск в baseDir
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

            // чаще всего: "1.21.1-neoforge-21.1.216"
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

        // 1) runtime внутри gameDir (если ты её раскладываешь сам)
        var runtimeDir = Path.Combine(baseDir, "runtime");
        if (Directory.Exists(runtimeDir))
        {
            // предпочитаем java.exe
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

            File.Copy(file, target, overwrite: true);
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

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunJavaAsync(
        string javaExe,
        IEnumerable<string> args,
        string workingDir,
        CancellationToken ct,
        IDictionary<string, string>? env,
        TimeSpan timeout)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

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

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить java для NeoForge installer.");

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        try
        {
            await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch { }

            // если отменили внешним ct — пробрасываем
            if (ct.IsCancellationRequested)
                throw;

            // иначе это наш timeout
            throw new TimeoutException("NeoForge installer: превышен таймаут выполнения.");
        }

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

        if (!string.IsNullOrWhiteSpace(stdout))
            _log?.Invoke(stdout);

        if (!string.IsNullOrWhiteSpace(stderr))
            _log?.Invoke(stderr);

        return (p.ExitCode, stdout, stderr);
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
