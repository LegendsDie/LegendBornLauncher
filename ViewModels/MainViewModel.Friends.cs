// File: ViewModels/MainViewModel.Friends.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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

    private static readonly TimeSpan LauncherHeartbeatInterval = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan MinecraftOnlineMaxAge = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan LauncherOnlineMaxAge = TimeSpan.FromSeconds(70);
    private static readonly TimeSpan SiteOnlineMaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FriendsPollingInterval = TimeSpan.FromSeconds(30);

    private int _friendsRefreshGuard;
    private CancellationTokenSource? _friendsScheduleCts;

    private CancellationTokenSource? _presenceCts;
    private CancellationTokenSource? _friendsPollingCts;

    private int _heartbeatOkLogged;
    private int _heartbeatFailLogged;

    private static readonly Random _previewRng = new();
    private static readonly object _previewRngLock = new();

    public enum OnlinePlace
    {
        Offline = 0,
        Site = 1,
        Launcher = 2,
        Minecraft = 3
    }

    public sealed class FriendEntry
    {
        public string Id { get; init; } = "";
        public string ProfileId { get; init; } = "";

        public int? PublicId { get; init; }
        public string? UserId { get; init; }
        public string? InternalId { get; init; }

        public string Name { get; init; } = "";
        public string? AvatarUrl { get; init; }

        public string? Status { get; init; }
        public string? Source { get; init; }
        public string? MinecraftServerId { get; init; }

        // Priority: Minecraft > Launcher > Site > Offline.
        public OnlinePlace OnlinePlace { get; init; } = OnlinePlace.Offline;
        public bool IsOnline => OnlinePlace != OnlinePlace.Offline;

        public DateTimeOffset? LastSeenUtc { get; init; }
        public OnlinePlace LastSeenPlace { get; init; } = OnlinePlace.Offline;

        public string Initial
        {
            get
            {
                var n = (Name ?? "").Trim();
                return string.IsNullOrWhiteSpace(n) ? "?" : n.Substring(0, 1).ToUpperInvariant();
            }
        }

        public string PresenceText
        {
            get
            {
                if (OnlinePlace == OnlinePlace.Minecraft)
                {
                    var serverId = (MinecraftServerId ?? "").Trim();
                    return string.IsNullOrWhiteSpace(serverId)
                        ? "в сети • в Minecraft"
                        : $"в сети • Minecraft • {serverId}";
                }

                if (OnlinePlace == OnlinePlace.Launcher) return "в сети • в лаунчере";
                if (OnlinePlace == OnlinePlace.Site) return "в сети • на сайте";

                if (!LastSeenUtc.HasValue)
                    return "";

                var when = FormatWhenLocal(LastSeenUtc.Value);

                if (LastSeenPlace == OnlinePlace.Minecraft) return $"был в Minecraft {when}";
                if (LastSeenPlace == OnlinePlace.Launcher) return $"был в лаунчере {when}";
                if (LastSeenPlace == OnlinePlace.Site) return $"был на сайте {when}";
                return $"был {when}";
            }
        }

        public string PresencePillText => IsOnline ? "ОНЛАЙН" : "ОФФЛАЙН";

        public string PresenceLine => PresenceText;
        public string StatusLabel => PresencePillText;

        public override string ToString() => Name;

        private static string FormatWhenLocal(DateTimeOffset utc)
        {
            try
            {
                var local = utc.ToLocalTime();
                return local.ToString("dd.MM HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
            }
            catch { return ""; }
        }
    }

    public ObservableCollection<FriendEntry> Friends { get; } = new();
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

    public string FriendsSummaryText => FriendsPreviewSummaryText;

    public string FriendsPreviewSummaryText
        => $"Онлайн: {OnlineFriendsCount} • Показано: {FriendsPreviewCount}/{FriendsPreviewMax}";

    public bool HasSiteToken => !_isClosing && TryGetAccessToken(out _);

    public bool CanRefreshFriends => !_isClosing && !IsBusy && HasSiteToken;

    public AsyncRelayCommand RefreshFriendsCommand { get; private set; } = null!;

    private void InitSocialCommands()
    {
        RefreshFriendsCommand = new AsyncRelayCommand(
            RefreshFriendsAsync,
            () => CanRefreshFriends);
    }

    private void ScheduleSocialRefresh() => ScheduleFriendsRefresh();
    private void ClearSocialUi() => ClearFriendsUi();

    private void StartOnlinePresence()
    {
        if (_isClosing) return;
        if (!HasSiteToken) return;

        StartLauncherPresenceLoop();
        StartFriendsPollingLoop();
    }

    private void StopOnlinePresence()
    {
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_isClosing) return;
                    if (!TryGetAccessToken(out var token)) return;

                    await _site.SendLauncherOfflineAsync(token, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
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
                try { await Task.Delay(800, cts.Token).ConfigureAwait(false); } catch { }

                while (!cts.IsCancellationRequested && !_isClosing && IsLoggedIn)
                {
                    try
                    {
                        if (TryGetAccessToken(out var tkn))
                            await TrySendLauncherHeartbeatAsync(tkn, cts.Token).ConfigureAwait(false);
                    }
                    catch { }

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
                try { await RefreshFriendsAsync().ConfigureAwait(false); } catch { }

                while (!cts.IsCancellationRequested && !_isClosing && IsLoggedIn)
                {
                    try { await Task.Delay(FriendsPollingInterval, cts.Token).ConfigureAwait(false); }
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
        try
        {
            var resp = await _site.SendLauncherHeartbeatAsync(token, ct).ConfigureAwait(false);

            if (resp is not null && resp.Ok)
            {
                if (Interlocked.CompareExchange(ref _heartbeatOkLogged, 1, 0) == 0)
                    AppendLog("Presence: heartbeat лаунчера отправлен (ok).");
            }
            else
            {
                if (Interlocked.CompareExchange(ref _heartbeatFailLogged, 1, 0) == 0)
                {
                    var err = resp?.Error ?? resp?.Message ?? "unknown";
                    AppendLog("Presence: heartbeat лаунчера не ok: " + err);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }
        catch { }
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
        catch { return null; }
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

            var s = (v.ToString() ?? "").Trim();

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
        catch { return null; }
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

            var s = (v.ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;

            if (DateTimeOffset.TryParse(s, out var parsed))
                return parsed.ToUniversalTime();

            return null;
        }
        catch { return null; }
    }

    private static bool IsRecent(DateTimeOffset tsUtc, TimeSpan maxAge)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (tsUtc > now) return true;
            return (now - tsUtc) <= maxAge;
        }
        catch { return false; }
    }

    private static bool IsMinecraftString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var p = s.Trim().ToLowerInvariant();
        return p == "mc" || p.Contains("minecraft");
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
        var place = (dto.OnlinePlace ?? "").Trim();
        var source = (dto.Source ?? "").Trim();

        if (!string.IsNullOrWhiteSpace(place))
        {
            if (IsMinecraftString(place)) return OnlinePlace.Minecraft;
            if (IsLauncherString(place)) return OnlinePlace.Launcher;
            if (IsSiteString(place)) return OnlinePlace.Site;
            if (place.Equals("offline", StringComparison.OrdinalIgnoreCase)) return OnlinePlace.Offline;
        }

        if (IsMinecraftString(source) && string.Equals(normalizedStatus, "online", StringComparison.OrdinalIgnoreCase))
            return OnlinePlace.Minecraft;

        bool launcherOnline = dto.LauncherOnline == true;
        bool siteOnline = dto.SiteOnline == true;

        launcherOnline |=
            (TryGetDtoBool(dto, "IsLauncherOnline") ?? false) ||
            (TryGetDtoBool(dto, "OnlineLauncher") ?? false) ||
            (TryGetDtoBool(dto, "InLauncher") ?? false);

        siteOnline |=
            (TryGetDtoBool(dto, "IsOnline") ?? false) ||
            (TryGetDtoBool(dto, "Online") ?? false) ||
            (TryGetDtoBool(dto, "IsSiteOnline") ?? false);

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

        if (IsMinecraftString(source) && dto.LastSeenUtc.HasValue &&
            IsRecent(dto.LastSeenUtc.Value.ToUniversalTime(), MinecraftOnlineMaxAge))
            return OnlinePlace.Minecraft;

        if (string.Equals(normalizedStatus, "online", StringComparison.OrdinalIgnoreCase))
            siteOnline = true;

        if (launcherOnline) return OnlinePlace.Launcher;
        if (siteOnline) return OnlinePlace.Site;
        return OnlinePlace.Offline;
    }

    private static (DateTimeOffset? tsUtc, OnlinePlace place) ResolveLastSeen(FriendDto dto)
    {
        var launcherLast = dto.LauncherLastSeenUtc
                           ?? TryGetDtoDateTimeOffset(dto, "LauncherLastSeenUtc")
                           ?? TryGetDtoDateTimeOffset(dto, "LauncherLastSeen")
                           ?? TryGetDtoDateTimeOffset(dto, "LastSeenLauncherUtc");

        var siteLast = dto.SiteLastSeenUtc
                       ?? dto.LastActivityUtc
                       ?? TryGetDtoDateTimeOffset(dto, "SiteLastSeenUtc")
                       ?? TryGetDtoDateTimeOffset(dto, "LastActivityUtc");

        var anyLast = dto.LastSeenUtc
                      ?? TryGetDtoDateTimeOffset(dto, "LastSeenUtc")
                      ?? TryGetDtoDateTimeOffset(dto, "LastSeen");

        DateTimeOffset? best = null;
        OnlinePlace bestPlace = OnlinePlace.Offline;

        void Consider(DateTimeOffset? ts, OnlinePlace p)
        {
            if (!ts.HasValue) return;
            var u = ts.Value.ToUniversalTime();
            if (!best.HasValue || u > best.Value)
            {
                best = u;
                bestPlace = p;
            }
        }

        var currentPlace = (dto.OnlinePlace ?? "").Trim();
        var source = (dto.Source ?? "").Trim();
        var anyLastPlace = IsMinecraftString(currentPlace) || IsMinecraftString(source)
            ? OnlinePlace.Minecraft
            : OnlinePlace.Offline;

        Consider(anyLast, anyLastPlace);
        Consider(siteLast, OnlinePlace.Site);
        Consider(launcherLast, OnlinePlace.Launcher);

        return (best, bestPlace);
    }

    private static int? TryGetPublicId(FriendDto dto)
    {
        try
        {
            if (dto.PublicId is int pid && pid > 0)
                return pid;
        }
        catch { }

        foreach (var name in new[] { "PublicId", "publicId", "ProfileId", "profileId", "ProfilePublicId", "PublicID", "publicID" })
        {
            try
            {
                var p = dto.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                var raw = p?.GetValue(dto);
                if (raw is null) continue;

                if (raw is int i && i > 0) return i;
                if (raw is long l && l > 0 && l <= int.MaxValue) return (int)l;

                var s = raw.ToString();
                if (!string.IsNullOrWhiteSpace(s) &&
                    int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                    parsed > 0)
                    return parsed;
            }
            catch { }
        }

        return null;
    }

    private static string ResolveProfileId(FriendDto dto, int? publicId, string? dtoId, string? userId)
    {
        if (publicId.HasValue && publicId.Value > 0)
            return publicId.Value.ToString(CultureInfo.InvariantCulture);

        var profileId =
            TryGetDtoString(dto, "ProfileId") ??
            TryGetDtoString(dto, "ProfileUserId") ??
            TryGetDtoString(dto, "ProfileSlug");

        profileId = (profileId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(profileId))
            return profileId;

        var id = (dtoId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(id) &&
            int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) &&
            n > 0)
            return n.ToString(CultureInfo.InvariantCulture);

        userId = (userId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(userId))
            return userId;

        if (!string.IsNullOrWhiteSpace(id))
            return id;

        return "";
    }

    private static string GetDedupKey(FriendEntry x)
    {
        var uid = (x.UserId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(uid)) return "u:" + uid;

        if (x.PublicId.HasValue && x.PublicId.Value > 0) return "p:" + x.PublicId.Value.ToString(CultureInfo.InvariantCulture);

        var iid = (x.InternalId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(iid)) return "i:" + iid;

        var id = (x.Id ?? "").Trim();
        return string.IsNullOrWhiteSpace(id) ? "" : "id:" + id;
    }

    private static FriendEntry ToFriendEntry(FriendDto dto)
    {
        var dtoId = (dto.Id ?? "").Trim();
        var userId = (dto.UserId ?? "").Trim();
        var publicId = TryGetPublicId(dto);

        var stableId =
            !string.IsNullOrWhiteSpace(userId) ? userId :
            !string.IsNullOrWhiteSpace(dtoId) ? dtoId :
            publicId?.ToString(CultureInfo.InvariantCulture) ?? "";

        var name = (dto.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "Без имени";

        var status = NormalizeStatus(dto.Status);
        var onlinePlace = ResolveOnlinePlace(dto, status);
        var (lastSeenUtc, lastSeenPlace) = ResolveLastSeen(dto);

        var source = (dto.Source ?? TryGetDtoString(dto, "Platform") ?? TryGetDtoString(dto, "Provider"))?.Trim();
        if (string.IsNullOrWhiteSpace(source)) source = null;

        var minecraftServerId = onlinePlace == OnlinePlace.Minecraft || IsMinecraftString(source)
            ? (dto.Note ?? "").Trim()
            : "";
        if (string.IsNullOrWhiteSpace(minecraftServerId)) minecraftServerId = null;

        var profileId = ResolveProfileId(dto, publicId, dtoId, userId);

        return new FriendEntry
        {
            Id = stableId,
            ProfileId = profileId,

            PublicId = publicId,
            UserId = string.IsNullOrWhiteSpace(userId) ? null : userId,
            InternalId = string.IsNullOrWhiteSpace(dtoId) ? null : dtoId,

            Name = name,
            AvatarUrl = NormalizePublicUrl(dto.Image),

            Status = status,
            Source = source,
            MinecraftServerId = minecraftServerId,

            OnlinePlace = onlinePlace,
            LastSeenUtc = lastSeenUtc,
            LastSeenPlace = lastSeenPlace
        };
    }

    private static List<FriendEntry> Deduplicate(List<FriendEntry> list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FriendEntry>(list.Count);

        foreach (var item in list)
        {
            var key = GetDedupKey(item);
            if (string.IsNullOrWhiteSpace(key)) continue;

            if (seen.Add(key))
                result.Add(item);
        }

        return result;
    }

    private static List<FriendEntry> BuildPreview(List<FriendEntry> all)
    {
        var result = new List<FriendEntry>(FriendsPreviewMax);

        var online = all.Where(x => x.IsOnline)
                        .OrderByDescending(x => (int)x.OnlinePlace)
                        .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                        .Take(FriendsPreviewMax)
                        .ToList();

        result.AddRange(online);

        if (result.Count < FriendsPreviewMax)
        {
            var used = new HashSet<string>(result.Select(GetDedupKey), StringComparer.OrdinalIgnoreCase);
            var rest = all.Where(x => !used.Contains(GetDedupKey(x))).ToList();

            lock (_previewRngLock)
            {
                for (int i = rest.Count - 1; i > 0; i--)
                {
                    int j = _previewRng.Next(i + 1);
                    (rest[i], rest[j]) = (rest[j], rest[i]);
                }
            }

            foreach (var f in rest)
            {
                result.Add(f);
                if (result.Count == FriendsPreviewMax) break;
            }
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
                .ToList();

            all = Deduplicate(all);

            all = all
                .OrderByDescending(x => (int)x.OnlinePlace)
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var preview = BuildPreview(all);

            PostToUi(() =>
            {
                Friends.Clear();
                foreach (var f in all) Friends.Add(f);

                FriendsPreview.Clear();
                foreach (var f in preview) FriendsPreview.Add(f);

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
