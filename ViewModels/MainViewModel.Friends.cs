// File: ViewModels/MainViewModel.Friends.cs
using System;
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

    private int _friendsRefreshGuard;
    private CancellationTokenSource? _friendsScheduleCts;

    private static readonly Random _previewRng = new();

    public sealed class FriendEntry
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string? AvatarUrl { get; init; }

        public string? Status { get; init; } // online/offline/null
        public string? Source { get; init; } // twitch/minecraft/telegram/site/null

        public bool IsOnline => string.Equals(Status, "online", StringComparison.OrdinalIgnoreCase);

        public string Initial
        {
            get
            {
                var n = (Name ?? "").Trim();
                return string.IsNullOrWhiteSpace(n) ? "?" : n.Substring(0, 1).ToUpperInvariant();
            }
        }

        public string SourceLabel => (Source ?? "").Trim().ToLowerInvariant() switch
        {
            "twitch" => "Twitch",
            "minecraft" => "Minecraft",
            "telegram" => "Telegram",
            "site" => "Портал",
            "" => "",
            _ => "Портал"
        };

        // ✅ Единственная строка статуса для UI
        public string PresenceLine
        {
            get
            {
                if (!IsOnline) return "Не в сети";
                var where = SourceLabel;
                return string.IsNullOrWhiteSpace(where) ? "В сети" : $"В сети • {where}";
            }
        }

        public override string ToString() => Name;
    }

    // Полный список (может быть нужен в будущем)
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
            var v = p?.GetValue(dto) as string;
            v = (v ?? "").Trim();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch
        {
            return null;
        }
    }

    private static FriendEntry ToFriendEntry(FriendDto dto)
    {
        // максимально устойчиво: Id / UserId / PublicId
        var id = (dto.Id ?? dto.UserId ?? (dto.PublicId?.ToString() ?? "")).Trim();

        var name = (dto.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "Без имени";

        // source мягко (на случай отсутствия поля в DTO)
        var source = TryGetDtoString(dto, "Source") ?? TryGetDtoString(dto, "Platform") ?? TryGetDtoString(dto, "Provider");
        source = (source ?? "").Trim();
        if (string.IsNullOrWhiteSpace(source)) source = null;

        return new FriendEntry
        {
            Id = id,
            Name = name,
            AvatarUrl = NormalizePublicUrl(dto.Image),
            Status = NormalizeStatus(dto.Status),
            Source = source
        };
    }

    private static List<FriendEntry> BuildPreview(List<FriendEntry> all)
    {
        var result = new List<FriendEntry>(FriendsPreviewMax);

        // 1) приоритет онлайн
        var online = all.Where(x => x.IsOnline)
                        .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                        .Take(FriendsPreviewMax)
                        .ToList();

        result.AddRange(online);

        // 2) добивка рандомом из остальных (если онлайн < 2)
        if (result.Count < FriendsPreviewMax)
        {
            var rest = all.Where(x => !result.Contains(x)).ToList();

            // простой shuffle
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
                .OrderByDescending(x => x.IsOnline)
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

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
                var has = pHas.GetValue(obj) as bool? ?? false;
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
