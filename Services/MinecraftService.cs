using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

public sealed class MinecraftService
{
    private readonly MinecraftPath _path;
    private readonly MinecraftLauncher _launcher;

    private static readonly HttpClient _http = CreateHttp();

    private static readonly string[] DefaultPackMirrors =
    {
        "https://legendborn.ru/launcher/pack/",
    };

    private const string ManifestFileName = "manifest.json";
    private const string PackStateFileName = "pack_state.json";

    private static readonly string[] AllowedDestPrefixes =
    {
        "config/",
        "defaultconfigs/",
        "kubejs/",
        "mods/",
        "resourcepacks/",
        "shaderpacks/",
    };

    public event EventHandler<string>? Log;
    public event EventHandler<int>? ProgressPercent;

    public MinecraftService(string gameDir)
    {
        _path = new MinecraftPath(gameDir);
        Directory.CreateDirectory(_path.BasePath);

        _launcher = new MinecraftLauncher(_path);

        _launcher.FileProgressChanged += (_, args) =>
        {
            var percent = args.TotalTasks > 0
                ? (int)Math.Round(args.ProgressedTasks * 100.0 / args.TotalTasks)
                : 0;

            ProgressPercent?.Invoke(this, Math.Clamp(percent, 0, 100));
            Log?.Invoke(this, $"{args.EventType}: {args.Name} ({args.ProgressedTasks}/{args.TotalTasks})");
        };

        _launcher.ByteProgressChanged += (_, args) =>
        {
            if (args.TotalBytes <= 0) return;
            var percent = (int)Math.Round(args.ProgressedBytes * 100.0 / args.TotalBytes);
            ProgressPercent?.Invoke(this, Math.Clamp(percent, 0, 100));
        };
    }

    public sealed record LoaderSpec(string Type, string Version, string InstallerUrl);

    public async Task<string> PrepareAsync(
        string minecraftVersion,
        LoaderSpec loader,
        string[]? packMirrors,
        bool syncPack,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException("minecraftVersion is empty");

        var loaderType = (loader.Type ?? "vanilla").Trim().ToLowerInvariant();
        var loaderVersion = (loader.Version ?? "").Trim();
        var installerUrl = (loader.InstallerUrl ?? "").Trim();

        if (syncPack)
        {
            var mirrors = (packMirrors is { Length: > 0 } ? packMirrors : DefaultPackMirrors)
                .Select(NormalizeBaseUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await EnsurePackUpToDateAsync(mirrors, ct);
        }

        Log?.Invoke(this, $"Minecraft: установка базовой версии {minecraftVersion}...");
        await _launcher.InstallAsync(minecraftVersion);

        var launchVersionId = await EnsureLoaderInstalledAsync(
            minecraftVersion,
            loaderType,
            loaderVersion,
            installerUrl,
            ct);

        if (!string.Equals(launchVersionId, minecraftVersion, StringComparison.OrdinalIgnoreCase))
        {
            Log?.Invoke(this, $"Minecraft: подготовка версии {launchVersionId}...");
            await _launcher.InstallAsync(launchVersionId);
        }

        ProgressPercent?.Invoke(this, 100);
        Log?.Invoke(this, "Подготовка завершена.");
        return launchVersionId;
    }

    public async Task<Process> BuildAndLaunchAsync(string version, string username, int ramMb, string? serverIp = null)
    {
        var opt = new MLaunchOption
        {
            Session = MSession.CreateOfflineSession(username),
            MaximumRamMb = ramMb
        };

        if (!string.IsNullOrWhiteSpace(serverIp))
            opt.ServerIp = serverIp;

        var process = await _launcher.BuildProcessAsync(version, opt);
        process.EnableRaisingEvents = true;
        process.Start();
        return process;
    }

    private async Task<string> EnsureLoaderInstalledAsync(
        string minecraftVersion,
        string loaderType,
        string loaderVersion,
        string installerUrl,
        CancellationToken ct)
    {
        if (loaderType == "vanilla")
            return minecraftVersion;

        if (string.IsNullOrWhiteSpace(installerUrl))
            throw new InvalidOperationException($"Loader '{loaderType}' требует installerUrl.");

        var expectedId = GetExpectedLoaderVersionId(minecraftVersion, loaderType, loaderVersion);

        if (IsVersionPresent(expectedId))
        {
            Log?.Invoke(this, $"Loader: уже установлен -> {expectedId}");
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

    private async Task<string?> InstallLoaderIntoGameDirAsync(
        string installerPath,
        string minecraftVersion,
        string loaderType,
        string loaderVersion,
        string expectedId,
        CancellationToken ct)
    {
        var javaExe = FindJavaExecutable();

        var versionsDir = Path.Combine(_path.BasePath, "versions");
        Directory.CreateDirectory(versionsDir);

        var beforeGame = SnapshotVersionIds(_path.BasePath);

        // 1) если installer вдруг поддерживает явный installDir
        var argTries = new List<string[]>
        {
            new[] { "-jar", installerPath, "--installClient", "--installDir", _path.BasePath },
            new[] { "-jar", installerPath, "--installClient", "--install-dir", _path.BasePath },
        };

        foreach (var args in argTries)
        {
            var res = await RunJavaAsync(javaExe, args, workingDir: _path.BasePath, ct);
            if (res.ExitCode == 0)
            {
                if (IsVersionPresent(expectedId)) return expectedId;

                var afterGame = SnapshotVersionIds(_path.BasePath);
                var created = afterGame.Except(beforeGame, StringComparer.OrdinalIgnoreCase).ToList();
                var picked = PickInstalledVersionId(created, minecraftVersion, loaderType, loaderVersion);
                if (!string.IsNullOrWhiteSpace(picked)) return picked;
            }

            if (LooksLikeUnrecognizedOption(res.StdErr) || LooksLikeUnrecognizedOption(res.StdOut))
                continue;
        }

        // 2) fallback: NeoForge/Forge installer на Windows почти всегда ставит в %APPDATA%\.minecraft
        if (!OperatingSystem.IsWindows())
        {
            var res = await RunJavaAsync(javaExe, new[] { "-jar", installerPath, "--installClient" }, workingDir: _path.BasePath, ct);
            if (res.ExitCode != 0)
                throw new InvalidOperationException($"Installer завершился с ошибкой.\n{res.StdErr}");

            if (IsVersionPresent(expectedId)) return expectedId;
            return FindInstalledVersionId(minecraftVersion, loaderType, loaderVersion);
        }

        var systemMc = GetSystemMinecraftDir();
        Directory.CreateDirectory(systemMc);
        EnsureLauncherProfileStub(systemMc);

        var beforeSys = SnapshotVersionIds(systemMc);

        Log?.Invoke(this, $"Loader: installer требует .minecraft, ставлю во временную системную папку и переношу в лаунчер...");

        var res2 = await RunJavaAsync(javaExe, new[] { "-jar", installerPath, "--installClient" }, workingDir: _path.BasePath, ct);
        if (res2.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Installer завершился с ошибкой (code {res2.ExitCode}).\n" +
                $"{(string.IsNullOrWhiteSpace(res2.StdErr) ? res2.StdOut : res2.StdErr)}");
        }

        var afterSys = SnapshotVersionIds(systemMc);
        var createdSys = afterSys.Except(beforeSys, StringComparer.OrdinalIgnoreCase).ToList();

        var sysPicked = PickInstalledVersionId(createdSys, minecraftVersion, loaderType, loaderVersion)
                        ?? FindInstalledVersionIdInBase(systemMc, minecraftVersion, loaderType, loaderVersion);

        // переносим (не трогаем существующий .minecraft пользователя кроме чтения)
        MergeDir(Path.Combine(systemMc, "versions"), Path.Combine(_path.BasePath, "versions"));
        MergeDir(Path.Combine(systemMc, "libraries"), Path.Combine(_path.BasePath, "libraries"));
        MergeDir(Path.Combine(systemMc, "assets"), Path.Combine(_path.BasePath, "assets"));

        if (!string.IsNullOrWhiteSpace(sysPicked) && IsVersionPresent(sysPicked!))
            return sysPicked;

        if (IsVersionPresent(expectedId))
            return expectedId;

        return FindInstalledVersionId(minecraftVersion, loaderType, loaderVersion);
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

    private async Task<string> DownloadInstallerAsync(
        string loaderType,
        string minecraftVersion,
        string loaderVersion,
        string installerUrl,
        CancellationToken ct)
    {
        var cacheDir = Path.Combine(_path.BasePath, "launcher", "installers", loaderType, minecraftVersion, loaderVersion);
        Directory.CreateDirectory(cacheDir);

        var fileName = Path.GetFileName(new Uri(installerUrl).AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"{loaderType}-{loaderVersion}-installer.jar";

        var local = Path.Combine(cacheDir, fileName);
        if (File.Exists(local) && new FileInfo(local).Length > 0)
            return local;

        var tmp = local + ".tmp";
        TryDeleteQuiet(tmp);

        Log?.Invoke(this, $"Loader: скачиваю installer: {installerUrl}");

        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts.CancelAfter(TimeSpan.FromMinutes(3));

        using var resp = await _http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, reqCts.Token);
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

    private static string GetSystemMinecraftDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, ".minecraft");
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
        CancellationToken ct)
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

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить java для installer.");

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        await p.WaitForExitAsync(ct);

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (!string.IsNullOrWhiteSpace(stdout))
            Log?.Invoke(this, stdout);

        if (!string.IsNullOrWhiteSpace(stderr))
            Log?.Invoke(this, stderr);

        return (p.ExitCode, stdout, stderr);
    }

    private async Task EnsurePackUpToDateAsync(string[] mirrors, CancellationToken ct)
    {
        Log?.Invoke(this, "Сборка: проверка обновлений...");

        var (activeBaseUrl, manifest) = await DownloadManifestFromMirrorsAsync(mirrors, ct);

        if (manifest.Files is null || manifest.Files.Count == 0)
        {
            Log?.Invoke(this, "Сборка: manifest пустой — пропускаю синхронизацию.");
            return;
        }

        var state = LoadPackState();

        var wanted = new HashSet<string>(
            manifest.Files.Select(f => NormalizeRelPath(f.Path)).Where(p => !string.IsNullOrWhiteSpace(p)),
            StringComparer.OrdinalIgnoreCase);

        long totalBytes = manifest.Files.Sum(f => Math.Max(0, f.Size));
        long doneBytes = 0;

        static long ClampLong(long v, long min, long max)
            => v < min ? min : (v > max ? max : v);

        int lastPercent = -1;
        void ReportProgress()
        {
            if (totalBytes <= 0) return;
            var p = (int)Math.Round(doneBytes * 100.0 / totalBytes);
            p = Math.Clamp(p, 0, 100);
            if (p != lastPercent)
            {
                lastPercent = p;
                ProgressPercent?.Invoke(this, p);
            }
        }

        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();

            var destRel = NormalizeRelPath(file.Path);
            if (string.IsNullOrWhiteSpace(destRel))
                continue;

            if (!IsAllowedDest(destRel))
                throw new InvalidOperationException($"Manifest пытается записать запрещённый путь: {destRel}");

            if (!IsValidSha256(file.Sha256))
                throw new InvalidOperationException($"Invalid sha256 in manifest for {destRel}");

            var localPath = ToLocalPath(destRel);

            if (!IsUnderGameDir(localPath))
                throw new InvalidOperationException($"Unsafe path in manifest: {file.Path}");

            await TryApplyPendingAsync(destRel, localPath, file.Sha256, ct);

            var check = await CheckFileAsync(destRel, localPath, file, state, ct);

            if (check == FileCheckResult.Match)
            {
                doneBytes = ClampLong(doneBytes + Math.Max(0, file.Size), 0, totalBytes);
                ReportProgress();
                continue;
            }

            // важный момент: config/ часто меняется игрой -> не перекачиваем обратно каждый запуск
            if (IsUserMutableDest(destRel))
            {
                Log?.Invoke(this, $"Сборка: {destRel} изменён локально — оставляю как есть.");
                doneBytes = ClampLong(doneBytes + Math.Max(0, file.Size), 0, totalBytes);
                ReportProgress();
                continue;
            }

            var blobRel = GetBlobRelPath(file);

            await DownloadFileFromMirrorsAsync(
                activeBaseUrl,
                blobRel,
                file,
                localPath,
                destRel,
                manifest.Mirrors,
                mirrors,
                ct,
                onBytes: bytes =>
                {
                    if (bytes == 0) return;
                    doneBytes = ClampLong(doneBytes + bytes, 0, totalBytes);
                    ReportProgress();
                });

            UpdatePackStateEntry(state, destRel, localPath, file.Sha256);
            Log?.Invoke(this, $"Сборка: OK {destRel}");
        }

        // ✅ prune: только разрешённые корни + НЕ трогаем user-mutable (config/)
        if (manifest.Prune is { Length: > 0 })
        {
            var roots = manifest.Prune
                .Select(NormalizeRoot)
                .Where(r =>
                    !string.IsNullOrWhiteSpace(r) &&
                    IsAllowedDest(r) &&
                    !IsUserMutableDest(r)) // не чистим config/
                .ToArray();

            if (roots.Length > 0)
                PruneExtras(roots, wanted);
        }

        state.PackId = manifest.PackId;
        state.ManifestVersion = manifest.Version;
        SavePackState(state);

        Log?.Invoke(this, $"Сборка: актуальна (версия {manifest.Version}).");
        ProgressPercent?.Invoke(this, 100);
    }

    private enum FileCheckResult { MissingOrDifferent, Match }

    private async Task<FileCheckResult> CheckFileAsync(string rel, string localPath, PackFile file, PackState state, CancellationToken ct)
    {
        if (!File.Exists(localPath))
            return FileCheckResult.MissingOrDifferent;

        try
        {
            var info = new FileInfo(localPath);

            if (file.Size > 0 && info.Length != file.Size)
                return FileCheckResult.MissingOrDifferent;

            if (state.Files.TryGetValue(rel, out var cached) &&
                cached.Size == info.Length &&
                cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
                cached.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return FileCheckResult.Match;
            }

            var sha = await ComputeSha256Async(localPath, ct);
            if (!sha.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                return FileCheckResult.MissingOrDifferent;

            UpdatePackStateEntry(state, rel, localPath, file.Sha256);
            return FileCheckResult.Match;
        }
        catch
        {
            return FileCheckResult.MissingOrDifferent;
        }
    }

    private void UpdatePackStateEntry(PackState state, string rel, string localPath, string sha256)
    {
        try
        {
            var info = new FileInfo(localPath);
            state.Files[rel] = new PackStateEntry
            {
                Size = info.Length,
                Sha256 = sha256.ToLowerInvariant(),
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks
            };
        }
        catch { }
    }

    private PackState LoadPackState()
    {
        try
        {
            var path = Path.Combine(_path.BasePath, "launcher", PackStateFileName);
            if (!File.Exists(path))
                return new PackState();

            var json = File.ReadAllText(path);
            var st = JsonSerializer.Deserialize<PackState>(json);
            return st ?? new PackState();
        }
        catch
        {
            return new PackState();
        }
    }

    private void SavePackState(PackState state)
    {
        try
        {
            var dir = Path.Combine(_path.BasePath, "launcher");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, PackStateFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private sealed class PackState
    {
        public string? PackId { get; set; }
        public string? ManifestVersion { get; set; }
        public Dictionary<string, PackStateEntry> Files { get; set; } = new();
    }

    private sealed class PackStateEntry
    {
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
        public long LastWriteUtcTicks { get; set; }
    }

    private async Task<(string activeBaseUrl, PackManifest manifest)> DownloadManifestFromMirrorsAsync(string[] mirrors, CancellationToken ct)
    {
        Exception? last = null;

        foreach (var baseUrlRaw in mirrors)
        {
            var baseUrl = NormalizeBaseUrl(baseUrlRaw);

            try
            {
                var url = CombineUrl(baseUrl, ManifestFileName);
                Log?.Invoke(this, $"Сборка: читаю manifest: {url}");

                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(TimeSpan.FromSeconds(30));

                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, reqCts.Token);
                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(reqCts.Token);
                var manifest = await JsonSerializer.DeserializeAsync(stream, PackJsonContext.Default.PackManifest, reqCts.Token);
                if (manifest is null)
                    throw new InvalidOperationException("Manifest deserialization returned null");

                if (manifest.Mirrors is { Length: > 0 })
                {
                    manifest = manifest with
                    {
                        Mirrors = manifest.Mirrors
                            .Select(NormalizeBaseUrl)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                    };
                }

                return (baseUrl, manifest);
            }
            catch (Exception ex)
            {
                last = ex;
                Log?.Invoke(this, $"Сборка: зеркало недоступно ({baseUrl}) — {ex.Message}");
            }
        }

        throw new InvalidOperationException("Не удалось скачать manifest ни с одного зеркала.", last);
    }

    private async Task DownloadFileFromMirrorsAsync(
        string activeBaseUrl,
        string blobRel,
        PackFile file,
        string localPath,
        string destRel,
        string[]? manifestMirrors,
        string[] rootMirrors,
        CancellationToken ct,
        Action<long> onBytes)
    {
        var mirrors = (manifestMirrors is { Length: > 0 } ? manifestMirrors : rootMirrors)
            .Select(NormalizeBaseUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var ordered = new List<string> { NormalizeBaseUrl(activeBaseUrl) };
        ordered.AddRange(mirrors.Where(m => !m.Equals(activeBaseUrl, StringComparison.OrdinalIgnoreCase)));

        Exception? last = null;

        foreach (var baseUrl in ordered)
        {
            ct.ThrowIfCancellationRequested();

            long attemptBytes = 0;
            void Counted(long b)
            {
                if (b > 0) attemptBytes += b;
                onBytes(b);
            }

            try
            {
                var url = CombineUrl(baseUrl, blobRel);
                Log?.Invoke(this, $"Сборка: download {blobRel} ({FormatBytes(file.Size)})");

                await DownloadToFileAsync(url, localPath, file.Sha256, destRel, ct, Counted);
                return;
            }
            catch (Exception ex)
            {
                last = ex;

                // ✅ rollback прогресса за неудавшуюся попытку
                if (attemptBytes > 0)
                    onBytes(-attemptBytes);

                Log?.Invoke(this, $"Сборка: ошибка скачивания (зеркало {baseUrl}) — {ex.Message}");
            }
        }

        throw new InvalidOperationException($"Не удалось скачать blob: {blobRel}", last);
    }

    private async Task DownloadToFileAsync(string url, string localPath, string expectedSha256, string destRel, CancellationToken ct, Action<long> onBytes)
    {
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var tmp = localPath + ".tmp";
        TryDeleteQuiet(tmp);

        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts.CancelAfter(TimeSpan.FromMinutes(10));

        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, reqCts.Token);
        resp.EnsureSuccessStatusCode();

        using var sha = SHA256.Create();
        var buffer = new byte[128 * 1024];

        await using (var input = await resp.Content.ReadAsStreamAsync(reqCts.Token))
        await using (var output = new FileStream(
            tmp,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, reqCts.Token)) > 0)
            {
                await output.WriteAsync(buffer, 0, read, reqCts.Token);
                sha.TransformBlock(buffer, 0, read, null, 0);
                onBytes(read);
            }

            await output.FlushAsync(reqCts.Token);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteQuiet(tmp);
            throw new InvalidOperationException($"SHA256 mismatch: expected {expectedSha256}, got {actual}");
        }

        var ok = await TryMoveOrReplaceWithRetryAsync(tmp, localPath, ct, attempts: 20, delayMs: 200);
        if (ok)
        {
            TryDeleteQuiet(tmp);
            return;
        }

        if (IsOptionalDest(destRel))
        {
            var pending = localPath + ".pending";
            var pendingOk = await TryMoveOrReplaceWithRetryAsync(tmp, pending, ct, attempts: 10, delayMs: 200);
            if (pendingOk)
            {
                Log?.Invoke(this, $"Сборка: файл занят, сохранил pending: {destRel}");
                return;
            }

            TryDeleteQuiet(tmp);
            Log?.Invoke(this, $"Сборка: файл занят и не удалось сохранить pending: {destRel}. Пропускаю.");
            return;
        }

        TryDeleteQuiet(tmp);
        throw new IOException($"Не удалось записать файл сборки (занят другим процессом): {destRel}");
    }

    private async Task TryApplyPendingAsync(string destRel, string localPath, string expectedSha256, CancellationToken ct)
    {
        var pending = localPath + ".pending";
        if (!File.Exists(pending))
            return;

        try
        {
            var sha = await ComputeSha256Async(pending, ct);
            if (!sha.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteQuiet(pending);
                return;
            }
        }
        catch
        {
            return;
        }

        var ok = await TryMoveOrReplaceWithRetryAsync(pending, localPath, ct, attempts: 10, delayMs: 200);
        if (ok)
        {
            TryDeleteQuiet(pending);
            Log?.Invoke(this, $"Сборка: применил pending для {destRel}");
        }
    }

    private static async Task<bool> TryMoveOrReplaceWithRetryAsync(string source, string dest, CancellationToken ct, int attempts, int delayMs)
    {
        for (int i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(dest))
                {
                    var backup = dest + ".bak";
                    TryDeleteQuiet(backup);

                    File.Replace(source, dest, backup, ignoreMetadataErrors: true);
                    TryDeleteQuiet(backup);
                }
                else
                {
                    File.Move(source, dest, overwrite: true);
                }

                return true;
            }
            catch (IOException)
            {
                await Task.Delay(delayMs, ct);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(delayMs, ct);
            }
        }

        return false;
    }

    private void PruneExtras(string[] roots, HashSet<string> wanted)
    {
        foreach (var root in roots)
        {
            var localRoot = ToLocalPath(root.TrimEnd('/') + "/");
            if (!Directory.Exists(localRoot))
                continue;

            foreach (var f in Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories))
            {
                var rel = NormalizeRelPath(Path.GetRelativePath(_path.BasePath, f).Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(rel))
                    continue;

                if (!wanted.Contains(rel))
                {
                    try
                    {
                        File.Delete(f);
                        Log?.Invoke(this, $"Сборка: удалён лишний файл {rel}");
                    }
                    catch { }
                }
            }
        }
    }

    private static bool IsAllowedDest(string destRel)
    {
        destRel = destRel.Replace('\\', '/');
        foreach (var p in AllowedDestPrefixes)
        {
            if (destRel.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsOptionalDest(string destRel)
    {
        destRel = destRel.Replace('\\', '/');
        return destRel.StartsWith("config/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUserMutableDest(string destRel)
    {
        destRel = destRel.Replace('\\', '/');
        return destRel.StartsWith("config/", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetBlobRelPath(PackFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.Blob))
            return NormalizeRelPath(file.Blob);

        var sha = (file.Sha256 ?? "").Trim().ToLowerInvariant();
        if (sha.Length < 2)
            throw new InvalidOperationException("sha256 is invalid for blob path");

        return $"blobs/{sha.Substring(0, 2)}/{sha}";
    }

    private string ToLocalPath(string relPath)
    {
        var safe = relPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(_path.BasePath, safe);
    }

    private bool IsUnderGameDir(string fullPath)
    {
        var root = Path.GetFullPath(_path.BasePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fp = Path.GetFullPath(fullPath);
        return fp.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelPath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        p = p.Trim().Replace('\\', '/');

        while (p.StartsWith("/")) p = p[1..];
        while (p.Contains("//")) p = p.Replace("//", "/");

        if (p.Contains("..", StringComparison.Ordinal))
            return "";

        return p;
    }

    private static string NormalizeBaseUrl(string url)
    {
        url = (url ?? "").Trim();
        if (!url.EndsWith("/")) url += "/";
        return url;
    }

    private static string NormalizeRoot(string root)
    {
        root = NormalizeRelPath(root);
        if (string.IsNullOrWhiteSpace(root)) return "";
        if (!root.EndsWith("/")) root += "/";
        return root;
    }

    private static string CombineUrl(string baseUrl, string rel)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        rel = NormalizeRelPath(rel);
        return new Uri(new Uri(baseUrl), rel).ToString();
    }

    private static bool IsValidSha256(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim();
        if (hex.Length != 64) return false;

        for (int i = 0; i < hex.Length; i++)
        {
            var c = hex[i];
            var ok =
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });

        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LegendBornLauncher/0.1.6");
        return http;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] suf = { "B", "KB", "MB", "GB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < suf.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {suf[i]}";
    }

    private static void TryDeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public sealed record PackManifest(
        [property: JsonPropertyName("packId")] string PackId,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("files")] List<PackFile> Files,
        [property: JsonPropertyName("mirrors")] string[]? Mirrors = null,
        [property: JsonPropertyName("prune")] string[]? Prune = null
    );

    public sealed record PackFile(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("blob")] string? Blob = null
    );
}

[JsonSerializable(typeof(MinecraftService.PackManifest))]
internal partial class PackJsonContext : JsonSerializerContext
{
}
