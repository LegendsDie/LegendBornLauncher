using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    private readonly LoaderInstaller _loaderInstaller;

    private static readonly HttpClient _http = CreateHttp();

    // ✅ Дефолтные зеркала паков (релизная логика):
    // 1) твой сайт (primary)
    // 2) Bunny (быстрый CDN)
    // 3) SourceForge master (fallback)
    //
    // Важно: baseUrl должен указывать на ПАПКУ, где лежат manifest.json и blobs/
    private static readonly string[] DefaultPackMirrors =
    {
        "https://legendborn.ru/launcher/pack/",
        "https://legendborn-pack.b-cdn.net/launcher/pack/",
        "https://master.dl.sourceforge.net/project/legendborn-pack/launcher/pack/"
    };

    private const string ManifestFileName = "manifest.json";
    private const string PackStateFileName = "pack_state.json";

    // ===== Параллелизм загрузки пака (v0.2.2) =====
    // Оптимально для сетевых загрузок: 4–8. Ставим безопасный дефолт.
    private const int PackMaxParallelDownloads = 6;

    // ===== Smart mirrors tuning (релизные параметры) =====
    private const int MirrorPrimaryTimeoutSec1 = 6;   // быстрый fail для РФ
    private const int MirrorPrimaryTimeoutSec2 = 12;  // второй шанс сайту
    private const int MirrorFallbackTimeoutSec = 18;  // таймаут для Bunny/SF
    private const int MirrorProbeMaxManifestBytes = 5 * 1024 * 1024; // safety (если Content-Length есть)
    private const double MirrorEwmaAlpha = 0.30;      // 30% новое, 70% старое

    // не спамим диск при больших паках
    private const int MirrorStatsSaveMinIntervalMs = 1500;
    private long _mirrorStatsLastSaveUnixMs;

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

    // ===== mirror stats (скорость/успех) =====
    private readonly object _mirrorStatsLock = new();
    private MirrorStatsRoot _mirrorStats = new();

    // I/O locks
    private readonly object _mirrorStatsIoLock = new();
    private readonly object _packStateIoLock = new();

    // tolerant json для mirror_stats.json
    private static readonly JsonSerializerOptions MirrorStatsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    // “состояние текущего запуска”
    private string? _primaryBaseUrlThisRun;
    private bool _primaryOkThisRun;

    private string MirrorStatsPath => Path.Combine(_path.BasePath, "launcher", "mirror_stats.json");

    public MinecraftService(string gameDir)
    {
        _path = new MinecraftPath(gameDir);
        Directory.CreateDirectory(_path.BasePath);

        _mirrorStats = LoadMirrorStats();

        _launcher = new MinecraftLauncher(_path);

        // LoaderInstaller (v0.2.2) — только NeoForge/Vanilla
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

    public async Task SyncPackAsync(string[]? packMirrors, CancellationToken ct = default)
    {
        var mirrors = ExpandAndOrderPackMirrors(packMirrors);

        if (mirrors.Length == 0)
            throw new InvalidOperationException("Pack mirrors list is empty");

        await EnsurePackUpToDateAsync(mirrors, ct).ConfigureAwait(false);
    }

    public async Task<string> PrepareAsync(
        string minecraftVersion,
        LoaderSpec loader,
        string[]? packMirrors,
        bool syncPack,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException("minecraftVersion is empty");

        var mc = minecraftVersion.Trim();
        var loaderType = (loader.Type ?? "vanilla").Trim().ToLowerInvariant();
        var loaderVersion = (loader.Version ?? "").Trim();
        var installerUrl = (loader.InstallerUrl ?? "").Trim();

        if (syncPack)
            await SyncPackAsync(packMirrors, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        Log?.Invoke(this, $"Minecraft: установка базовой версии {mc}...");
        await _launcher.InstallAsync(mc).ConfigureAwait(false); // библиотека не принимает ct
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

    public async Task<Process> BuildAndLaunchAsync(string version, string username, int ramMb, string? serverIp = null)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("version is empty", nameof(version));

        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("username is empty", nameof(username));

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
    // Mirrors ordering
    // =========================

    private static bool LooksLikeBunny(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        url = url.ToLowerInvariant();
        return url.Contains("b-cdn.net") || url.Contains("bunny");
    }

    private static bool LooksLikeSourceForge(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        url = url.ToLowerInvariant();
        return url.Contains("sourceforge.net") || url.Contains("master.dl.sourceforge.net");
    }

    private static string[] ExpandAndOrderPackMirrors(string[]? packMirrors)
    {
        var raw = (packMirrors is { Length: > 0 } ? packMirrors : DefaultPackMirrors)
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ЖЕЛЕЗНО: сайт первым. Далее Bunny/SF/прочие.
        return raw
            .OrderBy(u =>
            {
                var lu = u.ToLowerInvariant();
                if (lu.Contains("legendborn.ru")) return 0;
                if (LooksLikeBunny(lu)) return 1;
                if (LooksLikeSourceForge(lu)) return 2;
                return 3;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // =========================
    // Pack sync (v0.2.2: parallel download)
    // =========================

    private async Task EnsurePackUpToDateAsync(string[] mirrors, CancellationToken ct)
    {
        Log?.Invoke(this, "Сборка: проверка обновлений...");

        var (activeBaseUrl, manifest) = await DownloadManifestFromMirrorsAsync(mirrors, ct).ConfigureAwait(false);

        if (manifest.Files is null || manifest.Files.Count == 0)
        {
            Log?.Invoke(this, "Сборка: manifest пустой — пропускаю синхронизацию.");
            SaveMirrorStatsQuiet(force: true);
            return;
        }

        var state = LoadPackState();
        var stateLock = new object();

        var wanted = new HashSet<string>(
            manifest.Files.Select(f => NormalizeRelPath(f.Path)).Where(p => !string.IsNullOrWhiteSpace(p)),
            StringComparer.OrdinalIgnoreCase);

        long totalBytes = manifest.Files.Sum(f => Math.Max(0, f.Size));
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

        // Параллельная обработка файлов пака
        using var sem = new SemaphoreSlim(PackMaxParallelDownloads, PackMaxParallelDownloads);

        var tasks = new List<Task>();
        var errors = new List<Exception>();
        var errorsLock = new object();

        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();

            tasks.Add(Task.Run(async () =>
            {
                await sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var destRel = NormalizeRelPath(file.Path);
                    if (string.IsNullOrWhiteSpace(destRel))
                    {
                        // некорректная запись — пропускаем
                        return;
                    }

                    if (!IsAllowedDest(destRel))
                        throw new InvalidOperationException($"Manifest пытается записать запрещённый путь: {destRel}");

                    if (!IsValidSha256(file.Sha256))
                        throw new InvalidOperationException($"Invalid sha256 in manifest for {destRel}");

                    var localPath = ToLocalPath(destRel);

                    if (!IsUnderGameDir(localPath))
                        throw new InvalidOperationException($"Unsafe path in manifest: {file.Path}");

                    // применяем pending, если есть
                    await TryApplyPendingAsync(destRel, localPath, file.Sha256, ct).ConfigureAwait(false);

                    var check = await CheckFileAsync(destRel, localPath, file, state, stateLock, ct).ConfigureAwait(false);

                    if (check == FileCheckResult.Match)
                    {
                        AddBytes(Math.Max(0, file.Size));
                        return;
                    }

                    // ✅ Критический фикс:
                    // config/ НЕ перекачиваем если файл СУЩЕСТВУЕТ и “локально изменён”,
                    // но если файла НЕТ — мы обязаны его скачать.
                    if (IsUserMutableDest(destRel) && File.Exists(localPath))
                    {
                        Log?.Invoke(this, $"Сборка: {destRel} изменён локально — оставляю как есть.");
                        AddBytes(Math.Max(0, file.Size));
                        return;
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
                            // bytes может быть отрицательным при неудачной попытке на зеркале
                            AddBytes(bytes);
                        }).ConfigureAwait(false);

                    lock (stateLock)
                    {
                        UpdatePackStateEntry(state, destRel, localPath, file.Sha256);
                    }

                    Log?.Invoke(this, $"Сборка: OK {destRel}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // штатная отмена
                }
                catch (Exception ex)
                {
                    lock (errorsLock) errors.Add(ex);
                }
                finally
                {
                    sem.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        if (errors.Count > 0)
        {
            // Берём первую как основную, остальные допишем в лог
            var first = errors[0];
            for (int i = 1; i < errors.Count; i++)
                Log?.Invoke(this, $"Сборка: дополнительная ошибка: {errors[i].Message}");

            throw new InvalidOperationException("Синхронизация сборки завершилась с ошибками.", first);
        }

        // prune после завершения всех задач
        if (manifest.Prune is { Length: > 0 })
        {
            var roots = manifest.Prune
                .Select(NormalizeRoot)
                .Where(r =>
                    !string.IsNullOrWhiteSpace(r) &&
                    IsAllowedDest(r) &&
                    !IsUserMutableDest(r))
                .ToArray();

            if (roots.Length > 0)
                PruneExtras(roots, wanted);
        }

        // чистим state от несуществующих в manifest
        lock (stateLock)
        {
            foreach (var k in state.Files.Keys.ToList())
            {
                if (!wanted.Contains(k))
                    state.Files.Remove(k);
            }

            state.PackId = manifest.PackId;
            state.ManifestVersion = manifest.Version;
        }

        SavePackState(state);

        Log?.Invoke(this, $"Сборка: актуальна (версия {manifest.Version}).");
        ProgressPercent?.Invoke(this, 100);

        SaveMirrorStatsQuiet(force: true);
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

            // быстрый путь: pack_state кэш
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
            lock (_packStateIoLock)
            {
                var path = Path.Combine(_path.BasePath, "launcher", PackStateFileName);
                if (!File.Exists(path))
                    return new PackState();

                var json = File.ReadAllText(path);
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

    // ✅ атомарно + lock
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

                File.WriteAllText(tmp, JsonSerializer.Serialize(state, PackJsonContext.Default.PackState));

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

        public Dictionary<string, PackStateEntry> Files { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class PackStateEntry
    {
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
        public long LastWriteUtcTicks { get; set; }
    }

    // =========================
    // Manifest download
    // =========================

    private static string GetFinalBaseUrlFromResponse(HttpResponseMessage resp, string fallbackBaseUrl)
    {
        try
        {
            var uri = resp.RequestMessage?.RequestUri;
            if (uri is null)
                return NormalizeBaseUrl(fallbackBaseUrl);

            var baseUri = new Uri(uri, "./");
            return NormalizeBaseUrl(baseUri.ToString());
        }
        catch
        {
            return NormalizeBaseUrl(fallbackBaseUrl);
        }
    }

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

        // primary = твой сайт если есть, иначе первый
        var primary = list.FirstOrDefault(u => u.Contains("legendborn.ru", StringComparison.OrdinalIgnoreCase))
                      ?? list[0];

        _primaryBaseUrlThisRun = primary;
        _primaryOkThisRun = false;

        // 1) СНАЧАЛА — сайт (короткие таймауты)
        var primaryRes =
            await TryFetchManifestFromBaseAsync(primary, ct, MirrorPrimaryTimeoutSec1, countFail: false).ConfigureAwait(false)
            ?? await TryFetchManifestFromBaseAsync(primary, ct, MirrorPrimaryTimeoutSec2, countFail: false).ConfigureAwait(false);

        if (primaryRes is not null)
        {
            _primaryOkThisRun = true;
            SaveMirrorStatsQuiet(force: true);
            return primaryRes.Value;
        }

        // один штраф за запуск (а не два)
        TouchMirrorFailManifest(primary);

        // 2) сайт не доступен -> выбираем лучший fallback автоматически (race)
        var fallbacks = list
            .Where(u => !u.Equals(primary, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (fallbacks.Length == 0)
            throw new InvalidOperationException("Primary mirror failed and no fallbacks are available.");

        fallbacks = OrderMirrorsByStats(fallbacks, MirrorScoreKind.Manifest);

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var tasks = fallbacks
            .Select(b => TryFetchManifestFromBaseAsync(b, raceCts.Token, MirrorFallbackTimeoutSec))
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

                    SaveMirrorStatsQuiet(force: true);
                    return res.Value;
                }
            }
            catch
            {
                // ignore
            }
        }

        throw new InvalidOperationException("Не удалось скачать manifest ни с одного зеркала (site + fallbacks).");
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
                manifest = manifest with
                {
                    Mirrors = manifest.Mirrors
                        .Select(NormalizeAbsoluteBaseUrl)
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                };
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
            .Select(NormalizeAbsoluteBaseUrl)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Упорядочивание:
        // - Если сайт в этом запуске был ОК -> сайт первым ВСЕГДА.
        // - Иначе: первым activeBaseUrl, дальше по статистике (blob-score).
        var ordered = new List<string>();

        var primary = NormalizeAbsoluteBaseUrl(_primaryBaseUrlThisRun);
        var active = NormalizeAbsoluteBaseUrl(activeBaseUrl);

        if (_primaryOkThisRun && !string.IsNullOrWhiteSpace(primary))
            ordered.Add(primary);

        if (!string.IsNullOrWhiteSpace(active))
            ordered.Add(active);

        var rest = mirrors
            .Where(m =>
                !m.Equals(primary, StringComparison.OrdinalIgnoreCase) &&
                !m.Equals(active, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        rest = OrderMirrorsByStats(rest, MirrorScoreKind.Blob);
        ordered.AddRange(rest);

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

            try
            {
                var url = CombineUrl(baseUrl, blobRel);
                Log?.Invoke(this, $"Сборка: download {destRel} <- {baseUrl} ({FormatBytes(file.Size)})");

                var sw = Stopwatch.StartNew();
                await DownloadToFileAsync(url, localPath, file.Size, file.Sha256, destRel, ct, Counted).ConfigureAwait(false);
                sw.Stop();

                TouchMirrorOkBlob(baseUrl, NormalizeLatencyForSize(sw.Elapsed.TotalMilliseconds, file.Size));
                return;
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

        throw new InvalidOperationException($"Не удалось скачать blob: {blobRel}", last);
    }

    private async Task DownloadToFileAsync(
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
        resp.EnsureSuccessStatusCode();

        var media = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (media.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Зеркало вернуло HTML вместо файла (возможна блокировка/страница ошибки).");

        var len = resp.Content.Headers.ContentLength;
        if (expectedSize > 0 && len.HasValue && len.Value > 0)
        {
            var threshold = Math.Min(16_384L, Math.Max(1L, expectedSize / 10));
            if (len.Value < threshold)
                throw new InvalidOperationException(
                    $"Зеркало вернуло подозрительно маленький ответ (Content-Length={len.Value}, ожидается около {expectedSize}).");
        }

        using var sha = SHA256.Create();
        var buffer = new byte[128 * 1024];

        await Provisioning(resp, reqCts.Token, tmp, buffer, sha, onBytes).ConfigureAwait(false);

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
            return;
        }

        if (IsPendingAllowedDest(destRel))
        {
            var pending = localPath + ".pending";
            var pendingOk = await TryMoveOrReplaceWithRetryAsync(tmp, pending, ct, attempts: 10, delayMs: 200).ConfigureAwait(false);
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

    private static async Task Provisioning(HttpResponseMessage resp, CancellationToken token, string tmp, byte[] buffer, SHA256 sha, Action<long> onBytes)
    {
        await using var input = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(
            tmp,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        int read;
        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
            sha.TransformBlock(buffer, 0, read, null, 0);
            onBytes(read);
        }

        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task TryApplyPendingAsync(string destRel, string localPath, string expectedSha256, CancellationToken ct)
    {
        var pending = localPath + ".pending";
        if (!File.Exists(pending))
            return;

        try
        {
            var sha = await ComputeSha256Async(pending, ct).ConfigureAwait(false);
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

        var ok = await TryMoveOrReplaceWithRetryAsync(pending, localPath, ct, attempts: 10, delayMs: 200).ConfigureAwait(false);
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
        return !destRel.StartsWith("config/", StringComparison.OrdinalIgnoreCase);
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

    private static string NormalizeBaseUrl(string url)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!url.EndsWith("/")) url += "/";
        return url;
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

        var s = uri.ToString();
        if (!s.EndsWith("/")) s += "/";
        return s;
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
            Timeout = Timeout.InfiniteTimeSpan, // таймауты per-request
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        http.DefaultRequestHeaders.UserAgent.ParseAdd(LauncherIdentity.UserAgent);
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

    // =========================
    // Mirror stats (EWMA)
    // =========================

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

        // legacy (v1)
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

                var json = File.ReadAllText(path);
                var root = JsonSerializer.Deserialize<MirrorStatsRoot>(json, MirrorStatsJsonOptions) ?? new MirrorStatsRoot();

                if (root.Mirrors is null)
                    root.Mirrors = new Dictionary<string, MirrorStat>(StringComparer.OrdinalIgnoreCase);

                if (!Equals(root.Mirrors.Comparer, StringComparer.OrdinalIgnoreCase))
                    root.Mirrors = new Dictionary<string, MirrorStat>(root.Mirrors, StringComparer.OrdinalIgnoreCase);

                // миграция v1 -> v2
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
                File.WriteAllText(tmp, json);

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
        // score = ms per MiB
        if (ms <= 0) ms = 1;
        if (bytes <= 0) return ms;

        var mib = bytes / (1024.0 * 1024.0);
        if (mib < 1) mib = 1;
        return ms / mib;
    }

    // ===== Pack DTO =====

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
