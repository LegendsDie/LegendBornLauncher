using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LegendBorn.Mvvm;
using LegendBorn.Services;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private readonly LauncherProfileService _profileApi = new();
    private int _profileExperienceHooksInitialized;
    private bool _isProfileExperienceBusy;
    private bool _isFriendActionBusy;
    private bool _isClanBusy;
    private bool _isProgressionBusy;

    public sealed class FriendRequestEntry
    {
        public string UserId { get; init; } = "";
        public int? PublicId { get; init; }
        public string Name { get; init; } = "Без имени";
        public string? AvatarUrl { get; init; }
        public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();
    }

    public sealed class ClanBrowserEntry
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Tag { get; init; } = "";
        public string? AvatarUrl { get; init; }
        public int MemberCount { get; init; }
        public string DisplayName => string.IsNullOrWhiteSpace(Tag) ? Name : $"[{Tag}] {Name}";
        public string MembersText => $"{MemberCount:N0} участников";
    }

    public sealed class ClanMemberEntry
    {
        public int? PublicId { get; init; }
        public string Name { get; init; } = "Без имени";
        public string Role { get; init; } = "MEMBER";
        public string? AvatarUrl { get; init; }
        public string Presence { get; init; } = "offline";
        public string? ServerKey { get; init; }
        public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();
        public bool IsOnline => !Presence.Equals("offline", StringComparison.OrdinalIgnoreCase);
        public string PresenceText => IsOnline
            ? string.IsNullOrWhiteSpace(ServerKey) ? "в сети" : $"в сети • {ServerKey}"
            : "оффлайн";
        public string RoleText => Role.ToUpperInvariant() switch
        {
            "OWNER" => "Владелец",
            "OFFICER" => "Офицер",
            _ => "Участник"
        };
    }

    public ObservableCollection<FriendEntry> FilteredFriends { get; } = new();
    public ObservableCollection<FriendRequestEntry> IncomingFriendRequests { get; } = new();
    public ObservableCollection<FriendRequestEntry> OutgoingFriendRequests { get; } = new();
    public ObservableCollection<ClanBrowserEntry> ClanSearchResults { get; } = new();
    public ObservableCollection<ClanMemberEntry> ClanMembers { get; } = new();

    private string _friendSearchText = "";
    public string FriendSearchText
    {
        get => _friendSearchText;
        set
        {
            if (Set(ref _friendSearchText, value ?? ""))
                RebuildFilteredFriends();
        }
    }

    private string _friendRequestQuery = "";
    public string FriendRequestQuery
    {
        get => _friendRequestQuery;
        set
        {
            if (Set(ref _friendRequestQuery, value ?? ""))
                RefreshProfileExperienceCanStates();
        }
    }

    private string _socialStatusText = "";
    public string SocialStatusText
    {
        get => _socialStatusText;
        private set => Set(ref _socialStatusText, value);
    }

    private string _clanSearchText = "";
    public string ClanSearchText
    {
        get => _clanSearchText;
        set => Set(ref _clanSearchText, value ?? "");
    }

    private string _clanStatusText = "";
    public string ClanStatusText
    {
        get => _clanStatusText;
        private set => Set(ref _clanStatusText, value);
    }

    private string _clanId = "";
    private string _clanName = "";
    private string _clanTag = "";
    private string _clanRole = "";
    private string? _clanAvatarUrl;
    private int _clanMemberCount;
    private long _clanTreasury;

    public string ClanId => _clanId;
    public string ClanName => string.IsNullOrWhiteSpace(_clanName) ? "Нет клана" : _clanName;
    public string ClanTag => _clanTag;
    public string ClanRole => _clanRole;
    public string? ClanAvatarUrl => NormalizePublicUrl(_clanAvatarUrl);
    public int ClanMemberCount => _clanMemberCount;
    public long ClanTreasury => _clanTreasury;
    public bool HasClan => !string.IsNullOrWhiteSpace(_clanId) || Profile?.Clan is not null;
    public bool CanLeaveClan => HasClan && !_clanRole.Equals("OWNER", StringComparison.OrdinalIgnoreCase);
    public string ClanRoleText => _clanRole.ToUpperInvariant() switch
    {
        "OWNER" => "Владелец",
        "OFFICER" => "Офицер",
        "MEMBER" => "Участник",
        _ => Profile?.Clan?.Rank?.Name ?? ""
    };

    private int _profileLevel = 1;
    private long _profileXpTotal;
    private long _profileXpSeason;
    private long _profileXpIntoLevel;
    private long _profileXpForNext;
    private double _profileXpProgressPercent;

    public int ProfileLevel => _profileLevel;
    public long ProfileXpTotal => _profileXpTotal;
    public long ProfileXpSeason => _profileXpSeason;
    public long ProfileXpIntoLevel => _profileXpIntoLevel;
    public long ProfileXpForNext => _profileXpForNext;
    public double ProfileXpProgressPercent => _profileXpProgressPercent;
    public string ProfileXpLevelText => _profileXpForNext > 0
        ? $"{_profileXpIntoLevel:N0} / {_profileXpForNext:N0} XP"
        : $"{_profileXpTotal:N0} XP";

    public int IncomingFriendRequestCount => IncomingFriendRequests.Count;
    public int OutgoingFriendRequestCount => OutgoingFriendRequests.Count;
    public int FilteredFriendsCount => FilteredFriends.Count;
    public bool HasIncomingFriendRequests => IncomingFriendRequests.Count > 0;
    public bool HasOutgoingFriendRequests => OutgoingFriendRequests.Count > 0;
    public bool HasClanSearchResults => ClanSearchResults.Count > 0;
    public bool IsProfileExperienceBusy => _isProfileExperienceBusy;
    public bool IsFriendActionBusy => _isFriendActionBusy;
    public bool IsClanBusy => _isClanBusy;
    public bool IsProgressionBusy => _isProgressionBusy;

    public string SelectedSkinTitle
    {
        get
        {
            var skin = Profile?.Minecraft?.SelectedSkin;
            var title = (skin?.Title ?? "").Trim();
            return string.IsNullOrWhiteSpace(title) ? "Стандартный образ" : title;
        }
    }

    public string SelectedSkinKey => (Profile?.Minecraft?.SelectedSkinKey ?? "").Trim();
    public string? SkinPreviewUrl => NormalizePublicUrl(
        Profile?.Minecraft?.SelectedSkin?.PreviewUrl ?? Profile?.Minecraft?.SelectedSkin?.SkinUrl);
    public bool HasSelectedSkin => Profile?.Minecraft?.SelectedSkin is not null;
    public string SkinStatusText => Profile?.Minecraft?.IsLinked == true
        ? HasSelectedSkin ? "Активный образ синхронизирован с аккаунтом." : "Minecraft привязан. Можно выбрать образ на сайте."
        : "Сначала привяжите Minecraft-аккаунт.";

    private AsyncRelayCommand? _refreshProfileExperienceCommand;
    private AsyncRelayCommand? _sendFriendRequestCommand;
    private AsyncRelayCommand<FriendRequestEntry>? _acceptFriendRequestCommand;
    private AsyncRelayCommand<FriendRequestEntry>? _declineFriendRequestCommand;
    private AsyncRelayCommand<FriendEntry>? _removeFriendCommand;
    private AsyncRelayCommand? _searchClansCommand;
    private AsyncRelayCommand<ClanBrowserEntry>? _joinClanCommand;
    private AsyncRelayCommand? _leaveClanCommand;
    private RelayCommand? _openSkinManagerCommand;

    public AsyncRelayCommand RefreshProfileExperienceCommand => _refreshProfileExperienceCommand ??= new AsyncRelayCommand(
        RefreshProfileExperienceAsync,
        () => !_isClosing && IsLoggedIn && HasSiteToken && !_isProfileExperienceBusy);

    public AsyncRelayCommand SendFriendRequestCommand => _sendFriendRequestCommand ??= new AsyncRelayCommand(
        SendFriendRequestAsync,
        () => !_isClosing && IsLoggedIn && HasSiteToken && !_isFriendActionBusy && !string.IsNullOrWhiteSpace(FriendRequestQuery));

    public AsyncRelayCommand<FriendRequestEntry> AcceptFriendRequestCommand => _acceptFriendRequestCommand ??= new AsyncRelayCommand<FriendRequestEntry>(
        AcceptFriendRequestAsync,
        item => !_isClosing && IsLoggedIn && HasSiteToken && !_isFriendActionBusy && item is not null);

    public AsyncRelayCommand<FriendRequestEntry> DeclineFriendRequestCommand => _declineFriendRequestCommand ??= new AsyncRelayCommand<FriendRequestEntry>(
        DeclineFriendRequestAsync,
        item => !_isClosing && IsLoggedIn && HasSiteToken && !_isFriendActionBusy && item is not null);

    public AsyncRelayCommand<FriendEntry> RemoveFriendCommand => _removeFriendCommand ??= new AsyncRelayCommand<FriendEntry>(
        RemoveFriendAsync,
        item => !_isClosing && IsLoggedIn && HasSiteToken && !_isFriendActionBusy && item is not null);

    public AsyncRelayCommand SearchClansCommand => _searchClansCommand ??= new AsyncRelayCommand(
        SearchClansAsync,
        () => !_isClosing && IsLoggedIn && HasSiteToken && !_isClanBusy && !HasClan);

    public AsyncRelayCommand<ClanBrowserEntry> JoinClanCommand => _joinClanCommand ??= new AsyncRelayCommand<ClanBrowserEntry>(
        JoinClanAsync,
        item => !_isClosing && IsLoggedIn && HasSiteToken && !_isClanBusy && !HasClan && item is not null);

    public AsyncRelayCommand LeaveClanCommand => _leaveClanCommand ??= new AsyncRelayCommand(
        LeaveClanAsync,
        () => !_isClosing && IsLoggedIn && HasSiteToken && !_isClanBusy && CanLeaveClan);

    public RelayCommand OpenSkinManagerCommand => _openSkinManagerCommand ??= new RelayCommand(
        () => OpenProfileWebPath("minecraft/skin"),
        () => !_isClosing && IsLoggedIn);

    private void EnsureProfileExperienceHooks()
    {
        if (Interlocked.Exchange(ref _profileExperienceHooksInitialized, 1) == 1)
            return;

        Friends.CollectionChanged += (_, _) => RebuildFilteredFriends();
        ApplyProfileSnapshotToExperience();
        RebuildFilteredFriends();
    }

    private async Task RefreshProfileExperienceAsync()
    {
        EnsureProfileExperienceHooks();
        if (!TryGetAccessToken(out var token)) return;

        SetProfileExperienceBusy(true);
        SocialStatusText = "Обновляю профиль и социальные данные…";
        try
        {
            await RefreshProfileSnapshotAsync(token).ConfigureAwait(false);
            await RefreshFriendsAsync().ConfigureAwait(false);
            await RefreshFriendRequestsAsync(token).ConfigureAwait(false);
            await RefreshClanAsync(token).ConfigureAwait(false);
            await RefreshProgressionAsync(token).ConfigureAwait(false);
            SocialStatusText = "Профиль синхронизирован.";
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SocialStatusText = "Не всё удалось обновить: " + ex.Message;
            AppendLog("Профиль: ошибка обновления: " + ex.Message);
        }
        finally
        {
            SetProfileExperienceBusy(false);
        }
    }

    private async Task RefreshProfileSnapshotAsync(string token)
    {
        try
        {
            var profile = await _site.GetMeAsync(token, _lifetimeCts.Token).ConfigureAwait(false);
            PostToUi(() =>
            {
                Profile = profile;
                SiteUserName = profile.SafeUserName;
                ApplyProfileSnapshotToExperience();
            }, DispatcherPriority.DataBind);
        }
        catch (HttpRequestException ex)
        {
            AppendLog("Профиль: snapshot недоступен: " + ex.Message);
        }
    }

    private async Task RefreshFriendRequestsAsync(string token)
    {
        var response = await _site.GetFriendRequestsAsync(token, _lifetimeCts.Token).ConfigureAwait(false);
        if (!response.Ok)
        {
            SocialStatusText = response.Error ?? "Не удалось загрузить заявки в друзья.";
            return;
        }

        var incoming = response.Incoming.Select(ToFriendRequestEntry).ToArray();
        var outgoing = response.Outgoing.Select(ToFriendRequestEntry).ToArray();
        PostToUi(() =>
        {
            IncomingFriendRequests.Clear();
            foreach (var item in incoming) IncomingFriendRequests.Add(item);
            OutgoingFriendRequests.Clear();
            foreach (var item in outgoing) OutgoingFriendRequests.Add(item);
            RaiseFriendRequestPresentation();
        }, DispatcherPriority.DataBind);
    }

    private async Task SendFriendRequestAsync()
    {
        if (!TryGetAccessToken(out var token)) return;
        var query = FriendRequestQuery.Trim();
        if (query.Length == 0) return;

        SetFriendActionBusy(true);
        try
        {
            var response = await _site.SendFriendRequestAsync(token, query, _lifetimeCts.Token).ConfigureAwait(false);
            SocialStatusText = response.Ok
                ? response.Status switch
                {
                    "auto_accepted" => "Вы уже были во встречной заявке — дружба подтверждена.",
                    "already_sent" => "Заявка уже отправлена.",
                    _ => "Заявка в друзья отправлена."
                }
                : response.Error ?? response.Message ?? "Не удалось отправить заявку.";

            if (response.Ok)
            {
                PostToUi(() => FriendRequestQuery = "");
                await RefreshFriendsAsync().ConfigureAwait(false);
                await RefreshFriendRequestsAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            SetFriendActionBusy(false);
        }
    }

    private Task AcceptFriendRequestAsync(FriendRequestEntry? item)
        => MutateFriendRequestAsync(item, accept: true);

    private Task DeclineFriendRequestAsync(FriendRequestEntry? item)
        => MutateFriendRequestAsync(item, accept: false);

    private async Task MutateFriendRequestAsync(FriendRequestEntry? item, bool accept)
    {
        if (item is null || !TryGetAccessToken(out var token)) return;
        SetFriendActionBusy(true);
        try
        {
            var response = accept
                ? await _site.AcceptFriendRequestAsync(token, item.UserId, _lifetimeCts.Token).ConfigureAwait(false)
                : await _site.DeclineFriendRequestAsync(token, item.UserId, _lifetimeCts.Token).ConfigureAwait(false);

            SocialStatusText = response.Ok
                ? accept ? $"{item.Name} добавлен в друзья." : $"Заявка от {item.Name} отклонена."
                : response.Error ?? response.Message ?? "Операция не выполнена.";

            if (response.Ok)
            {
                await RefreshFriendsAsync().ConfigureAwait(false);
                await RefreshFriendRequestsAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            SetFriendActionBusy(false);
        }
    }

    private async Task RemoveFriendAsync(FriendEntry? item)
    {
        if (item is null || !TryGetAccessToken(out var token)) return;
        var userId = (item.UserId ?? item.InternalId ?? item.Id).Trim();
        if (userId.Length == 0) return;

        SetFriendActionBusy(true);
        try
        {
            var response = await _site.RemoveFriendAsync(token, userId, _lifetimeCts.Token).ConfigureAwait(false);
            SocialStatusText = response.Ok
                ? $"{item.Name} удалён из друзей."
                : response.Error ?? response.Message ?? "Не удалось удалить друга.";
            if (response.Ok)
                await RefreshFriendsAsync().ConfigureAwait(false);
        }
        finally
        {
            SetFriendActionBusy(false);
        }
    }

    private async Task RefreshClanAsync(string token)
    {
        SetClanBusy(true);
        try
        {
            var response = await _profileApi.GetClanAsync(token, _lifetimeCts.Token).ConfigureAwait(false);
            if (!response.Ok)
            {
                ClanStatusText = response.Error ?? "Не удалось загрузить клан.";
                return;
            }

            ApplyClan(response.Clan);
            if (response.Clan is null)
            {
                ClanStatusText = "Вы пока не состоите в клане.";
                return;
            }

            var members = await _profileApi.GetClanMembersAsync(token, _lifetimeCts.Token).ConfigureAwait(false);
            if (members.Ok)
            {
                var mapped = members.Members.Select(MapClanMember).ToArray();
                PostToUi(() =>
                {
                    ClanMembers.Clear();
                    foreach (var member in mapped) ClanMembers.Add(member);
                    Raise(nameof(ClanMemberCount));
                }, DispatcherPriority.DataBind);
            }
            ClanStatusText = "Клан синхронизирован.";
        }
        finally
        {
            SetClanBusy(false);
        }
    }

    private async Task SearchClansAsync()
    {
        if (!TryGetAccessToken(out var token) || HasClan) return;
        SetClanBusy(true);
        try
        {
            var response = await _profileApi.SearchClansAsync(token, ClanSearchText, _lifetimeCts.Token).ConfigureAwait(false);
            if (!response.Ok)
            {
                ClanStatusText = response.Error ?? "Не удалось найти кланы.";
                return;
            }

            var mapped = response.Clans
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => new ClanBrowserEntry
                {
                    Id = x.Id,
                    Name = x.Name.Trim(),
                    Tag = (x.Tag ?? "").Trim(),
                    AvatarUrl = NormalizePublicUrl(x.AvatarUrl),
                    MemberCount = Math.Max(0, x.MemberCount)
                })
                .ToArray();

            PostToUi(() =>
            {
                ClanSearchResults.Clear();
                foreach (var clan in mapped) ClanSearchResults.Add(clan);
                Raise(nameof(HasClanSearchResults));
            }, DispatcherPriority.DataBind);
            ClanStatusText = mapped.Length == 0 ? "Кланы не найдены." : $"Найдено кланов: {mapped.Length}.";
        }
        finally
        {
            SetClanBusy(false);
        }
    }

    private async Task JoinClanAsync(ClanBrowserEntry? item)
    {
        if (item is null || !TryGetAccessToken(out var token) || HasClan) return;
        SetClanBusy(true);
        try
        {
            var response = await _profileApi.JoinClanAsync(token, item.Id, _lifetimeCts.Token).ConfigureAwait(false);
            ClanStatusText = response.Ok
                ? $"Вы вступили в клан {item.DisplayName}."
                : response.Error ?? response.Message ?? "Не удалось вступить в клан.";
            if (response.Ok)
            {
                PostToUi(() => ClanSearchResults.Clear());
                await RefreshClanAsync(token).ConfigureAwait(false);
                await RefreshProfileSnapshotAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            SetClanBusy(false);
        }
    }

    private async Task LeaveClanAsync()
    {
        if (!TryGetAccessToken(out var token) || !CanLeaveClan) return;
        SetClanBusy(true);
        try
        {
            var response = await _profileApi.LeaveClanAsync(token, _lifetimeCts.Token).ConfigureAwait(false);
            ClanStatusText = response.Ok
                ? "Вы покинули клан."
                : response.Error ?? response.Message ?? "Не удалось покинуть клан.";
            if (response.Ok)
            {
                ApplyClan(null);
                await RefreshProfileSnapshotAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            SetClanBusy(false);
        }
    }

    private async Task RefreshProgressionAsync(string token)
    {
        SetProgressionBusy(true);
        try
        {
            var response = await _profileApi.GetProgressionAsync(token, _lifetimeCts.Token).ConfigureAwait(false);
            if (!response.Ok)
            {
                AppendLog("Прогрессия: " + (response.Error ?? "ответ не OK"));
                return;
            }

            PostToUi(() =>
            {
                _profileLevel = Math.Max(1, response.Level);
                _profileXpTotal = Math.Max(0, response.XpTotal);
                _profileXpSeason = Math.Max(0, response.XpSeason);
                _profileXpIntoLevel = Math.Max(0, response.XpIntoLevel);
                _profileXpForNext = Math.Max(0, response.XpForNext);
                var progress = response.XpProgress;
                if (progress <= 1.0) progress *= 100.0;
                _profileXpProgressPercent = Math.Clamp(progress, 0.0, 100.0);
                if (response.BalanceRzn >= 0) Rezonite = response.BalanceRzn;
                RaiseProgressionPresentation();
            }, DispatcherPriority.DataBind);
        }
        finally
        {
            SetProgressionBusy(false);
        }
    }

    private void ApplyProfileSnapshotToExperience()
    {
        var clan = Profile?.Clan;
        if (clan is not null && string.IsNullOrWhiteSpace(_clanName))
        {
            _clanName = (clan.Name ?? "").Trim();
            _clanTag = (clan.Key ?? "").Trim();
            _clanRole = clan.Rank?.IsLeader == true ? "OWNER" : (clan.Rank?.Key ?? "MEMBER");
        }

        var progression = Profile?.Progression;
        if (progression is not null)
        {
            _profileLevel = Math.Max(1, progression.Level);
            _profileXpTotal = Math.Max(0, progression.XpTotal);
            _profileXpSeason = Math.Max(0, progression.XpSeason);
            _profileXpIntoLevel = Math.Max(0, progression.XpIntoLevel);
            _profileXpForNext = Math.Max(0, progression.XpForNext);
            var progress = progression.XpProgress;
            if (progress <= 1.0) progress *= 100.0;
            _profileXpProgressPercent = Math.Clamp(progress, 0.0, 100.0);
        }

        RaiseProfileExperiencePresentation();
    }

    private void ApplyClan(LauncherProfileService.ClanDto? clan)
    {
        PostToUi(() =>
        {
            _clanId = clan?.Id?.Trim() ?? "";
            _clanName = clan?.Name?.Trim() ?? "";
            _clanTag = clan?.Tag?.Trim() ?? "";
            _clanRole = clan?.Role?.Trim() ?? "";
            _clanAvatarUrl = clan?.AvatarUrl;
            _clanMemberCount = Math.Max(0, clan?.MemberCount ?? 0);
            _clanTreasury = Math.Max(0, clan?.Treasury ?? 0);
            if (clan is null) ClanMembers.Clear();
            RaiseClanPresentation();
        }, DispatcherPriority.DataBind);
    }

    private static FriendRequestEntry ToFriendRequestEntry(SiteAuthService.FriendDto dto)
    {
        var name = (dto.Name ?? "").Trim();
        var userId = (dto.UserId ?? dto.Id ?? "").Trim();
        return new FriendRequestEntry
        {
            UserId = userId,
            PublicId = dto.PublicId,
            Name = string.IsNullOrWhiteSpace(name) ? "Без имени" : name,
            AvatarUrl = NormalizePublicUrl(dto.Image)
        };
    }

    private static ClanMemberEntry MapClanMember(LauncherProfileService.ClanMemberDto dto)
    {
        var name = (dto.User.Nick ?? "").Trim();
        return new ClanMemberEntry
        {
            PublicId = dto.User.PublicId,
            Name = string.IsNullOrWhiteSpace(name) ? "Без имени" : name,
            Role = string.IsNullOrWhiteSpace(dto.Role) ? "MEMBER" : dto.Role,
            AvatarUrl = NormalizePublicUrl(dto.User.AvatarUrl),
            Presence = (dto.User.Presence ?? "offline").Trim(),
            ServerKey = string.IsNullOrWhiteSpace(dto.User.PresenceServerKey) ? null : dto.User.PresenceServerKey.Trim()
        };
    }

    private void RebuildFilteredFriends()
    {
        var query = (FriendSearchText ?? "").Trim();
        var source = Friends
            .Where(x => query.Length == 0 ||
                        x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        (x.PublicId?.ToString() ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => (int)x.OnlinePlace)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        PostToUi(() =>
        {
            FilteredFriends.Clear();
            foreach (var friend in source) FilteredFriends.Add(friend);
            Raise(nameof(FilteredFriendsCount));
        }, DispatcherPriority.DataBind);
    }

    private void SetProfileExperienceBusy(bool value)
    {
        _isProfileExperienceBusy = value;
        PostToUi(() =>
        {
            Raise(nameof(IsProfileExperienceBusy));
            RefreshProfileExperienceCanStates();
        });
    }

    private void SetFriendActionBusy(bool value)
    {
        _isFriendActionBusy = value;
        PostToUi(() =>
        {
            Raise(nameof(IsFriendActionBusy));
            RefreshProfileExperienceCanStates();
        });
    }

    private void SetClanBusy(bool value)
    {
        _isClanBusy = value;
        PostToUi(() =>
        {
            Raise(nameof(IsClanBusy));
            RefreshProfileExperienceCanStates();
        });
    }

    private void SetProgressionBusy(bool value)
    {
        _isProgressionBusy = value;
        PostToUi(() => Raise(nameof(IsProgressionBusy)));
    }

    private void RaiseFriendRequestPresentation()
    {
        Raise(nameof(IncomingFriendRequestCount));
        Raise(nameof(OutgoingFriendRequestCount));
        Raise(nameof(HasIncomingFriendRequests));
        Raise(nameof(HasOutgoingFriendRequests));
        RefreshProfileExperienceCanStates();
    }

    private void RaiseClanPresentation()
    {
        Raise(nameof(ClanId));
        Raise(nameof(ClanName));
        Raise(nameof(ClanTag));
        Raise(nameof(ClanRole));
        Raise(nameof(ClanRoleText));
        Raise(nameof(ClanAvatarUrl));
        Raise(nameof(ClanMemberCount));
        Raise(nameof(ClanTreasury));
        Raise(nameof(HasClan));
        Raise(nameof(CanLeaveClan));
        RefreshProfileExperienceCanStates();
    }

    private void RaiseProgressionPresentation()
    {
        Raise(nameof(ProfileLevel));
        Raise(nameof(ProfileXpTotal));
        Raise(nameof(ProfileXpSeason));
        Raise(nameof(ProfileXpIntoLevel));
        Raise(nameof(ProfileXpForNext));
        Raise(nameof(ProfileXpProgressPercent));
        Raise(nameof(ProfileXpLevelText));
    }

    private void RaiseProfileExperiencePresentation()
    {
        RaiseClanPresentation();
        RaiseProgressionPresentation();
        Raise(nameof(SelectedSkinTitle));
        Raise(nameof(SelectedSkinKey));
        Raise(nameof(SkinPreviewUrl));
        Raise(nameof(HasSelectedSkin));
        Raise(nameof(SkinStatusText));
    }

    private void RefreshProfileExperienceCanStates()
    {
        try { _refreshProfileExperienceCommand?.RaiseCanExecuteChanged(); } catch { }
        try { _sendFriendRequestCommand?.RaiseCanExecuteChanged(); } catch { }
        try { _acceptFriendRequestCommand?.RaiseCanExecuteChanged(); } catch { }
        try { _declineFriendRequestCommand?.RaiseCanExecuteChanged(); } catch { }
        try { _removeFriendCommand?.RaiseCanExecuteChanged(); } catch { }
        try { _searchClansCommand?.RaiseCanExecuteChanged(); } catch { }
        try { _joinClanCommand?.RaiseCanExecuteChanged(); } catch { }
        try { _leaveClanCommand?.RaiseCanExecuteChanged(); } catch { }
        try { _openSkinManagerCommand?.RaiseCanExecuteChanged(); } catch { }
    }

    private void OpenProfileWebPath(string relative)
    {
        try
        {
            relative = (relative ?? "").Trim().TrimStart('/');
            var url = SitePublicUrlPrimary.TrimEnd('/') + "/" + relative;
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("Не удалось открыть сайт: " + ex.Message);
        }
    }
}
