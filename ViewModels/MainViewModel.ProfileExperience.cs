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
    private const string SkinManagerWebPath = "immersion";

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
        public string Id { get; init; } = ""; // public clan key; never the DB id
        public string Name { get; init; } = "";
        public string Tag { get; init; } = "";
        public string? AvatarUrl { get; init; }
        public int MemberCount { get; init; }
        public string DisplayName => string.IsNullOrWhiteSpace(Tag) ? Name : $"[{Tag}] {Name}";
        public string MembersText => $"{MemberCount:N0} участников";
    }

    public sealed class ClanMemberEntry
    {
        public string UserId { get; init; } = "";
        public int? PublicId { get; init; }
        public string Name { get; init; } = "Без имени";
        public string RoleText { get; init; } = "Участник";
        public string? AvatarUrl { get; init; }
        public string PresenceText { get; init; } = "оффлайн";
        public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();
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
            if (Set(ref _friendSearchText, value ?? "")) RebuildFilteredFriends();
        }
    }

    private string _friendRequestQuery = "";
    public string FriendRequestQuery
    {
        get => _friendRequestQuery;
        set
        {
            if (Set(ref _friendRequestQuery, value ?? "")) RefreshProfileExperienceCanStates();
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

    public string ClanId => _clanId;
    public string ClanName => string.IsNullOrWhiteSpace(_clanName) ? "Нет клана" : _clanName;
    public string ClanTag => _clanTag;
    public string ClanRole => _clanRole;
    public string? ClanAvatarUrl => NormalizePublicUrl(_clanAvatarUrl);
    public int ClanMemberCount => _clanMemberCount;
    public long ClanTreasury => 0; // current launcher clan API does not expose treasury
    public bool HasClan => !string.IsNullOrWhiteSpace(_clanId) || Profile?.Clan is not null;
    public bool CanLeaveClan => HasClan;
    public string ClanRoleText => _clanRole.ToUpperInvariant() switch
    {
        "OWNER" => "Владелец",
        "OFFICER" => "Офицер",
        "MEMBER" => "Участник",
        _ => string.IsNullOrWhiteSpace(_clanRole) ? Profile?.Clan?.Rank?.Name ?? "" : _clanRole
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
            var title = (Profile?.Minecraft?.SelectedSkin?.Title ?? "").Trim();
            return title.Length == 0 ? "Стандартный образ" : title;
        }
    }

    public string SelectedSkinKey => (Profile?.Minecraft?.SelectedSkinKey ?? "").Trim();
    public string? SkinPreviewUrl => NormalizePublicUrl(Profile?.Minecraft?.SelectedSkin?.PreviewUrl ?? Profile?.Minecraft?.SelectedSkin?.SkinUrl);
    public bool HasSelectedSkin => Profile?.Minecraft?.SelectedSkin is not null;
    public string SkinStatusText => Profile?.Minecraft?.IsLinked == true
        ? HasSelectedSkin ? "Активный образ синхронизирован с аккаунтом." : "Minecraft привязан. Образ можно выбрать на странице «Погружение»."
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
        item => MutateFriendRequestAsync(item, true),
        item => !_isClosing && IsLoggedIn && HasSiteToken && !_isFriendActionBusy && item is not null);

    public AsyncRelayCommand<FriendRequestEntry> DeclineFriendRequestCommand => _declineFriendRequestCommand ??= new AsyncRelayCommand<FriendRequestEntry>(
        item => MutateFriendRequestAsync(item, false),
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
        () => OpenProfileWebPath(SkinManagerWebPath),
        () => !_isClosing && IsLoggedIn);

    private void EnsureProfileExperienceHooks()
    {
        if (Interlocked.Exchange(ref _profileExperienceHooksInitialized, 1) == 1) return;
        Friends.CollectionChanged += (_, _) => RebuildFilteredFriends();
        ApplyProfileSnapshotToExperience();
        RebuildFilteredFriends();
    }

    private async Task RefreshProfileExperienceAsync()
    {
        EnsureProfileExperienceHooks();
        if (!TryGetAccessToken(out var token)) return;

        SetProfileExperienceBusy(true);
        SetSocialStatus("Обновляю профиль…");
        try
        {
            await RefreshProfileSnapshotAsync(token).ConfigureAwait(false);
            await RefreshFriendsAsync().ConfigureAwait(false);
            await RefreshFriendRequestsAsync(token).ConfigureAwait(false);
            await RefreshClanAsync(token).ConfigureAwait(false);
            await RefreshProgressionAsync(token).ConfigureAwait(false);
            SetSocialStatus("Профиль синхронизирован.");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SetSocialStatus("Не всё удалось обновить: " + ex.Message);
            AppendLog("Профиль: ошибка обновления: " + ex.Message);
        }
        finally { SetProfileExperienceBusy(false); }
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
        catch (HttpRequestException ex) { AppendLog("Профиль: snapshot недоступен: " + ex.Message); }
    }

    private async Task RefreshFriendRequestsAsync(string token)
    {
        var response = await _site.GetFriendRequestsAsync(token, _lifetimeCts.Token).ConfigureAwait(false);
        if (!response.Ok)
        {
            SetSocialStatus(response.Error ?? "Не удалось загрузить заявки в друзья.");
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
            SetSocialStatus(response.Ok
                ? response.Status switch
                {
                    "auto_accepted" => "Встречная заявка найдена — дружба подтверждена.",
                    "already_sent" => "Заявка уже отправлена.",
                    _ => "Заявка в друзья отправлена."
                }
                : response.Error ?? response.Message ?? "Не удалось отправить заявку.");

            if (!response.Ok) return;
            PostToUi(() => FriendRequestQuery = "");
            await RefreshFriendsAsync().ConfigureAwait(false);
            await RefreshFriendRequestsAsync(token).ConfigureAwait(false);
        }
        finally { SetFriendActionBusy(false); }
    }

    private async Task MutateFriendRequestAsync(FriendRequestEntry? item, bool accept)
    {
        if (item is null || !TryGetAccessToken(out var token)) return;
        SetFriendActionBusy(true);
        try
        {
            var response = accept
                ? await _site.AcceptFriendRequestAsync(token, item.UserId, _lifetimeCts.Token).ConfigureAwait(false)
                : await _site.DeclineFriendRequestAsync(token, item.UserId, _lifetimeCts.Token).ConfigureAwait(false);
            SetSocialStatus(response.Ok
                ? accept ? $"{item.Name} добавлен в друзья." : $"Заявка от {item.Name} отклонена."
                : response.Error ?? response.Message ?? "Операция не выполнена.");
            if (!response.Ok) return;
            await RefreshFriendsAsync().ConfigureAwait(false);
            await RefreshFriendRequestsAsync(token).ConfigureAwait(false);
        }
        finally { SetFriendActionBusy(false); }
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
            SetSocialStatus(response.Ok ? $"{item.Name} удалён из друзей." : response.Error ?? response.Message ?? "Не удалось удалить друга.");
            if (response.Ok) await RefreshFriendsAsync().ConfigureAwait(false);
        }
        finally { SetFriendActionBusy(false); }
    }

    private async Task RefreshClanAsync(string token)
    {
        SetClanBusy(true);
        try
        {
            var response = await _profileApi.GetClanAsync(token, _lifetimeCts.Token).ConfigureAwait(false);
            if (!response.Ok)
            {
                SetClanStatus(response.Error ?? "Не удалось загрузить клан.");
                return;
            }

            ApplyClan(response);
            if (!response.HasClan || response.Clan is null)
            {
                SetClanStatus("Вы пока не состоите в клане.");
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
                    _clanMemberCount = mapped.Length;
                    Raise(nameof(ClanMemberCount));
                }, DispatcherPriority.DataBind);
            }
            SetClanStatus("Клан синхронизирован.");
        }
        finally { SetClanBusy(false); }
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
                SetClanStatus(response.Error ?? "Не удалось найти кланы.");
                return;
            }

            var mapped = response.Clans
                .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => new ClanBrowserEntry
                {
                    Id = x.Key.Trim(),
                    Name = x.Name.Trim(),
                    Tag = x.Key.Trim(),
                    AvatarUrl = NormalizePublicUrl(x.EmblemUrl ?? x.Image),
                    MemberCount = Math.Max(0, x.MembersCount)
                }).ToArray();

            PostToUi(() =>
            {
                ClanSearchResults.Clear();
                foreach (var clan in mapped) ClanSearchResults.Add(clan);
                Raise(nameof(HasClanSearchResults));
            }, DispatcherPriority.DataBind);
            SetClanStatus(mapped.Length == 0 ? "Кланы не найдены." : $"Найдено кланов: {mapped.Length}.");
        }
        finally { SetClanBusy(false); }
    }

    private async Task JoinClanAsync(ClanBrowserEntry? item)
    {
        if (item is null || !TryGetAccessToken(out var token) || HasClan) return;
        SetClanBusy(true);
        try
        {
            var response = await _profileApi.JoinClanAsync(token, item.Id, _lifetimeCts.Token).ConfigureAwait(false);
            SetClanStatus(response.Ok ? $"Вы вступили в клан {item.DisplayName}." : response.Error ?? response.Message ?? "Не удалось вступить в клан.");
            if (!response.Ok) return;
            PostToUi(() => ClanSearchResults.Clear());
            await RefreshProfileSnapshotAsync(token).ConfigureAwait(false);
            await RefreshClanAsync(token).ConfigureAwait(false);
        }
        finally { SetClanBusy(false); }
    }

    private async Task LeaveClanAsync()
    {
        if (!TryGetAccessToken(out var token) || !CanLeaveClan) return;
        SetClanBusy(true);
        try
        {
            var response = await _profileApi.LeaveClanAsync(token, _lifetimeCts.Token).ConfigureAwait(false);
            SetClanStatus(response.Ok ? "Вы покинули клан." : response.Error ?? response.Message ?? "Не удалось покинуть клан.");
            if (!response.Ok) return;
            ClearClanState();
            await RefreshProfileSnapshotAsync(token).ConfigureAwait(false);
        }
        finally { SetClanBusy(false); }
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
            PostToUi(() => ApplyProgression(response.Level, response.XpTotal, response.XpSeason, response.XpIntoLevel,
                response.XpForNext, response.XpProgress, response.BalanceRzn), DispatcherPriority.DataBind);
        }
        finally { SetProgressionBusy(false); }
    }

    private void ApplyProfileSnapshotToExperience()
    {
        var clan = Profile?.Clan;
        if (clan is not null && string.IsNullOrWhiteSpace(_clanName))
        {
            _clanId = (clan.Key ?? clan.Name ?? "").Trim();
            _clanName = (clan.Name ?? "").Trim();
            _clanTag = (clan.Key ?? "").Trim();
            _clanRole = clan.Rank?.IsLeader == true ? "OWNER" : (clan.Rank?.Key ?? clan.Rank?.Name ?? "MEMBER");
            _clanAvatarUrl = clan.EmblemUrl;
        }

        var progression = Profile?.Progression;
        if (progression is not null)
            ApplyProgression(progression.Level, progression.XpTotal, progression.XpSeason, progression.XpIntoLevel,
                progression.XpForNext, progression.XpProgress, Rezonite);

        RaiseProfileExperiencePresentation();
    }

    private void ApplyProgression(int level, long total, long season, long intoLevel, long forNext, double progress, long balance)
    {
        _profileLevel = Math.Max(1, level);
        _profileXpTotal = Math.Max(0, total);
        _profileXpSeason = Math.Max(0, season);
        _profileXpIntoLevel = Math.Max(0, intoLevel);
        _profileXpForNext = Math.Max(0, forNext);
        if (progress <= 1.0) progress *= 100.0;
        _profileXpProgressPercent = Math.Clamp(progress, 0.0, 100.0);
        if (balance >= 0) Rezonite = balance;
        RaiseProgressionPresentation();
    }

    private void ApplyClan(LauncherProfileService.ClanResponse response)
    {
        PostToUi(() =>
        {
            var clan = response.Clan;
            _clanId = response.HasClan ? (clan?.Key ?? clan?.Id ?? "").Trim() : "";
            _clanName = response.HasClan ? (clan?.Name ?? "").Trim() : "";
            _clanTag = response.HasClan ? (clan?.Key ?? "").Trim() : "";
            _clanRole = response.IsLeader ? "OWNER" : (response.Rank?.Key ?? response.Rank?.Name ?? (response.HasClan ? "MEMBER" : ""));
            _clanAvatarUrl = response.HasClan ? clan?.EmblemUrl ?? clan?.Image : null;
            if (!response.HasClan) ClearClanCollectionsOnly();
            RaiseClanPresentation();
        }, DispatcherPriority.DataBind);
    }

    private void ClearClanState()
    {
        PostToUi(() =>
        {
            _clanId = "";
            _clanName = "";
            _clanTag = "";
            _clanRole = "";
            _clanAvatarUrl = null;
            _clanMemberCount = 0;
            ClearClanCollectionsOnly();
            RaiseClanPresentation();
        }, DispatcherPriority.DataBind);
    }

    private void ClearClanCollectionsOnly()
    {
        ClanMembers.Clear();
        _clanMemberCount = 0;
    }

    private ClanMemberEntry MapClanMember(LauncherProfileService.ClanMemberDto dto)
    {
        var name = (dto.Name ?? "").Trim();
        var friend = Friends.FirstOrDefault(f =>
            (dto.PublicId.HasValue && f.PublicId == dto.PublicId) ||
            (!string.IsNullOrWhiteSpace(dto.UserId) && string.Equals(f.UserId, dto.UserId, StringComparison.OrdinalIgnoreCase)));

        return new ClanMemberEntry
        {
            UserId = dto.UserId,
            PublicId = dto.PublicId,
            Name = name.Length == 0 ? "Без имени" : name,
            RoleText = dto.IsLeader ? "Владелец" : string.IsNullOrWhiteSpace(dto.RankName) ? "Участник" : dto.RankName!,
            AvatarUrl = NormalizePublicUrl(dto.Image),
            PresenceText = friend?.PresenceText is { Length: > 0 } presence ? presence : "оффлайн"
        };
    }

    private static FriendRequestEntry ToFriendRequestEntry(SiteAuthService.FriendDto dto)
    {
        var name = (dto.Name ?? "").Trim();
        return new FriendRequestEntry
        {
            UserId = (dto.UserId ?? dto.Id ?? "").Trim(),
            PublicId = dto.PublicId,
            Name = name.Length == 0 ? "Без имени" : name,
            AvatarUrl = NormalizePublicUrl(dto.Image)
        };
    }

    private void RebuildFilteredFriends()
    {
        var query = (FriendSearchText ?? "").Trim();
        var source = Friends
            .Where(x => query.Length == 0 || x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
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

    private void SetSocialStatus(string text) => PostToUi(() => SocialStatusText = text);
    private void SetClanStatus(string text) => PostToUi(() => ClanStatusText = text);

    private void SetProfileExperienceBusy(bool value)
    {
        _isProfileExperienceBusy = value;
        PostToUi(() => { Raise(nameof(IsProfileExperienceBusy)); RefreshProfileExperienceCanStates(); });
    }

    private void SetFriendActionBusy(bool value)
    {
        _isFriendActionBusy = value;
        PostToUi(() => { Raise(nameof(IsFriendActionBusy)); RefreshProfileExperienceCanStates(); });
    }

    private void SetClanBusy(bool value)
    {
        _isClanBusy = value;
        PostToUi(() => { Raise(nameof(IsClanBusy)); RefreshProfileExperienceCanStates(); });
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
        Raise(nameof(ClanId)); Raise(nameof(ClanName)); Raise(nameof(ClanTag)); Raise(nameof(ClanRole));
        Raise(nameof(ClanRoleText)); Raise(nameof(ClanAvatarUrl)); Raise(nameof(ClanMemberCount)); Raise(nameof(ClanTreasury));
        Raise(nameof(HasClan)); Raise(nameof(CanLeaveClan));
        RefreshProfileExperienceCanStates();
    }

    private void RaiseProgressionPresentation()
    {
        Raise(nameof(ProfileLevel)); Raise(nameof(ProfileXpTotal)); Raise(nameof(ProfileXpSeason));
        Raise(nameof(ProfileXpIntoLevel)); Raise(nameof(ProfileXpForNext)); Raise(nameof(ProfileXpProgressPercent));
        Raise(nameof(ProfileXpLevelText));
    }

    private void RaiseProfileExperiencePresentation()
    {
        RaiseClanPresentation();
        RaiseProgressionPresentation();
        Raise(nameof(SelectedSkinTitle)); Raise(nameof(SelectedSkinKey)); Raise(nameof(SkinPreviewUrl));
        Raise(nameof(HasSelectedSkin)); Raise(nameof(SkinStatusText));
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
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog("Не удалось открыть сайт: " + ex.Message); }
    }
}
