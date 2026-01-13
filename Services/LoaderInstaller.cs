// LoaderInstaller.cs
using CmlLib.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

/// <summary>
/// Отдельный установщик Forge/NeoForge (installer.jar) в кастомный gameDir.
/// MinecraftService отвечает за базовую установку версий через CmlLib, а сюда вынесен весь “java -jar installer.jar …”.
/// </summary>
public sealed class LoaderInstaller
{
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
        loaderType = (loaderType ?? "vanilla").Trim().ToLowerInvariant();

        if (loaderType == "vanilla")
            return minecraftVersion;

        if (string.IsNullOrWhiteSpace(loaderVersion))
            throw new InvalidOperationException($"Loader '{loaderType}' требует версию (loader.version).");

        if (string.IsNullOrWhiteSpace(installerUrl))
            throw new InvalidOperationException($"Loader '{loaderType}' требует installerUrl.");

        var expectedId = GetExpectedLoaderVersionId(minecraftVersion, loaderType, loaderVersion);

        if (IsVersionPresent(expectedId))
        {
            _log?.Invoke($"Loader: уже установлен -> {expectedId}");
            return expectedId;
        }

        var installerPath = await DownloadInstallerAsync(loaderType, minecraftVersion, loaderVersion, installerUrl, ct);

        var installedId = await InstallLoaderIntoGameDirAsync(
            installerPath,
            minecraftVersion,
            loaderType,
            loaderVersion,
            expectedId,
            ct);

        if (string.IsNullOrWhiteSpace(installedId))
            throw new InvalidOperationException("Installer отработал, но версия лоадера не найдена.");

        return installedId!;
    }

    private static string? GetOfficialInstallerUrl(string loaderType, string mc, string loaderVersion)
    {
        loaderType = (loaderType ?? "").Trim().ToLowerInvariant();
        loaderVersion = (loaderVersion ?? "").Trim();
        mc = (mc ?? "").Trim();

        if (string.IsNullOrWhiteSpace(loaderType) || string.IsNullOrWhiteSpace(loaderVersion))
            return null;

        return loaderType switch
        {
            "neoforge" => $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar",
            "forge" => $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mc}-{loaderVersion}/forge-{mc}-{loaderVersion}-installer.jar",
            _ => null
        };
    }

    private async Task<string> DownloadInstallerAsync(
        string loaderType,
        string minecraftVersion,
        string loaderVersion,
        string installerUrl,
        CancellationToken ct)
    {
        var urls = new List<string>();

        if (!string.IsNullOrWhiteSpace(installerUrl))
            urls.Add(installerUrl.Trim());

        var official = GetOfficialInstallerUrl(loaderType, minecraftVersion, loaderVersion);
        if (!string.IsNullOrWhiteSpace(official) &&
            !urls.Any(u => u.Equals(official, StringComparison.OrdinalIgnoreCase)))
        {
            urls.Add(official);
        }

        Exception? last = null;

        foreach (var urlTry in urls)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!Uri.TryCreate(urlTry, UriKind.Absolute, out var uri))
                    throw new InvalidOperationException($"installerUrl is not a valid absolute url: {urlTry}");

                var cacheDir = Path.Combine(_path.BasePath, "launcher", "installers", loaderType, minecraftVersion, loaderVersion);
                Directory.CreateDirectory(cacheDir);

                var fileName = Path.GetFileName(uri.AbsolutePath);
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = $"{loaderType}-{loaderVersion}-installer.jar";

                var local = Path.Combine(cacheDir, fileName);
                if (File.Exists(local) && new FileInfo(local).Length > 0)
                    return local;

                var tmp = local + ".tmp";
                TryDeleteQuiet(tmp);

                _log?.Invoke($"Loader: скачиваю installer: {urlTry}");

                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(TimeSpan.FromMinutes(3));

                using var req = new HttpRequestMessage(HttpMethod.Get, urlTry);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/java-archive"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token);
                resp.EnsureSuccessStatusCode();

                await using (var input = await resp.Content.ReadAsStreamAsync(reqCts.Token))
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, reqCts.Token);
                    await output.FlushAsync(reqCts.Token);
                }

                var ok = await TryMoveOrReplaceWithRetryAsync(tmp, local, ct, attempts: 20, delayMs: 200);
                TryDeleteQuiet(tmp);

                if (!ok)
                    throw new IOException("Не удалось сохранить installer.jar (файл занят/нет доступа).");

                return local;
            }
            catch (Exception ex)
            {
                last = ex;
                _log?.Invoke($"Loader: не удалось скачать installer ({urlTry}) — {ex.Message}");
            }
        }

        throw new InvalidOperationException("Не удалось скачать installer ни по одному URL.", last);
    }

    private async Task<string?> InstallLoaderIntoGameDirAsync(
        string installerPath,
        string minecraftVersion,
        string loaderType,
        string loaderVersion,
        string expectedId,
        CancellationToken ct)
    {
        var javaExe = FindJavaExecutable();

        Directory.CreateDirectory(Path.Combine(_path.BasePath, "versions"));

        var beforeGame = SnapshotVersionIds(_path.BasePath);

        var argTries = new List<string[]>
        {
            new[] { "-jar", installerPath, "--installClient", "--installDir", _path.BasePath },
            new[] { "-jar", installerPath, "--installClient", "--install-dir", _path.BasePath },
        };

        foreach (var args in argTries)
        {
            var res = await RunJavaAsync(javaExe, args, workingDir: _path.BasePath, ct, env: null);
            if (res.ExitCode == 0)
            {
                if (IsVersionPresent(expectedId))
                    return expectedId;

                var afterGame = SnapshotVersionIds(_path.BasePath);
                var created = afterGame.Except(beforeGame, StringComparer.OrdinalIgnoreCase).ToList();
                var picked = PickInstalledVersionId(created, minecraftVersion, loaderType, loaderVersion);
                if (!string.IsNullOrWhiteSpace(picked))
                    return picked;
            }

            if (LooksLikeUnrecognizedOption(res.StdErr) || LooksLikeUnrecognizedOption(res.StdOut))
                continue;
        }

        // Linux/macOS: иногда installer не понимает installDir/install-dir
        if (!OperatingSystem.IsWindows())
        {
            var res = await RunJavaAsync(javaExe, new[] { "-jar", installerPath, "--installClient" }, workingDir: _path.BasePath, ct, env: null);
            if (res.ExitCode != 0)
                throw new InvalidOperationException($"Installer завершился с ошибкой.\n{(string.IsNullOrWhiteSpace(res.StdErr) ? res.StdOut : res.StdErr)}");

            if (IsVersionPresent(expectedId))
                return expectedId;

            return FindInstalledVersionId(minecraftVersion, loaderType, loaderVersion);
        }

        // Windows: installer любит %APPDATA%\.minecraft
        var tempAppData = Path.Combine(_path.BasePath, "launcher", "tmp", "appdata");
        var tempMc = Path.Combine(tempAppData, ".minecraft");

        try
        {
            Directory.CreateDirectory(tempAppData);
            Directory.CreateDirectory(tempMc);
            EnsureLauncherProfileStub(tempMc);

            var beforeTemp = SnapshotVersionIds(tempMc);

            _log?.Invoke("Loader: installer требует .minecraft, ставлю во временный APPDATA и переношу в лаунчер...");

            var env = new Dictionary<string, string>
            {
                ["APPDATA"] = tempAppData,
                ["LOCALAPPDATA"] = tempAppData
            };

            var res2 = await RunJavaAsync(javaExe, new[] { "-jar", installerPath, "--installClient" }, workingDir: _path.BasePath, ct, env);
            if (res2.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Installer завершился с ошибкой (code {res2.ExitCode}).\n" +
                    $"{(string.IsNullOrWhiteSpace(res2.StdErr) ? res2.StdOut : res2.StdErr)}");
            }

            var afterTemp = SnapshotVersionIds(tempMc);
            var createdTemp = afterTemp.Except(beforeTemp, StringComparer.OrdinalIgnoreCase).ToList();

            var tempPicked = PickInstalledVersionId(createdTemp, minecraftVersion, loaderType, loaderVersion)
                             ?? FindInstalledVersionIdInBase(tempMc, minecraftVersion, loaderType, loaderVersion);

            MergeDir(Path.Combine(tempMc, "versions"), Path.Combine(_path.BasePath, "versions"));
            MergeDir(Path.Combine(tempMc, "libraries"), Path.Combine(_path.BasePath, "libraries"));
            MergeDir(Path.Combine(tempMc, "assets"), Path.Combine(_path.BasePath, "assets"));

            if (!string.IsNullOrWhiteSpace(tempPicked) && IsVersionPresent(tempPicked!))
                return tempPicked;

            if (IsVersionPresent(expectedId))
                return expectedId;

            return FindInstalledVersionId(minecraftVersion, loaderType, loaderVersion);
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

    private static string? PickInstalledVersionId(
        List<string> candidates,
        string minecraftVersion,
        string loaderType,
        string loaderVersion)
    {
        foreach (var id in candidates)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;

            var keyword = loaderType switch
            {
                "neoforge" => "neoforge",
                "forge" => "forge",
                _ => loaderType
            };

            if (id.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(loaderVersion) || id.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase)))
                return id;
        }

        return null;
    }

    private static string GetExpectedLoaderVersionId(string mc, string loaderType, string loaderVersion)
    {
        loaderType = (loaderType ?? "vanilla").Trim().ToLowerInvariant();
        loaderVersion = (loaderVersion ?? "").Trim();

        return loaderType switch
        {
            "neoforge" => $"{mc}-neoforge-{loaderVersion}",
            "forge" => $"{mc}-forge-{loaderVersion}",
            _ => $"{mc}-{loaderType}-{loaderVersion}"
        };
    }

    private bool IsVersionPresent(string versionId)
    {
        var json = Path.Combine(_path.BasePath, "versions", versionId, versionId + ".json");
        return File.Exists(json);
    }

    private static bool LooksLikeUnrecognizedOption(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return s.Contains("UnrecognizedOptionException", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("is not a recognized option", StringComparison.OrdinalIgnoreCase);
    }

    private string FindJavaExecutable()
    {
        var runtimeDir = Path.Combine(_path.BasePath, "runtime");
        if (Directory.Exists(runtimeDir))
        {
            var candidates = Directory.EnumerateFiles(runtimeDir, "java.exe", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(runtimeDir, "javaw.exe", SearchOption.AllDirectories))
                .ToList();

            var pick = candidates.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(pick))
                return pick!;
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var p1 = Path.Combine(javaHome!, "bin", "java.exe");
            if (File.Exists(p1)) return p1;

            var p2 = Path.Combine(javaHome!, "bin", "java");
            if (File.Exists(p2)) return p2;
        }

        return "java";
    }

    private string? FindInstalledVersionId(string minecraftVersion, string loaderType, string loaderVersion)
        => FindInstalledVersionIdInBase(_path.BasePath, minecraftVersion, loaderType, loaderVersion);

    private static string? FindInstalledVersionIdInBase(string baseDir, string minecraftVersion, string loaderType, string loaderVersion)
    {
        var versionsDir = Path.Combine(baseDir, "versions");
        if (!Directory.Exists(versionsDir))
            return null;

        foreach (var dir in Directory.EnumerateDirectories(versionsDir))
        {
            var id = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (id.Contains(loaderType, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(loaderVersion) || id.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase)))
                return id;
        }

        return null;
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
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void EnsureLauncherProfileStub(string mcDir)
    {
        try
        {
            Directory.CreateDirectory(mcDir);

            var p1 = Path.Combine(mcDir, "launcher_profiles.json");
            if (!File.Exists(p1))
            {
                var stub = new
                {
                    profiles = new Dictionary<string, object>(),
                    settings = new Dictionary<string, object>(),
                    selectedProfile = "",
                    authenticationDatabase = new Dictionary<string, object>(),
                    launcherVersion = new { name = "LegendBorn", format = 21 }
                };

                File.WriteAllText(p1, JsonSerializer.Serialize(stub, new JsonSerializerOptions { WriteIndented = true }));
            }

            var p2 = Path.Combine(mcDir, "launcher_profiles_microsoft_store.json");
            if (!File.Exists(p2))
                File.WriteAllText(p2, File.ReadAllText(p1));
        }
        catch { }
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunJavaAsync(
        string javaExe,
        IEnumerable<string> args,
        string workingDir,
        CancellationToken ct,
        IDictionary<string, string>? env)
    {
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

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить java для installer.");

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

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
