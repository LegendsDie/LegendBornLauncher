using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private int _profileExperienceAccountScopeHooked;

    /// <summary>
    /// Profile-only collections are populated after the profile tab is opened. Once that happens,
    /// keep an account-scope observer alive for the rest of the window lifetime so logout can never
    /// leave requests/clan/progression from the previous account visible to the next account.
    /// </summary>
    internal void EnsureProfileExperienceAccountScope()
    {
        if (Interlocked.Exchange(ref _profileExperienceAccountScopeHooked, 1) == 1)
            return;

        PropertyChanged += OnProfileExperienceOwnerPropertyChanged;
        if (!IsLoggedIn)
            ClearProfileExperienceAccountState();
    }

    private void OnProfileExperienceOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(IsLoggedIn), StringComparison.Ordinal) && !IsLoggedIn)
            ClearProfileExperienceAccountState();
    }

    private void ClearProfileExperienceAccountState()
    {
        void ClearCore()
        {
            FilteredFriends.Clear();
            IncomingFriendRequests.Clear();
            OutgoingFriendRequests.Clear();
            ClanSearchResults.Clear();
            ClanMembers.Clear();

            _friendSearchText = "";
            _friendRequestQuery = "";
            _socialStatusText = "";
            _clanSearchText = "";
            _clanStatusText = "";

            _clanId = "";
            _clanName = "";
            _clanTag = "";
            _clanRole = "";
            _clanAvatarUrl = null;
            _clanMemberCount = 0;

            _profileLevel = 1;
            _profileXpTotal = 0;
            _profileXpSeason = 0;
            _profileXpIntoLevel = 0;
            _profileXpForNext = 0;
            _profileXpProgressPercent = 0;

            _isProfileExperienceBusy = false;
            _isFriendActionBusy = false;
            _isClanBusy = false;
            _isProgressionBusy = false;

            Raise(nameof(FriendSearchText));
            Raise(nameof(FriendRequestQuery));
            Raise(nameof(SocialStatusText));
            Raise(nameof(ClanSearchText));
            Raise(nameof(ClanStatusText));
            Raise(nameof(FilteredFriendsCount));
            Raise(nameof(IncomingFriendRequestCount));
            Raise(nameof(OutgoingFriendRequestCount));
            Raise(nameof(HasIncomingFriendRequests));
            Raise(nameof(HasOutgoingFriendRequests));
            Raise(nameof(HasClanSearchResults));
            Raise(nameof(IsProfileExperienceBusy));
            Raise(nameof(IsFriendActionBusy));
            Raise(nameof(IsClanBusy));
            Raise(nameof(IsProgressionBusy));
            RaiseProfileExperiencePresentation();
            RefreshProfileExperienceCanStates();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ClearCore();
            return;
        }

        try
        {
            dispatcher.Invoke(ClearCore);
        }
        catch
        {
            // Window shutdown can race the logout observer; no UI remains to expose in that case.
        }
    }
}
