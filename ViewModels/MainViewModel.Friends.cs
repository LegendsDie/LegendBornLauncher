// File: ViewModels/MainViewModel.Friends.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LegendBorn.Mvvm;
using FriendDto = LegendBorn.Services.SiteAuthService.FriendDto;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private const int FriendsPreviewMax = 2;

    // Как часто дергаем presence лаунчера на сервер
    private static readonly TimeSpan LauncherHeartbeatInterval = TimeSpan.FromSeconds(25);

    // Сколько считаем "онлайн в лаунчере" по lastSeen (если сервер так отдаёт)
    private static readonly TimeSpan LauncherOnlineMaxAge = TimeSpan.FromSeconds(70);

    // Сколько считаем "онлайн на сайте" по lastActivity
    private static readonly TimeSpan SiteOnlineMaxAge = TimeSpan.FromMinutes(5);

    // Как часто обновляем друзей, чтобы онлайны обновлялись сами
    private static readonly TimeSpan FriendsPollingInterval = TimeSpan.FromSeconds(30);

    private int _friendsRefreshGuard;
    private CancellationTokenSource? _friendsScheduleCts;

    private CancellationTokenSource? _presenceCts;
    private CancellationTokenSource? _friendsPollingCts;

    // Логируем статус heartbeat только 1 раз, чтобы не спамить лог
    private int _heartbeatOkLogged;
    private int _heartbeatFailLogged;

    private static readonly Random _previewRng = new();

    public enum OnlinePlace
    {
        Offline = 0,
        Site = 1,
        Launcher = 2
    }

    public sealed class FriendEntry
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string? AvatarUrl { get; init; }

        // Старые поля оставим, если где-то ещё используются
        public string? Status { get; init; } // online/offline/null
        public string? Source { get; init; } // twitch/minecraft/telegram/site/null

        // ✅ Новый источник онлайна (приоритет Launcher > Site > Offline)
        public OnlinePlace OnlinePlace { get; init; } = OnlinePlace.Offline;

        public bool IsOnline => OnlinePlace != OnlinePlace.Offline;

        public string Initial
        {
            get
            {
                var n = (Name ?? "").Trim();
                return string.IsNullOrWhiteSpace(n) ? "?" : n.Substring(0, 1).ToUpperInvariant();
            }
        }

        // ✅ Единственная строка под ником
        public string PresenceText =>
            OnlinePlace switch
            {
                OnlinePlace.Launcher => "В сети • в лаунчере",
                OnlinePlace.Site => "В сети • на сайте",
                _ => "Не в сети"
            };

        // ✅ Единственная плашка справа
        public string PresencePillText =>
            OnlinePlace switch
            {
                OnlinePlace.Launcher => "В ЛАУНЧЕРЕ",
                OnlinePlace.Site => "НА САЙТЕ",
                _ => "НЕ В СЕТИ"
            };

        // Алиасы на случай старых биндингов
        public string PresenceLine => PresenceText;
        public string StatusLabel => PresencePillText;

        public override string ToString() => Name;
    }

    // Полный список
    public ObservableCollection<FriendEntry> Friends { get; } = new();

    // ✅ Витрина: максимум 2 (онлайн приоритет + рандом добивка)
    public ObservableCollection<FriendEntry> FriendsPreview { get; } = new();

    private FriendEntry? _selectedFriend;
    public FriendEntry? SelectedFriend
    {
        get => _selectedFriend;
        set
        {
            if (Set(ref _selectedFriend, value))
                RefreshCanStates();
        }
    }

    public int FriendsCount => Friends.Count;
    public int OnlineFriendsCount => Friends.Count(x => x.IsOnline);
    public int FriendsPreviewCount => FriendsPreview.Count;

    // Алиас под разные XAML варианты
    public string FriendsSummaryText => FriendsPreviewSummaryText;

    public string FriendsPreviewSummaryText
        => $"Онлайн: {OnlineFriendsCount} • Показано: {FriendsPreviewCount}/{FriendsPreviewMax}";

    /// <summary>
    /// Только факт наличия токена.
    /// </summary>
    public bool HasSiteToken => !_isClosing && TryGetAccessToken(out _);

    public bool CanRefreshFriends => !_isClosing && !IsBusy && HasSiteToken;

    public AsyncRelayCommand RefreshFriendsCommand { get; private set; } = null!;

    // Оставляем имя InitSocialCommands, чтобы не ломать места вызова
    private void InitSocialCommands()
    {
        RefreshFriendsCommand = new AsyncRelayCommand(
            RefreshFriendsAsync,
            () => CanRefreshFriends);
    }

    // ===== legacy wrappers (чтобы MainViewModel.cs не ругался на старые имена) =====
    private void ScheduleSocialRefresh() => ScheduleFriendsRefresh();
    private void ClearSocialUi() => ClearFriendsUi();

    // ===== ONLINE presence control (вызывается из MainViewModel.cs) =====
    private void StartOnlinePresence()
    {
        if (_isClosing) return;
        if (!HasSiteToken) return;

        StartLauncherPresenceLoop();
        StartFriendsPollingLoop();
    }

    private void StopOnlinePresence()
    {
        // При остановке можно мягко отправить offline (не обязаловка, TTL тоже решает)
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_isClosing) return;
                    if (!TryGetAccessToken(out var token)) return;

                    // Если метод есть в SiteAuthService — отлично.
                    // Если нет — просто молча игнорируем.
                    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var m = _site.GetType().GetMethod("SendLauncherOfflineAsync", flags);
                    if (m != null)
                    {
                        var res = m.Invoke(_site, new object?[] { token, CancellationToken.None });
                        if (res is Task task) await task.ConfigureAwait(false);
                    }
                }
                catch { /* ignore */ }
            });
        }
        catch { }

        try { _presenceCts?.Cancel(); } catch { }
        try { _presenceCts?.Dispose(); } catch { }
        _presenceCts = null;

        try { _friendsPollingCts?.Cancel(); } catch { }
        try { _friendsPollingCts?.Dispose(); } catch { }
        _friendsPollingCts = null;
    }

    private void StartLauncherPresenceLoop()
    {
        if (_isClosing) return;
        if (!TryGetAccessToken(out _)) return;

        try
        {
            var prev = Interlocked.Exchange(ref _presenceCts, null);
            try { prev?.Cancel(); } catch { }
            try { prev?.Dispose(); } catch { }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            Interlocked.Exchange(ref _presenceCts, cts);

            _ = Task.Run(async () =>
            {
                // небольшая задержка, чтобы логин/профиль успели устаканиться
                try { await Task.Delay(800, cts.Token).ConfigureAwait(false); } catch { }

                while (!cts.IsCancellationRequested && !_isClosing && IsLoggedIn)
                {
                    try
                    {
                        if (TryGetAccessToken(out var tkn))
                            await TrySendLauncherHeartbeatAsync(tkn, cts.Token).ConfigureAwait(false);
                    }
                    catch { /* ignore */ }

                    try
                    {
                        await Task.Delay(LauncherHeartbeatInterval, cts.Token).ConfigureAwait(false);
                    }
                    catch { }
                }
            }, cts.Token);
        }
        catch { }
    }

    private void StartFriendsPollingLoop()
    {
        if (_isClosing) return;

        try
        {
            var prev = Interlocked.Exchange(ref _friendsPollingCts, null);
            try { prev?.Cancel(); } catch { }
            try { prev?.Dispose(); } catch { }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            Interlocked.Exchange(ref _friendsPollingCts, cts);

            _ = Task.Run(async () =>
            {
                // сразу один рефреш
                try { await RefreshFriendsAsync().ConfigureAwait(false); } catch { }

                while (!cts.IsCancellationRequested && !_isClosing && IsLoggedIn)
                {
                    try
                    {
                        await Task.Delay(FriendsPollingInterval, cts.Token).ConfigureAwait(false);
                    }
                    catch { }

                    if (cts.IsCancellationRequested || _isClosing || !IsLoggedIn) break;

                    try { await RefreshFriendsAsync().ConfigureAwait(false); } catch { }
                }
            }, cts.Token);
        }
        catch { }
    }

    private async Task TrySendLauncherHeartbeatAsync(string token, CancellationToken ct)
    {
        // ✅ У тебя в SiteAuthService ЕСТЬ SendLauncherHeartbeatAsync — вызываем напрямую.
        // Без reflection: меньше шансов “не нашли метод => ничего не отправили”.
        try
        {
            // _site у тебя уже используется как SiteAuthService (GetFriendsAsync и т.д.)
            var resp = await _site.SendLauncherHeartbeatAsync(token, ct).ConfigureAwait(false);

            if (resp is not null && resp.Ok)
            {
                if (Interlocked.CompareExchange(ref _heartbeatOkLogged, 1, 0) == 0)
                    AppendLog("Presence: heartbeat лаунчера отправлен (ok).");
            }
            else
            {
                // логируем 1 раз, чтобы не спамить
                if (Interlocked.CompareExchange(ref _heartbeatFailLogged, 1, 0) == 0)
                {
                    var err = resp?.Error ?? resp?.Message ?? "unknown";
                    AppendLog("Presence: heartbeat лаунчера не ok: " + err);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { /* сеть/TTL переживём */ }
        catch { /* ignore */ }
    }

    private void ClearFriendsUi()
    {
        PostToUi(() =>
        {
            Friends.Clear();
            FriendsPreview.Clear();
            SelectedFriend = null;

            Raise(nameof(FriendsCount));
            Raise(nameof(OnlineFriendsCount));
            Raise(nameof(FriendsPreviewCount));
            Raise(nameof(FriendsPreviewSummaryText));
            Raise(nameof(FriendsSummaryText));
            Raise(nameof(HasSiteToken));
            Raise(nameof(CanRefreshFriends));
        }, DispatcherPriority.DataBind);
    }

    // Можно дергать с View (Loaded) — мягко дебаунсит
    private void ScheduleFriendsRefresh()
    {
        if (_isClosing) return;
        if (!HasSiteToken) return;

        try
        {
            var prev = Interlocked.Exchange(ref _friendsScheduleCts, null);
            try { prev?.Cancel(); } catch { }
            try { prev?.Dispose(); } catch { }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            Interlocked.Exchange(ref _friendsScheduleCts, cts);

            _ = ScheduleFriendsRefreshAsync(cts.Token);
        }
        catch { }
    }

    private async Task ScheduleFriendsRefreshAsync(CancellationToken ct)
    {
        try { await Task.Delay(250, ct).ConfigureAwait(false); }
        catch { return; }

        if (_isClosing || ct.IsCancellationRequested) return;

        try { await RefreshFriendsAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppendLog("Друзья: ошибка авто-обновления: " + ex.Message); }
    }

    private static string? NormalizePublicUrl(string? url)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;

        if (url.StartsWith("//", StringComparison.Ordinal))
            return "https:" + url;

        var primary = string.IsNullOrWhiteSpace(SitePublicUrlPrimary) ? SitePublicUrlFallback : SitePublicUrlPrimary;

        if (url.StartsWith("/", StringComparison.Ordinal))
            return primary + url;

        return primary + "/" + url;
    }

    private static string? NormalizeStatus(string? status)
    {
        status = (status ?? "").Trim();
        if (string.IsNullOrWhiteSpace(status)) return null;

        if (status.Equals("online", StringComparison.OrdinalIgnoreCase)) return "online";
        if (status.Equals("offline", StringComparison.OrdinalIgnoreCase)) return "offline";
        return status;
    }

    private static string? TryGetDtoString(object dto, string propName)
    {
        try
        {
            var p = dto.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
            var v = p?.GetValue(dto);
            var s = v?.ToString();
            s = (s ?? "").Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch
        {
            return null;
        }
    }

    private static bool? TryGetDtoBool(object dto, string propName)
    {
        try
        {
            var p = dto.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
            if (p == null) return null;

            var v = p.GetValue(dto);
            if (v == null) return null;

            if (v is bool b) return b;

            var s = v.ToString();
            s = (s ?? "").Trim();

            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(s, "1", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "0", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "no", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(s, "online", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "offline", StringComparison.OrdinalIgnoreCase)) return false;

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryGetDtoDateTimeOffset(object dto, string propName)
    {
        try
        {
            var p = dto.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
            if (p == null) return null;

            var v = p.GetValue(dto);
            if (v == null) return null;

            if (v is DateTimeOffset dtoff) return dtoff;

            if (v is DateTime dt)
            {
                if (dt.Kind == DateTimeKind.Unspecified)
                    return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                return new DateTimeOffset(dt.ToUniversalTime());
            }

            var s = v.ToString();
            s = (s ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;

            if (DateTimeOffset.TryParse(s, out var parsed))
                return parsed.ToUniversalTime();

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRecent(DateTimeOffset tsUtc, TimeSpan maxAge)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (tsUtc > now) return true; // чуть-чуть "в будущем" — считаем живым
            return (now - tsUtc) <= maxAge;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLauncherString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var p = s.Trim().ToLowerInvariant();
        return p.Contains("launcher") || p.Contains("client") || p.Contains("app");
    }

    private static bool IsSiteString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var p = s.Trim().ToLowerInvariant();
        return p.Contains("site") || p.Contains("web") || p.Contains("portal");
    }

    private static OnlinePlace ResolveOnlinePlace(FriendDto dto, string? normalizedStatus)
    {
        // ✅ 0) Лучший вариант — server computed onlinePlace
        var place = (dto.OnlinePlace ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(place))
        {
            if (IsLauncherString(place)) return OnlinePlace.Launcher;
            if (IsSiteString(place)) return OnlinePlace.Site;
            if (place.Equals("offline", StringComparison.OrdinalIgnoreCase)) return OnlinePlace.Offline;
        }

        // ✅ 1) Явные флаги
        bool launcherOnline = dto.LauncherOnline == true;
        bool siteOnline = dto.SiteOnline == true;

        // ✅ 1.1) На всякий — поддержка потенциальных будущих/легаси имён через reflection
        launcherOnline |=
            (TryGetDtoBool(dto, "IsLauncherOnline") ?? false) ||
            (TryGetDtoBool(dto, "OnlineLauncher") ?? false) ||
            (TryGetDtoBool(dto, "InLauncher") ?? false);

        siteOnline |=
            (TryGetDtoBool(dto, "IsOnline") ?? false) ||
            (TryGetDtoBool(dto, "Online") ?? false) ||
            (TryGetDtoBool(dto, "IsSiteOnline") ?? false);

        // ✅ 2) lastSeen timestamps (сервер может отдавать UTC)
        var launcherLast = dto.LauncherLastSeenUtc
                           ?? TryGetDtoDateTimeOffset(dto, "LauncherLastSeenUtc")
                           ?? TryGetDtoDateTimeOffset(dto, "LauncherLastSeen")
                           ?? TryGetDtoDateTimeOffset(dto, "LastSeenLauncherUtc");

        if (launcherLast.HasValue && IsRecent(launcherLast.Value.ToUniversalTime(), LauncherOnlineMaxAge))
            launcherOnline = true;

        var siteLast = dto.SiteLastSeenUtc
                       ?? dto.LastActivityUtc
                       ?? dto.LastSeenUtc
                       ?? TryGetDtoDateTimeOffset(dto, "SiteLastSeenUtc")
                       ?? TryGetDtoDateTimeOffset(dto, "LastActivityUtc")
                       ?? TryGetDtoDateTimeOffset(dto, "LastSeenUtc")
                       ?? TryGetDtoDateTimeOffset(dto, "LastSeen");

        if (siteLast.HasValue && IsRecent(siteLast.Value.ToUniversalTime(), SiteOnlineMaxAge))
            siteOnline = true;

        // ✅ 3) Старый Status=online (если сервер не дал place) — считаем "site"
        if (string.Equals(normalizedStatus, "online", StringComparison.OrdinalIgnoreCase))
            siteOnline = true;

        // ✅ Приоритет: Launcher > Site > Offline
        if (launcherOnline) return OnlinePlace.Launcher;
        if (siteOnline) return OnlinePlace.Site;
        return OnlinePlace.Offline;
    }

    private static FriendEntry ToFriendEntry(FriendDto dto)
    {
        // максимально устойчиво: Id / UserId / PublicId
        var id = (dto.Id ?? dto.UserId ?? (dto.PublicId?.ToString() ?? "")).Trim();

        var name = (dto.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "Без имени";

        var status = NormalizeStatus(dto.Status);
        var onlinePlace = ResolveOnlinePlace(dto, status);

        // source оставим, но он про "платформу друга", а не про presence
        var source = (dto.Source ?? TryGetDtoString(dto, "Platform") ?? TryGetDtoString(dto, "Provider"))?.Trim();
        if (string.IsNullOrWhiteSpace(source)) source = null;

        return new FriendEntry
        {
            Id = id,
            Name = name,
            AvatarUrl = NormalizePublicUrl(dto.Image),

            Status = status,
            Source = source,

            OnlinePlace = onlinePlace
        };
    }

    private static List<FriendEntry> BuildPreview(List<FriendEntry> all)
    {
        var result = new List<FriendEntry>(FriendsPreviewMax);

        // 1) онлайн приоритет (Launcher > Site)
        var online = all.Where(x => x.IsOnline)
                        .OrderByDescending(x => (int)x.OnlinePlace)
                        .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                        .Take(FriendsPreviewMax)
                        .ToList();

        result.AddRange(online);

        // 2) добивка рандомом из остальных (по Id, чтобы не словить дубль)
        if (result.Count < FriendsPreviewMax)
        {
            var used = new HashSet<string>(result.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            var rest = all.Where(x => !used.Contains(x.Id)).ToList();

            for (int i = rest.Count - 1; i > 0; i--)
            {
                int j = _previewRng.Next(i + 1);
                (rest[i], rest[j]) = (rest[j], rest[i]);
            }

            foreach (var f in rest)
            {
                result.Add(f);
                if (result.Count == FriendsPreviewMax) break;
            }
        }

        return result;
    }

    private static List<FriendEntry> DeduplicateById(List<FriendEntry> list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FriendEntry>(list.Count);

        foreach (var item in list)
        {
            var id = (item.Id ?? "").Trim();
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (seen.Add(id)) result.Add(item);
        }

        return result;
    }

    private async Task RefreshFriendsAsync()
    {
        if (_isClosing) return;
        if (!TryGetAccessToken(out var token)) return;
        if (!CanRefreshFriends) return;

        if (Interlocked.Exchange(ref _friendsRefreshGuard, 1) == 1)
            return;

        try
        {
            var resp = await _site.GetFriendsAsync(token, _lifetimeCts.Token).ConfigureAwait(false);

            var ok = resp is not null && resp.Ok && resp.Friends is not null;
            if (!ok)
            {
                AppendLog("Друзья: не удалось обновить список (ответ не OK).");
                return;
            }

            var all = resp!.Friends!
                .Select(ToFriendEntry)
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .OrderByDescending(x => (int)x.OnlinePlace) // ✅ Launcher > Site > Offline
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            all = DeduplicateById(all);

            var preview = BuildPreview(all);

            PostToUi(() =>
            {
                Friends.Clear();
                foreach (var f in all)
                    Friends.Add(f);

                FriendsPreview.Clear();
                foreach (var f in preview)
                    FriendsPreview.Add(f);

                Raise(nameof(FriendsCount));
                Raise(nameof(OnlineFriendsCount));
                Raise(nameof(FriendsPreviewCount));
                Raise(nameof(FriendsPreviewSummaryText));
                Raise(nameof(FriendsSummaryText));
            }, DispatcherPriority.DataBind);
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException ex)
        {
            AppendLog("Друзья: ошибка сети/API: " + ex.Message);
        }
        catch (Exception ex)
        {
            AppendLog("Друзья: ошибка: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _friendsRefreshGuard, 0);

            PostToUi(() =>
            {
                Raise(nameof(HasSiteToken));
                Raise(nameof(CanRefreshFriends));
                Raise(nameof(FriendsPreviewSummaryText));
                Raise(nameof(FriendsSummaryText));
            }, DispatcherPriority.DataBind);

            RefreshCanStates();
        }
    }

    // =========================
    // Token resolving (макс. устойчиво)
    // =========================

    private bool TryGetAccessToken(out string token)
    {
        token = "";

        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var t = GetType();

            var tokensObj =
                t.GetField("_tokens", flags)?.GetValue(this) ??
                t.GetProperty("Tokens", flags)?.GetValue(this);

            if (TryReadTokenFromObject(tokensObj, out token))
                return true;
        }
        catch { }

        try
        {
            if (TryReadTokenFromObject(_tokenStore, out token))
                return true;
        }
        catch { }

        token = "";
        return false;
    }

    private static bool TryReadTokenFromObject(object? obj, out string token)
    {
        token = "";
        if (obj is null) return false;

        if (obj is string s)
        {
            s = s.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(s)) { token = s; return true; }
            return false;
        }

        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = obj.GetType();

        var pHas = type.GetProperty("HasAccessToken", flags);
        var pSafe = type.GetProperty("SafeAccessToken", flags);
        if (pHas is not null && pSafe is not null)
        {
            try
            {
                var hasObj = pHas.GetValue(obj);
                var has = hasObj is bool hb && hb;

                var safe = pSafe.GetValue(obj) as string;
                safe = (safe ?? "").Trim().Trim('"');

                if (has && !string.IsNullOrWhiteSpace(safe))
                {
                    token = safe;
                    return true;
                }
            }
            catch { }
        }

        var pAccess = type.GetProperty("AccessToken", flags);
        if (pAccess is not null)
        {
            try
            {
                var at = pAccess.GetValue(obj) as string;
                at = (at ?? "").Trim().Trim('"');

                if (!string.IsNullOrWhiteSpace(at))
                {
                    token = at;
                    return true;
                }
            }
            catch { }
        }

        foreach (var name in new[] { "Current", "Value", "Token", "Tokens" })
        {
            var p = type.GetProperty(name, flags);
            if (p is null) continue;

            try
            {
                var inner = p.GetValue(obj);
                if (TryReadTokenFromObject(inner, out token))
                    return true;
            }
            catch { }
        }

        return false;
    }
}
