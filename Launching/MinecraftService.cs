using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using LegendBorn.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Launching;

public sealed class MinecraftService
{
    private readonly MinecraftPath _path;
    private readonly MinecraftLauncher _launcher;
    private readonly LoaderInstaller _loaderInstaller;

    private static readonly HttpClient _http = CreateHttp();

    // ====== защита от параллельных Prepare/Sync ======
    private readonly SemaphoreSlim _exclusive = new(1, 1);

    private static readonly string[] DefaultPackMirrors =
    {
        "https://pack.legendborn.ru/launcher/pack/",
        "https://612cd759-4c9d-450e-bc91-a51d3c56e834.selstorage.ru/launcher/pack/",
        "https://master.dl.sourceforge.net/project/legendborn-pack/launcher/pack/",
        "https://downloads.sourceforge.net/project/legendborn-pack/launcher/pack/"
    };

    private const string ManifestFileName = "manifest.json";
    private const string PackStateFileName = "pack_state.json";

    private const int PackMaxParallelDownloads = 6;

    private const int MirrorPrimaryTimeoutSec1 = 6;
    private const int MirrorPrimaryTimeoutSec2 = 12;
    private const int MirrorFallbackTimeoutSec = 18;
    private const int MirrorProbeMaxManifestBytes = 5 * 1024 * 1024;
    private const double MirrorEwmaAlpha = 0.30;

    private const int MirrorStatsSaveMinIntervalMs = 1500;
    private long _mirrorStatsLastSaveUnixMs;

    // RAM clamp: 4..16 GB (в MB)
    public const int MinRamMb = 4096;
    public const int MaxRamMb = 16384;

    // SHA256(empty)
    private const string Sha256Empty =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

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

    private readonly object _mirrorStatsLock = new();
    private MirrorStatsRoot _mirrorStats = new();

    private readonly object _mirrorStatsIoLock = new();
    private readonly object _packStateIoLock = new();

    private static readonly JsonSerializerOptions MirrorStatsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    private string? _primaryBaseUrlThisRun;
    private bool _primaryOkThisRun;

    // “быстрый CDN-фоллбек” этой сессии (между SourceForge и Selectel)
    private string? _fastCdnFallbackThisRun;

    private string MirrorStatsPath => Path.Combine(_path.BasePath, "launcher", "mirror_stats.json");

    public MinecraftService(string gameDir)
    {
        _path = new MinecraftPath(gameDir);
        Directory.CreateDirectory(_path.BasePath);

        _mirrorStats = LoadMirrorStats();

        _launcher = new MinecraftLauncher(_path);

        _loaderInstaller = new LoaderInstaller(
            _path,
            _http,
            log: msg => Log?.Invoke(this, msg));

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

    // =========================
    // Public API (exclusive)
    // =========================

    public Task SyncPackAsync(string[]? packMirrors, CancellationToken ct = default)
        => RunExclusiveAsync(ct, t => SyncPackCoreAsync(packMirrors, t));

    public Task<string> PrepareAsync(
        string minecraftVersion,
        LoaderSpec loader,
        string[]? packMirrors,
        bool syncPack,
        CancellationToken ct = default)
        => RunExclusiveAsync(ct, t => PrepareCoreAsync(minecraftVersion, loader, packMirrors, syncPack, t));

    private async Task SyncPackCoreAsync(string[]? packMirrors, CancellationToken ct)
    {
        var mirrors = ExpandAndOrderPackMirrors(packMirrors);

        if (mirrors.Length == 0)
            throw new InvalidOperationException("Pack mirrors list is empty");

        await EnsurePackUpToDateAsync(mirrors, ct).ConfigureAwait(false);
    }

    private async Task<string> PrepareCoreAsync(
        string minecraftVersion,
        LoaderSpec loader,
        string[]? packMirrors,
        bool syncPack,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException("minecraftVersion is empty");

        var mc = minecraftVersion.Trim();
        var loaderType = (loader.Type ?? "vanilla").Trim().ToLowerInvariant();
        var loaderVersion = (loader.Version ?? "").Trim();
        var installerUrl = (loader.InstallerUrl ?? "").Trim();

        if (syncPack)
            await SyncPackCoreAsync(packMirrors, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        Log?.Invoke(this, $"Minecraft: установка базовой версии {mc}...");
        await _launcher.InstallAsync(mc).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var launchVersionId = await _loaderInstaller.EnsureInstalledAsync(
            minecraftVersion: mc,
            loaderType: loaderType,
            loaderVersion: loaderVersion,
            installerUrl: installerUrl,
            ct: ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        if (!string.Equals(launchVersionId, mc, StringComparison.OrdinalIgnoreCase))
        {
            Log?.Invoke(this, $"Minecraft: подготовка версии {launchVersionId}...");
            await _launcher.InstallAsync(launchVersionId).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        ProgressPercent?.Invoke(this, 100);
        Log?.Invoke(this, "Подготовка завершена.");
        return launchVersionId;
    }

    private async Task RunExclusiveAsync(CancellationToken ct, Func<CancellationToken, Task> action)
    {
        await _exclusive.WaitAsync(ct).ConfigureAwait(false);
        try { await action(ct).ConfigureAwait(false); }
        finally { _exclusive.Release(); }
    }

    private async Task<T> RunExclusiveAsync<T>(CancellationToken ct, Func<CancellationToken, Task<T>> action)
    {
        await _exclusive.WaitAsync(ct).ConfigureAwait(false);
        try { return await action(ct).ConfigureAwait(false); }
        finally { _exclusive.Release(); }
    }

    public async Task<Process> BuildAndLaunchAsync(string version, string username, int ramMb, string? serverIp = null)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("version is empty", nameof(version));

        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("username is empty", nameof(username));

        ramMb = Math.Clamp(ramMb <= 0 ? MinRamMb : ramMb, MinRamMb, MaxRamMb);

        var opt = new MLaunchOption
        {
            Session = MSession.CreateOfflineSession(username.Trim()),
            MaximumRamMb = ramMb
        };

        if (!string.IsNullOrWhiteSpace(serverIp))
            opt.ServerIp = serverIp.Trim();

        var process = await _launcher.BuildProcessAsync(version, opt).ConfigureAwait(false);

        process.EnableRaisingEvents = true;
        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить процесс Minecraft.");

        return process;
    }

    // =========================
    // Mirrors: normalize + default ordering
    // =========================

    private static bool LooksLikeCloudBucket(string url)
    {
        url = (url ?? "").ToLowerInvariant();
        return url.Contains("pack.legendborn.ru");
    }

    private static bool LooksLikeSourceForge(string url)
    {
        url = (url ?? "").ToLowerInvariant();
        return url.Contains("master.dl.sourceforge.net") ||
               url.Contains("downloads.sourceforge.net") ||
               url.Contains("sourceforge.net");
    }

    private static bool LooksLikeSelectel(string url)
    {
        url = (url ?? "").ToLowerInvariant();

        return url.Contains(".selstorage.ru") ||
               url.Contains("selstorage.ru") ||
               url.Contains("storage.selcloud.ru") ||
               url.Contains("s3.storage.selcloud.ru") ||
               url.Contains("selcdn") ||
               url.Contains("selectel");
    }

    private static bool IsSfOrSelectel(string url)
        => LooksLikeSourceForge(url) || LooksLikeSelectel(url);

    private static string[] ExpandAndOrderPackMirrors(string[]? packMirrors)
    {
        if (packMirrors is { Length: > 0 })
        {
            var ordered = new List<string>(packMirrors.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in packMirrors)
            {
                var u = NormalizeAbsoluteBaseUrl(raw);
                if (string.IsNullOrWhiteSpace(u)) continue;
                if (seen.Add(u)) ordered.Add(u);
            }

            return ordered.ToArray();
        }

        var defaults = DefaultPackMirrors
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return defaults
            .OrderBy(u =>
            {
                var lu = u.ToLowerInvariant();
                if (LooksLikeCloudBucket(lu)) return 0;
                if (LooksLikeSelectel(lu)) return 1;
                if (lu.Contains("master.dl.sourceforge.net")) return 2;
                if (lu.Contains("downloads.sourceforge.net")) return 3;
                if (LooksLikeSourceForge(lu)) return 4;
                return 5;
            })
            .ToArray();
    }

    // =========================
    // Pack sync
    // =========================

    private async Task EnsurePackUpToDateAsync(string[] mirrors, CancellationToken ct)
    {
        Log?.Invoke(this, "Сборка: проверка обновлений...");

        var (activeBaseUrl, manifest) = await DownloadManifestFromMirrorsAsync(mirrors, ct).ConfigureAwait(false);

        var filesList = manifest.Files ?? new List<PackFile>();
        if (filesList.Count == 0)
        {
            Log?.Invoke(this, "Сборка: manifest пустой — пропускаю синхронизацию.");
            SaveMirrorStatsQuiet(force: true);
            return;
        }

        var normalizedMap = new Dictionary<string, PackFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in filesList)
        {
            var rel = NormalizeRelPath(f.Path);
            if (string.IsNullOrWhiteSpace(rel))
                continue;

            if (!IsValidFileRelPath(rel))
                throw new InvalidOperationException($"Manifest содержит путь, который выглядит как директория/невалидный файл: {rel}");

            if (normalizedMap.TryGetValue(rel, out var existing))
            {
                var same =
                    string.Equals(existing.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase) &&
                    existing.Size == f.Size &&
                    string.Equals(existing.Blob ?? "", f.Blob ?? "", StringComparison.OrdinalIgnoreCase);

                if (!same)
                    throw new InvalidOperationException($"Manifest содержит дубликат пути с разными данными: {rel}");

                continue;
            }

            normalizedMap[rel] = f;
        }

        if (normalizedMap.Count == 0)
        {
            Log?.Invoke(this, "Сборка: manifest не содержит валидных путей — пропускаю синхронизацию.");
            SaveMirrorStatsQuiet(force: true);
            return;
        }

        var state = LoadPackState();
        var stateLock = new object();

        var wanted = new HashSet<string>(normalizedMap.Keys, StringComparer.OrdinalIgnoreCase);

        long totalBytes = normalizedMap.Values.Sum(f => Math.Max(0, f.Size));
        long doneBytes = 0;
        int lastPercent = -1;

        void AddBytes(long delta)
        {
            if (delta == 0) return;

            Interlocked.Add(ref doneBytes, delta);

            if (totalBytes <= 0) return;

            var done = Interlocked.Read(ref doneBytes);
            if (done < 0) done = 0;
            if (done > totalBytes) done = totalBytes;

            var p = (int)Math.Round(done * 100.0 / totalBytes);
            p = Math.Clamp(p, 0, 100);

            var prev = Volatile.Read(ref lastPercent);
            if (p == prev) return;

            if (Interlocked.CompareExchange(ref lastPercent, p, prev) == prev)
                ProgressPercent?.Invoke(this, p);
        }

        var errors = new ConcurrentQueue<Exception>();
        var pendings = new ConcurrentQueue<string>();

        await Parallel.ForEachAsync(
            normalizedMap,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = PackMaxParallelDownloads,
                CancellationToken = ct
            },
            async (kv, token) =>
            {
                try
                {
                    await ProcessOneFileAsync(
                        destRel: kv.Key,
                        file: kv.Value,
                        errors: errors,
                        pendings: pendings,
                        state: state,
                        stateLock: stateLock,
                        activeBaseUrl: activeBaseUrl,
                        manifestMirrors: manifest.Mirrors,
                        rootMirrors: mirrors,
                        addBytes: AddBytes,
                        ct: token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }).ConfigureAwait(false);

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        if (!errors.IsEmpty)
        {
            if (errors.TryDequeue(out var first))
            {
                while (errors.TryDequeue(out var extra))
                    Log?.Invoke(this, $"Сборка: дополнительная ошибка: {extra.Message}");

                throw new InvalidOperationException("Синхронизация сборки завершилась с ошибками.", first);
            }
        }

        ApplyDeletesAndPrunes(manifest, wanted, state, stateLock);

        var hasPending = !pendings.IsEmpty;

        lock (stateLock)
        {
            foreach (var k in state.Files.Keys.ToList())
            {
                if (!wanted.Contains(k))
                    state.Files.Remove(k);
            }

            // ВАЖНО: если есть pending — сборка ещё НЕ в нужном состоянии, не ставим версию manifest как "применённую".
            if (!hasPending)
            {
                state.PackId = manifest.PackId;
                state.ManifestVersion = GetManifestIdentity(manifest);
            }
        }

        SavePackState(state);

        if (hasPending)
        {
            var cnt = pendings.Count;
            Log?.Invoke(this, $"Сборка: синхронизация завершена, но {cnt} файл(ов) сохранены как .pending (файлы заняты). Закрой Minecraft и запусти синхронизацию ещё раз — pending применятся автоматически.");
        }
        else
        {
            Log?.Invoke(this, $"Сборка: актуальна ({GetManifestDisplayVersion(manifest)}).");
        }

        ProgressPercent?.Invoke(this, 100);
        SaveMirrorStatsQuiet(force: true);
    }

    private void ApplyDeletesAndPrunes(PackManifest manifest, HashSet<string> wanted, PackState state, object stateLock)
    {
        var deletes = (manifest.Delete is { Length: > 0 } ? manifest.Delete : null);
        var prunes = (manifest.Prune is { Length: > 0 } ? manifest.Prune : null);

        if (deletes is { Length: > 0 })
        {
            foreach (var raw in deletes)
            {
                var rel = NormalizeRelPath(raw);
                if (string.IsNullOrWhiteSpace(rel)) continue;

                if (raw.Trim().EndsWith("/") || raw.Trim().EndsWith("\\"))
                    continue;

                if (!IsAllowedDest(rel)) continue;
                if (IsUserMutableDest(rel)) continue;

                var local = ToLocalPath(rel);
                try
                {
                    if (File.Exists(local))
                    {
                        File.Delete(local);
                        Log?.Invoke(this, $"Сборка: delete удалён файл {rel}");
                    }
                    else if (Directory.Exists(local))
                    {
                        Directory.Delete(local, recursive: true);
                        Log?.Invoke(this, $"Сборка: delete удалена директория {rel}");
                    }

                    lock (stateLock)
                    {
                        state.Files.Remove(rel);
                    }
                }
                catch { }
            }
        }

        var rootsRaw = new List<string>();

        if (prunes is { Length: > 0 })
            rootsRaw.AddRange(prunes);

        if (deletes is { Length: > 0 })
        {
            foreach (var r in deletes)
            {
                if (r is null) continue;
                var t = r.Trim();
                if (t.EndsWith("/") || t.EndsWith("\\"))
                    rootsRaw.Add(t);
            }
        }

        var roots = rootsRaw
            .Select(NormalizeRoot)
            .Where(r =>
                !string.IsNullOrWhiteSpace(r) &&
                IsAllowedDest(r) &&
                !IsUserMutableDest(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (roots.Length > 0)
            PruneExtras(roots, wanted);
    }

    private enum DownloadApplyResult { Applied, SavedPending }

    private async Task ProcessOneFileAsync(
        string destRel,
        PackFile file,
        ConcurrentQueue<Exception> errors,
        ConcurrentQueue<string> pendings,
        PackState state,
        object stateLock,
        string activeBaseUrl,
        string[]? manifestMirrors,
        string[] rootMirrors,
        Action<long> addBytes,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            if (!IsAllowedDest(destRel))
                throw new InvalidOperationException($"Manifest пытается записать запрещённый путь: {destRel}");

            if (!IsValidFileRelPath(destRel))
                throw new InvalidOperationException($"Manifest содержит путь, который выглядит как директория/невалидный файл: {destRel}");

            if (!IsValidSha256(file.Sha256))
                throw new InvalidOperationException($"Invalid sha256 in manifest for {destRel}");

            if (file.Size < 0)
                throw new InvalidOperationException($"Manifest содержит отрицательный size для {destRel}");

            if (file.Size == 0 && !IsEmptySha(file.Sha256))
                throw new InvalidOperationException($"Manifest содержит size=0, но sha256 не пустого файла: {destRel}");

            var localPath = ToLocalPath(destRel);

            if (!IsUnderGameDir(localPath))
                throw new InvalidOperationException($"Unsafe path in manifest: {file.Path}");

            // 1) сначала пробуем применить pending (если он валиден)
            var (pendingApplied, hasValidPendingButLocked) = await TryApplyPendingAsync(destRel, localPath, file.Sha256, ct).ConfigureAwait(false);

            // Если pending валиден, но файл занят — НЕ качаем заново (экономим трафик), просто ждём следующего прогона
            if (!pendingApplied && hasValidPendingButLocked)
            {
                pendings.Enqueue(destRel);
                Log?.Invoke(this, $"Сборка: {destRel} — есть валидный .pending, но файл занят. Применю позже.");
                addBytes(Math.Max(0, file.Size));
                return;
            }

            // 2) Проверка существующего файла
            var check = await CheckFileAsync(destRel, localPath, file, state, stateLock, ct).ConfigureAwait(false);
            if (check == FileCheckResult.Match)
            {
                addBytes(Math.Max(0, file.Size));
                return;
            }

            // 3) User-mutable: не перезаписываем если уже есть
            if (IsUserMutableDest(destRel) && File.Exists(localPath))
            {
                Log?.Invoke(this, $"Сборка: {destRel} изменён/кастомный — оставляю как есть.");
                addBytes(Math.Max(0, file.Size));
                return;
            }

            // 4) Пустые файлы не качаем
            if (file.Size == 0 && IsEmptySha(file.Sha256))
            {
                await EnsureEmptyFileAsync(localPath, destRel, ct).ConfigureAwait(false);

                lock (stateLock)
                {
                    UpdatePackStateEntry(state, destRel, localPath, file.Sha256);
                }

                Log?.Invoke(this, $"Сборка: OK (empty) {destRel}");
                return;
            }

            var blobRel = GetBlobRelPath(file);

            var outcome = await DownloadFileFromMirrorsAsync(
                activeBaseUrl: activeBaseUrl,
                blobRel: blobRel,
                file: file,
                localPath: localPath,
                destRel: destRel,
                manifestMirrors: manifestMirrors,
                rootMirrors: rootMirrors,
                ct: ct,
                onBytes: bytes => addBytes(bytes)).ConfigureAwait(false);

            if (outcome == DownloadApplyResult.Applied)
            {
                lock (stateLock)
                {
                    UpdatePackStateEntry(state, destRel, localPath, file.Sha256);
                }

                Log?.Invoke(this, $"Сборка: OK {destRel}");
            }
            else
            {
                // pending сохранён, но state НЕ обновляем (иначе будет ложный Match)
                pendings.Enqueue(destRel);
                Log?.Invoke(this, $"Сборка: PENDING {destRel} (файл занят, применю позже)");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // делаем ошибку информативнее
            errors.Enqueue(new InvalidOperationException($"Ошибка обработки файла {destRel}: {ex.Message}", ex));
        }
    }

    private enum FileCheckResult { MissingOrDifferent, Match }

    private async Task<FileCheckResult> CheckFileAsync(
        string rel,
        string localPath,
        PackFile file,
        PackState state,
        object stateLock,
        CancellationToken ct)
    {
        if (!File.Exists(localPath))
            return FileCheckResult.MissingOrDifferent;

        try
        {
            var info = new FileInfo(localPath);

            if (file.Size > 0 && info.Length != file.Size)
                return FileCheckResult.MissingOrDifferent;

            lock (stateLock)
            {
                if (state.Files.TryGetValue(rel, out var cached) &&
                    cached.Size == info.Length &&
                    cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
                    cached.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return FileCheckResult.Match;
                }
            }

            if (file.Size == 0 && IsEmptySha(file.Sha256))
            {
                if (info.Length == 0)
                {
                    lock (stateLock)
                    {
                        UpdatePackStateEntry(state, rel, localPath, file.Sha256);
                    }
                    return FileCheckResult.Match;
                }

                return FileCheckResult.MissingOrDifferent;
            }

            var sha = await ComputeSha256Async(localPath, ct).ConfigureAwait(false);
            if (!sha.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                return FileCheckResult.MissingOrDifferent;

            lock (stateLock)
            {
                UpdatePackStateEntry(state, rel, localPath, file.Sha256);
            }

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
                Sha256 = (sha256 ?? "").Trim().ToLowerInvariant(),
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks
            };
        }
        catch { }
    }

    private PackState LoadPackState()
    {
        try
        {
            lock (_packStateIoLock)
            {
                var path = Path.Combine(_path.BasePath, "launcher", PackStateFileName);
                if (!File.Exists(path))
                    return new PackState();

                var json = File.ReadAllText(path, Encoding.UTF8);
                var st = JsonSerializer.Deserialize(json, PackJsonContext.Default.PackState) ?? new PackState();

                if (st.Files is null)
                    st.Files = new Dictionary<string, PackStateEntry>(StringComparer.OrdinalIgnoreCase);
                else if (!Equals(st.Files.Comparer, StringComparer.OrdinalIgnoreCase))
                    st.Files = new Dictionary<string, PackStateEntry>(st.Files, StringComparer.OrdinalIgnoreCase);

                return st;
            }
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
            lock (_packStateIoLock)
            {
                var dir = Path.Combine(_path.BasePath, "launcher");
                Directory.CreateDirectory(dir);

                var path = Path.Combine(dir, PackStateFileName);
                var tmp = path + ".tmp";

                var json = JsonSerializer.Serialize(state, PackJsonContext.Default.PackState);
                File.WriteAllText(tmp, json, Encoding.UTF8);

                ReplaceOrMoveAtomic(tmp, path);
                TryDeleteQuiet(tmp);
            }
        }
        catch { }
    }

    internal sealed class PackState
    {
        public string? PackId { get; set; }
        public string? ManifestVersion { get; set; }
        public Dictionary<string, PackStateEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class PackStateEntry
    {
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
        public long LastWriteUtcTicks { get; set; }
    }

    private static string GetFinalBaseUrlFromResponse(HttpResponseMessage resp, string fallbackBaseUrl)
    {
        try
        {
            var uri = resp.RequestMessage?.RequestUri;
            if (uri is null)
                return NormalizeAbsoluteBaseUrl(fallbackBaseUrl);

            var baseUri = new Uri(uri, "./");
            return NormalizeAbsoluteBaseUrl(baseUri.ToString());
        }
        catch
        {
            return NormalizeAbsoluteBaseUrl(fallbackBaseUrl);
        }
    }

    // =========================
    // Manifest download
    // =========================

    private async Task<(string activeBaseUrl, PackManifest manifest)> DownloadManifestFromMirrorsAsync(string[] mirrors, CancellationToken ct)
    {
        if (mirrors is null || mirrors.Length == 0)
            throw new InvalidOperationException("Mirrors list is empty");

        var list = mirrors
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (list.Length == 0)
            throw new InvalidOperationException("Mirrors list is empty after normalize");

        var primary = list[0];

        _primaryBaseUrlThisRun = primary;
        _primaryOkThisRun = false;
        _fastCdnFallbackThisRun = null;

        var primaryRes =
            await TryFetchManifestFromBaseAsync(primary, ct, MirrorPrimaryTimeoutSec1, countFail: false).ConfigureAwait(false)
            ?? await TryFetchManifestFromBaseAsync(primary, ct, MirrorPrimaryTimeoutSec2, countFail: false).ConfigureAwait(false);

        if (primaryRes is not null)
        {
            _primaryOkThisRun = true;

            if (IsSfOrSelectel(primaryRes.Value.activeBaseUrl))
                _fastCdnFallbackThisRun = primaryRes.Value.activeBaseUrl;

            SaveMirrorStatsQuiet(force: true);
            return primaryRes.Value;
        }

        TouchMirrorFailManifest(primary);

        var fallbacksAll = list
            .Where(u => !u.Equals(primary, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (fallbacksAll.Length == 0)
            throw new InvalidOperationException("Primary mirror failed and no fallbacks are available.");

        var cdnFallbacks = fallbacksAll.Where(IsSfOrSelectel).ToArray();
        if (cdnFallbacks.Length > 0)
        {
            cdnFallbacks = OrderMirrorsByStats(cdnFallbacks, MirrorScoreKind.Manifest);

            var cdnRes = await RaceManifestAsync(cdnFallbacks, ct, MirrorFallbackTimeoutSec).ConfigureAwait(false);
            if (cdnRes is not null)
            {
                _fastCdnFallbackThisRun = cdnRes.Value.activeBaseUrl;
                SaveMirrorStatsQuiet(force: true);
                return cdnRes.Value;
            }
        }

        var otherFallbacks = fallbacksAll.Where(u => !IsSfOrSelectel(u)).ToArray();
        if (otherFallbacks.Length == 0)
            throw new InvalidOperationException("Не удалось скачать manifest: CDN зеркала (SF/Selectel) недоступны, других fallback'ов нет.");

        otherFallbacks = OrderMirrorsByStats(otherFallbacks, MirrorScoreKind.Manifest);

        var otherRes = await RaceManifestAsync(otherFallbacks, ct, MirrorFallbackTimeoutSec).ConfigureAwait(false);
        if (otherRes is not null)
        {
            if (IsSfOrSelectel(otherRes.Value.activeBaseUrl))
                _fastCdnFallbackThisRun = otherRes.Value.activeBaseUrl;

            SaveMirrorStatsQuiet(force: true);
            return otherRes.Value;
        }

        throw new InvalidOperationException("Не удалось скачать manifest ни с одного зеркала (primary + fallbacks).");
    }

    private async Task<(string activeBaseUrl, PackManifest manifest)?> RaceManifestAsync(string[] fallbacks, CancellationToken ct, int timeoutSec)
    {
        if (fallbacks is null || fallbacks.Length == 0)
            return null;

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var tasks = fallbacks
            .Select(b => TryFetchManifestFromBaseAsync(b, raceCts.Token, timeoutSec))
            .ToList();

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);

            try
            {
                var res = await finished.ConfigureAwait(false);
                if (res is not null)
                {
                    raceCts.Cancel();
                    try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
                    return res.Value;
                }
            }
            catch { }
        }

        return null;
    }

    private async Task<(string activeBaseUrl, PackManifest manifest)?> TryFetchManifestFromBaseAsync(
        string baseUrl,
        CancellationToken ct,
        int timeoutSec,
        bool countFail = true)
    {
        baseUrl = NormalizeAbsoluteBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var url = CombineUrl(baseUrl, ManifestFileName);

        try
        {
            Log?.Invoke(this, $"Сборка: читаю manifest: {url}");

            var sw = Stopwatch.StartNew();

            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                if (countFail) TouchMirrorFailManifest(baseUrl);
                Log?.Invoke(this, $"Сборка: manifest HTTP {(int)resp.StatusCode} ({baseUrl})");
                return null;
            }

            var media = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (media.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                if (countFail) TouchMirrorFailManifest(baseUrl);
                return null;
            }

            var len = resp.Content.Headers.ContentLength;
            if (len.HasValue && len.Value > MirrorProbeMaxManifestBytes)
            {
                if (countFail) TouchMirrorFailManifest(baseUrl);
                return null;
            }

            var pinnedBaseUrl = GetFinalBaseUrlFromResponse(resp, baseUrl);

            await using var stream = await resp.Content.ReadAsStreamAsync(reqCts.Token).ConfigureAwait(false);

            PackManifest? manifest;
            try
            {
                manifest = await JsonSerializer.DeserializeAsync(stream, PackJsonContext.Default.PackManifest, reqCts.Token).ConfigureAwait(false);
            }
            catch
            {
                if (countFail) TouchMirrorFailManifest(baseUrl);
                return null;
            }

            if (manifest is null)
            {
                if (countFail) TouchMirrorFailManifest(baseUrl);
                return null;
            }

            if (manifest.Mirrors is { Length: > 0 })
            {
                manifest.Mirrors = manifest.Mirrors
                    .Select(NormalizeAbsoluteBaseUrl)
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            sw.Stop();

            var manifestBytes = resp.Content.Headers.ContentLength ?? (64 * 1024);
            var score = NormalizeLatencyForSize(sw.Elapsed.TotalMilliseconds, manifestBytes);

            TouchMirrorOkManifest(pinnedBaseUrl, score);

            Log?.Invoke(this, $"Сборка: выбрано зеркало: {pinnedBaseUrl}");
            return (pinnedBaseUrl, manifest);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (countFail) TouchMirrorFailManifest(baseUrl);
            Log?.Invoke(this, $"Сборка: таймаут {timeoutSec}s ({baseUrl})");
            return null;
        }
        catch (Exception ex)
        {
            if (countFail) TouchMirrorFailManifest(baseUrl);
            Log?.Invoke(this, $"Сборка: зеркало недоступно ({baseUrl}) — {ex.Message}");
            return null;
        }
    }

    private sealed class DownloadHttpException : Exception
    {
        public HttpStatusCode StatusCode { get; }
        public string Url { get; }

        public DownloadHttpException(HttpStatusCode code, string url, string message)
            : base(message)
        {
            StatusCode = code;
            Url = url;
        }

        public bool IsClientNotFound =>
            StatusCode == HttpStatusCode.NotFound ||
            StatusCode == HttpStatusCode.Gone;

        public bool IsClientError =>
            ((int)StatusCode >= 400 && (int)StatusCode < 500);

        public bool IsTransient =>
            StatusCode == (HttpStatusCode)429 ||
            StatusCode == HttpStatusCode.RequestTimeout ||
            StatusCode == HttpStatusCode.BadGateway ||
            StatusCode == HttpStatusCode.ServiceUnavailable ||
            StatusCode == HttpStatusCode.GatewayTimeout ||
            ((int)StatusCode >= 500 && (int)StatusCode <= 599);
    }

    private async Task<DownloadApplyResult> DownloadFileFromMirrorsAsync(
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
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var ordered = new List<string>();

        var primary = NormalizeAbsoluteBaseUrl(_primaryBaseUrlThisRun);
        var active = NormalizeAbsoluteBaseUrl(activeBaseUrl);
        var fastCdn = NormalizeAbsoluteBaseUrl(_fastCdnFallbackThisRun);

        // 1) active (откуда был скачан manifest)
        if (!string.IsNullOrWhiteSpace(active))
            ordered.Add(active);

        // 2) быстрый CDN-фоллбек
        if (!string.IsNullOrWhiteSpace(fastCdn))
            ordered.Add(fastCdn);

        // 3) primary: если отработал по manifest — ставим наверх, иначе оставим в хвосте (но НЕ исключаем полностью)
        if (!string.IsNullOrWhiteSpace(primary))
        {
            if (_primaryOkThisRun)
                ordered.Insert(0, primary);
            else
                ordered.Add(primary);
        }

        var rest = mirrors
            .Where(m =>
                !m.Equals(primary, StringComparison.OrdinalIgnoreCase) &&
                !m.Equals(active, StringComparison.OrdinalIgnoreCase) &&
                !m.Equals(fastCdn, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var restCdn = rest.Where(IsSfOrSelectel).ToArray();
        var restOther = rest.Where(m => !IsSfOrSelectel(m)).ToArray();

        restCdn = OrderMirrorsByStats(restCdn, MirrorScoreKind.Blob);
        restOther = OrderMirrorsByStats(restOther, MirrorScoreKind.Blob);

        ordered.AddRange(restCdn);
        ordered.AddRange(restOther);

        ordered = ordered
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

            var candidates = BuildDownloadCandidates(destRel, blobRel);

            try
            {
                foreach (var rel in candidates)
                {
                    ct.ThrowIfCancellationRequested();

                    var url = CombineUrl(baseUrl, rel);
                    Log?.Invoke(this, $"Сборка: download {destRel} <- {baseUrl} ({FormatBytes(file.Size)}) [{rel}]");

                    var sw = Stopwatch.StartNew();
                    try
                    {
                        var outcome = await DownloadToFileAsync(url, localPath, file.Size, file.Sha256, destRel, ct, Counted).ConfigureAwait(false);
                        sw.Stop();

                        TouchMirrorOkBlob(baseUrl, NormalizeLatencyForSize(sw.Elapsed.TotalMilliseconds, file.Size));
                        return outcome;
                    }
                    catch (DownloadHttpException hex)
                    {
                        last = hex;

                        if (hex.IsClientError && !hex.IsTransient)
                            continue;

                        throw;
                    }
                }

                throw last ?? new InvalidOperationException("Не удалось скачать файл ни одним способом на данном зеркале.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                TouchMirrorFailBlob(baseUrl);

                if (attemptBytes > 0)
                    onBytes(-attemptBytes);

                Log?.Invoke(this, $"Сборка: ошибка скачивания (зеркало {baseUrl}) — {ex.Message}");
            }
        }

        throw new InvalidOperationException($"Не удалось скачать файл: {destRel}", last);
    }

    private static List<string> BuildDownloadCandidates(string destRel, string blobRel)
    {
        var list = new List<string>(2);

        destRel = destRel.Replace('\\', '/');
        blobRel = blobRel.Replace('\\', '/');

        var isConfig = destRel.StartsWith("config/", StringComparison.OrdinalIgnoreCase);

        if (isConfig)
        {
            list.Add(destRel);
            if (!string.Equals(blobRel, destRel, StringComparison.OrdinalIgnoreCase))
                list.Add(blobRel);
        }
        else
        {
            list.Add(blobRel);
            if (!string.Equals(destRel, blobRel, StringComparison.OrdinalIgnoreCase))
                list.Add(destRel);
        }

        return list
            .Select(NormalizeRelPath)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static long CalcSizeTolerance(long expectedSize)
    {
        if (expectedSize <= 0) return 0;
        // 5% или минимум 1 MiB
        var tol = Math.Max(1L * 1024 * 1024, expectedSize / 20);
        return tol;
    }

    private async Task<DownloadApplyResult> DownloadToFileAsync(
        string url,
        string localPath,
        long expectedSize,
        string expectedSha256,
        string destRel,
        CancellationToken ct,
        Action<long> onBytes)
    {
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var tmp = localPath + ".tmp";
        TryDeleteQuiet(tmp);

        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts.CancelAfter(TimeSpan.FromMinutes(10));

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new DownloadHttpException(resp.StatusCode, url, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} ({url})");

        var media = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (media.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Зеркало вернуло HTML вместо файла.");

        var tol = CalcSizeTolerance(expectedSize);

        var len = resp.Content.Headers.ContentLength;
        if (expectedSize > 0 && len.HasValue && len.Value > 0)
        {
            var threshold = Math.Min(16_384L, Math.Max(1L, expectedSize / 10));
            if (len.Value < threshold)
                throw new InvalidOperationException(
                    $"Зеркало вернуло подозрительно маленький ответ (Content-Length={len.Value}, ожидается около {expectedSize}).");

            if (len.Value > expectedSize + tol)
                throw new InvalidOperationException(
                    $"Зеркало заявило слишком большой Content-Length={len.Value}, ожидается {expectedSize} (+{tol} допуск).");
        }

        using var sha = SHA256.Create();
        var buffer = new byte[128 * 1024];

        try
        {
            await Provisioning(resp, reqCts.Token, tmp, buffer, sha, onBytes, expectedSize, tol).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteQuiet(tmp);
            throw;
        }

        // если size задан — дополнительно проверим фактический размер tmp
        if (expectedSize > 0)
        {
            try
            {
                var info = new FileInfo(tmp);
                if (info.Length != expectedSize)
                {
                    TryDeleteQuiet(tmp);
                    throw new InvalidOperationException($"Размер файла не совпал: expected {expectedSize}, got {info.Length} ({destRel})");
                }
            }
            catch
            {
                TryDeleteQuiet(tmp);
                throw;
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteQuiet(tmp);
            throw new InvalidOperationException($"SHA256 mismatch: expected {expectedSha256}, got {actual}");
        }

        var ok = await TryMoveOrReplaceWithRetryAsync(tmp, localPath, ct, attempts: 20, delayMs: 200).ConfigureAwait(false);
        if (ok)
        {
            TryDeleteQuiet(tmp);
            return DownloadApplyResult.Applied;
        }

        // pending имеет смысл только для НЕ user-mutable путей (иначе мы всё равно не перезаписываем)
        if (IsPendingAllowedDest(destRel))
        {
            var pending = localPath + ".pending";
            var pendingOk = await TryMoveOrReplaceWithRetryAsync(tmp, pending, ct, attempts: 10, delayMs: 200).ConfigureAwait(false);
            if (pendingOk)
            {
                TryDeleteQuiet(tmp);
                Log?.Invoke(this, $"Сборка: файл занят, сохранил pending: {destRel}");
                return DownloadApplyResult.SavedPending;
            }

            TryDeleteQuiet(tmp);
            throw new IOException($"Файл занят и не удалось сохранить pending: {destRel}");
        }

        TryDeleteQuiet(tmp);
        throw new IOException($"Не удалось записать файл сборки (занят другим процессом): {destRel}");
    }

    private static async Task Provisioning(
        HttpResponseMessage resp,
        CancellationToken token,
        string tmp,
        byte[] buffer,
        SHA256 sha,
        Action<long> onBytes,
        long expectedSize,
        long tolerance)
    {
        await using var input = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(
            tmp,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        long downloaded = 0;

        int read;
        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
        {
            if (expectedSize > 0)
            {
                // если сервер льёт больше ожидаемого — останавливаем (DoS/битое зеркало)
                if (downloaded + read > expectedSize + tolerance)
                    throw new InvalidOperationException($"Ответ слишком большой: > {expectedSize}+{tolerance} байт");
            }

            await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
            sha.TransformBlock(buffer, 0, read, null, 0);

            downloaded += read;
            onBytes(read);
        }

        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task EnsureEmptyFileAsync(string localPath, string destRel, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(localPath))
        {
            try
            {
                var info = new FileInfo(localPath);
                if (info.Length == 0) return;
            }
            catch { }
        }

        var tmp = localPath + ".tmp";
        TryDeleteQuiet(tmp);

        await using (var fs = new FileStream(
            tmp,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4 * 1024,
            options: FileOptions.Asynchronous))
        {
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }

        var ok = await TryMoveOrReplaceWithRetryAsync(tmp, localPath, ct, attempts: 10, delayMs: 200).ConfigureAwait(false);
        if (ok)
        {
            TryDeleteQuiet(tmp);
            return;
        }

        TryDeleteQuiet(tmp);
        throw new IOException($"Не удалось создать пустой файл: {destRel}");
    }

    private async Task<(bool applied, bool validButLocked)> TryApplyPendingAsync(string destRel, string localPath, string expectedSha256, CancellationToken ct)
    {
        var pending = localPath + ".pending";
        if (!File.Exists(pending))
            return (false, false);

        // проверяем валидность pending
        try
        {
            var sha = await ComputeSha256Async(pending, ct).ConfigureAwait(false);
            if (!sha.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteQuiet(pending);
                return (false, false);
            }
        }
        catch
        {
            return (false, false);
        }

        // пытаемся применить
        var ok = await TryMoveOrReplaceWithRetryAsync(pending, localPath, ct, attempts: 10, delayMs: 200).ConfigureAwait(false);
        if (ok)
        {
            TryDeleteQuiet(pending);
            Log?.Invoke(this, $"Сборка: применил pending для {destRel}");
            return (true, false);
        }

        // pending валиден, но файл занят
        return (false, true);
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

            try
            {
                foreach (var d in Directory.EnumerateDirectories(localRoot, "*", SearchOption.AllDirectories)
                             .OrderByDescending(x => x.Length))
                {
                    if (!Directory.EnumerateFileSystemEntries(d).Any())
                        Directory.Delete(d, recursive: false);
                }
            }
            catch { }
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

    private static bool IsPendingAllowedDest(string destRel)
    {
        destRel = destRel.Replace('\\', '/');
        if (!IsAllowedDest(destRel)) return false;

        // pending НЕ нужен для user-mutable путей (мы их не перезаписываем)
        return !IsUserMutableDest(destRel);
    }

    private static bool IsUserMutableDest(string destRel)
    {
        // По твоей просьбе: не трогаем config/defaultconfigs/resourcepacks/shaderpacks.
        // kubejs НЕ добавляем.
        destRel = destRel.Replace('\\', '/');

        return destRel.StartsWith("config/", StringComparison.OrdinalIgnoreCase)
            || destRel.StartsWith("defaultconfigs/", StringComparison.OrdinalIgnoreCase)
            || destRel.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase)
            || destRel.StartsWith("shaderpacks/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidFileRelPath(string destRel)
    {
        destRel = destRel.Replace('\\', '/');

        if (string.IsNullOrWhiteSpace(destRel)) return false;
        if (destRel.EndsWith("/")) return false;

        if (!destRel.Contains('/')) return false;

        var name = destRel.Split('/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name == "." || name == "..") return false;

        return true;
    }

    private static bool IsEmptySha(string sha256)
        => string.Equals((sha256 ?? "").Trim(), Sha256Empty, StringComparison.OrdinalIgnoreCase);

    private static string GetBlobRelPath(PackFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.Blob))
        {
            var rel = NormalizeRelPath(file.Blob);
            if (string.IsNullOrWhiteSpace(rel))
                throw new InvalidOperationException("Invalid blob path in manifest");
            return rel;
        }

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

        if (p.Contains(':')) return "";
        if (p.StartsWith("~")) return "";

        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";

        foreach (var seg in parts)
        {
            if (seg == "." || seg == "..")
                return "";
        }

        return string.Join("/", parts);
    }

    private static string NormalizeAbsoluteBaseUrl(string? url)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url)) return "";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "";

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return "";

        var b = new UriBuilder(uri)
        {
            Query = "",
            Fragment = ""
        };

        var path = b.Path ?? "/";
        if (!path.EndsWith("/")) path += "/";
        b.Path = path;

        return b.Uri.ToString();
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
        baseUrl = NormalizeAbsoluteBaseUrl(baseUrl);
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
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = WebRequest.DefaultWebProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 8
        };

        var http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        try
        {
            var ua = LauncherIdentity.UserAgent;
            if (!string.IsNullOrWhiteSpace(ua))
                http.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        }
        catch
        {
            try
            {
                http.DefaultRequestHeaders.UserAgent.Clear();
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"LegendBornLauncher/{LauncherIdentity.InformationalVersion}");
            }
            catch { }
        }

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

    private enum MirrorScoreKind { Manifest, Blob }

    private sealed class MirrorStatsRoot
    {
        public int Version { get; set; } = 2;
        public Dictionary<string, MirrorStat> Mirrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class MirrorStat
    {
        public double ManifestEwmaScore { get; set; } = 0;
        public int ManifestOk { get; set; } = 0;
        public int ManifestFail { get; set; } = 0;
        public long ManifestLastOkUnix { get; set; } = 0;

        public double BlobEwmaScore { get; set; } = 0;
        public int BlobOk { get; set; } = 0;
        public int BlobFail { get; set; } = 0;
        public long BlobLastOkUnix { get; set; } = 0;

        // legacy
        public double EwmaScore { get; set; } = 0;
        public int Ok { get; set; } = 0;
        public int Fail { get; set; } = 0;
        public long LastOkUnix { get; set; } = 0;
    }

    private MirrorStatsRoot LoadMirrorStats()
    {
        try
        {
            lock (_mirrorStatsIoLock)
            {
                var path = MirrorStatsPath;
                if (!File.Exists(path))
                    return new MirrorStatsRoot();

                var json = File.ReadAllText(path, Encoding.UTF8);
                var root = JsonSerializer.Deserialize<MirrorStatsRoot>(json, MirrorStatsJsonOptions) ?? new MirrorStatsRoot();

                if (root.Mirrors is null)
                    root.Mirrors = new Dictionary<string, MirrorStat>(StringComparer.OrdinalIgnoreCase);

                if (!Equals(root.Mirrors.Comparer, StringComparer.OrdinalIgnoreCase))
                    root.Mirrors = new Dictionary<string, MirrorStat>(root.Mirrors, StringComparer.OrdinalIgnoreCase);

                foreach (var kv in root.Mirrors)
                {
                    var s = kv.Value;
                    if (s is null) continue;

                    if (s.EwmaScore > 0 && s.ManifestEwmaScore <= 0 && s.BlobEwmaScore <= 0)
                    {
                        s.ManifestEwmaScore = s.EwmaScore;
                        s.BlobEwmaScore = s.EwmaScore;
                    }

                    if (s.Ok > 0 && s.ManifestOk == 0 && s.BlobOk == 0)
                        s.BlobOk = s.Ok;

                    if (s.Fail > 0 && s.ManifestFail == 0 && s.BlobFail == 0)
                        s.BlobFail = s.Fail;

                    if (s.LastOkUnix > 0 && s.ManifestLastOkUnix == 0 && s.BlobLastOkUnix == 0)
                        s.BlobLastOkUnix = s.LastOkUnix;

                    s.EwmaScore = 0;
                    s.Ok = 0;
                    s.Fail = 0;
                    s.LastOkUnix = 0;
                }

                return root;
            }
        }
        catch
        {
            return new MirrorStatsRoot();
        }
    }

    private void SaveMirrorStatsQuiet(bool force = false)
    {
        try
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (!force)
            {
                var last = Interlocked.Read(ref _mirrorStatsLastSaveUnixMs);
                if (nowMs - last < MirrorStatsSaveMinIntervalMs)
                    return;

                Interlocked.Exchange(ref _mirrorStatsLastSaveUnixMs, nowMs);
            }
            else
            {
                Interlocked.Exchange(ref _mirrorStatsLastSaveUnixMs, nowMs);
            }

            MirrorStatsRoot snapshot;
            lock (_mirrorStatsLock)
            {
                snapshot = new MirrorStatsRoot
                {
                    Version = _mirrorStats.Version,
                    Mirrors = new Dictionary<string, MirrorStat>(_mirrorStats.Mirrors.Count, StringComparer.OrdinalIgnoreCase)
                };

                foreach (var kv in _mirrorStats.Mirrors)
                {
                    var v = kv.Value ?? new MirrorStat();
                    snapshot.Mirrors[kv.Key] = new MirrorStat
                    {
                        ManifestEwmaScore = v.ManifestEwmaScore,
                        ManifestOk = v.ManifestOk,
                        ManifestFail = v.ManifestFail,
                        ManifestLastOkUnix = v.ManifestLastOkUnix,

                        BlobEwmaScore = v.BlobEwmaScore,
                        BlobOk = v.BlobOk,
                        BlobFail = v.BlobFail,
                        BlobLastOkUnix = v.BlobLastOkUnix,

                        EwmaScore = 0,
                        Ok = 0,
                        Fail = 0,
                        LastOkUnix = 0
                    };
                }
            }

            var json = JsonSerializer.Serialize(snapshot, MirrorStatsJsonOptions);

            lock (_mirrorStatsIoLock)
            {
                var dir = Path.GetDirectoryName(MirrorStatsPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var tmp = MirrorStatsPath + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);

                ReplaceOrMoveAtomic(tmp, MirrorStatsPath);
                TryDeleteQuiet(tmp);
            }
        }
        catch { }
    }

    private static double Ewma(double prev, double next)
        => prev <= 0 ? next : (prev * (1.0 - MirrorEwmaAlpha) + next * MirrorEwmaAlpha);

    private MirrorStat GetOrCreateMirrorStatLocked(string baseUrl)
    {
        if (!_mirrorStats.Mirrors.TryGetValue(baseUrl, out var s) || s is null)
        {
            s = new MirrorStat();
            _mirrorStats.Mirrors[baseUrl] = s;
        }
        return s;
    }

    private void TouchMirrorOkManifest(string baseUrl, double score)
    {
        baseUrl = NormalizeAbsoluteBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl)) return;

        lock (_mirrorStatsLock)
        {
            var s = GetOrCreateMirrorStatLocked(baseUrl);
            s.ManifestEwmaScore = Ewma(s.ManifestEwmaScore, score);
            s.ManifestOk++;
            s.ManifestLastOkUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        SaveMirrorStatsQuiet();
    }

    private void TouchMirrorFailManifest(string baseUrl)
    {
        baseUrl = NormalizeAbsoluteBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl)) return;

        lock (_mirrorStatsLock)
        {
            var s = GetOrCreateMirrorStatLocked(baseUrl);
            s.ManifestFail++;
        }

        SaveMirrorStatsQuiet();
    }

    private void TouchMirrorOkBlob(string baseUrl, double score)
    {
        baseUrl = NormalizeAbsoluteBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl)) return;

        lock (_mirrorStatsLock)
        {
            var s = GetOrCreateMirrorStatLocked(baseUrl);
            s.BlobEwmaScore = Ewma(s.BlobEwmaScore, score);
            s.BlobOk++;
            s.BlobLastOkUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        SaveMirrorStatsQuiet();
    }

    private void TouchMirrorFailBlob(string baseUrl)
    {
        baseUrl = NormalizeAbsoluteBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl)) return;

        lock (_mirrorStatsLock)
        {
            var s = GetOrCreateMirrorStatLocked(baseUrl);
            s.BlobFail++;
        }

        SaveMirrorStatsQuiet();
    }

    private string[] OrderMirrorsByStats(string[] mirrors, MirrorScoreKind kind)
    {
        lock (_mirrorStatsLock)
        {
            return mirrors
                .Select(NormalizeAbsoluteBaseUrl)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m =>
                {
                    if (_mirrorStats.Mirrors.TryGetValue(m, out var s) && s is not null)
                    {
                        var score = kind == MirrorScoreKind.Manifest ? s.ManifestEwmaScore : s.BlobEwmaScore;
                        if (score > 0)
                            return score;
                    }

                    return 99999.0;
                })
                .ToArray();
        }
    }

    private static double NormalizeLatencyForSize(double ms, long bytes)
    {
        if (ms <= 0) ms = 1;
        if (bytes <= 0) return ms;

        var mib = bytes / (1024.0 * 1024.0);
        if (mib < 1) mib = 1;
        return ms / mib;
    }

    private static string GetManifestIdentity(PackManifest m)
    {
        var pv = (m.PackVersion ?? m.Version ?? "").Trim();
        var build = m.Build.HasValue ? m.Build.Value.ToString() : "";
        if (!string.IsNullOrWhiteSpace(pv) && !string.IsNullOrWhiteSpace(build))
            return $"{pv}+{build}";
        return !string.IsNullOrWhiteSpace(pv) ? pv : (m.Version ?? "unknown");
    }

    private static string GetManifestDisplayVersion(PackManifest m)
    {
        var id = GetManifestIdentity(m);
        var ch = (m.Channel ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(ch))
            return $"{id} ({ch})";
        return id;
    }

    // ===== Manifest models =====

    public sealed class PackManifest
    {
        [JsonPropertyName("packId")]
        public string? PackId { get; set; }

        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        [JsonPropertyName("packVersion")]
        public string? PackVersion { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("build")]
        public int? Build { get; set; }

        [JsonPropertyName("minecraft")]
        public string? Minecraft { get; set; }

        [JsonPropertyName("loader")]
        public string? Loader { get; set; }

        [JsonPropertyName("loaderVersion")]
        public string? LoaderVersion { get; set; }

        [JsonPropertyName("files")]
        public List<PackFile>? Files { get; set; }

        [JsonPropertyName("mirrors")]
        public string[]? Mirrors { get; set; }

        [JsonPropertyName("delete")]
        public string[]? Delete { get; set; }

        [JsonPropertyName("prune")]
        public string[]? Prune { get; set; }
    }

    public sealed class PackFile
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("blob")]
        public string? Blob { get; set; }
    }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    WriteIndented = true)]
[JsonSerializable(typeof(MinecraftService.PackManifest))]
[JsonSerializable(typeof(MinecraftService.PackFile))]
[JsonSerializable(typeof(MinecraftService.PackState))]
[JsonSerializable(typeof(MinecraftService.PackStateEntry))]
internal partial class PackJsonContext : JsonSerializerContext
{
}
