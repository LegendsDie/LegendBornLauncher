// File: ViewModels/MainViewModel.Social.cs
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Models;
using LegendBorn.Mvvm;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private int _socialRefreshGuard;
    private bool _socialHooksInstalled;

    public sealed class FriendEntry
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string? AvatarUrl { get; init; }
        public string? PublicId { get; init; }
        public string? Status { get; init; }

        public override string ToString() => Name;
    }

    public ObservableCollection<FriendEntry> Friends { get; } = new();
    public ObservableCollection<FriendEntry> IncomingFriendRequests { get; } = new();
    public ObservableCollection<FriendEntry> OutgoingFriendRequests { get; } = new();

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

    private FriendEntry? _selectedIncomingRequest;
    public FriendEntry? SelectedIncomingRequest
    {
        get => _selectedIncomingRequest;
        set
        {
            if (Set(ref _selectedIncomingRequest, value))
                RefreshCanStates();
        }
    }

    private FriendEntry? _selectedOutgoingRequest;
    public FriendEntry? SelectedOutgoingRequest
    {
        get => _selectedOutgoingRequest;
        set
        {
            if (Set(ref _selectedOutgoingRequest, value))
                RefreshCanStates();
        }
    }

    private string _friendQuery = "";
    public string FriendQuery
    {
        get => _friendQuery;
        set
        {
            if (Set(ref _friendQuery, value))
                RefreshCanStates();
        }
    }

    public int FriendsCount => Friends.Count;
    public int IncomingRequestsCount => IncomingFriendRequests.Count;
    public int OutgoingRequestsCount => OutgoingFriendRequests.Count;

    public string FriendsSummaryText
        => $"Друзья: {FriendsCount} • Входящие: {IncomingRequestsCount} • Исходящие: {OutgoingRequestsCount}";

    public bool CanUseSocialApi
        => !_isClosing
           && !IsBusy
           && IsLoggedIn
           && TryGetAccessToken(out _);

    public AsyncRelayCommand RefreshFriendsCommand { get; private set; } = null!;
    public AsyncRelayCommand SendFriendRequestCommand { get; private set; } = null!;
    public AsyncRelayCommand AcceptFriendRequestCommand { get; private set; } = null!;
    public AsyncRelayCommand DeclineFriendRequestCommand { get; private set; } = null!;
    public AsyncRelayCommand RemoveFriendCommand { get; private set; } = null!;

    private void InitSocialCommands()
    {
        EnsureSocialUiHooks();

        RefreshFriendsCommand = new AsyncRelayCommand(
            RefreshSocialAsync,
            () => CanUseSocialApi);

        SendFriendRequestCommand = new AsyncRelayCommand(
            SendFriendRequestAsync,
            () => CanUseSocialApi && !string.IsNullOrWhiteSpace(FriendQuery));

        AcceptFriendRequestCommand = new AsyncRelayCommand(
            AcceptSelectedIncomingRequestAsync,
            () => CanUseSocialApi && SelectedIncomingRequest is not null && !string.IsNullOrWhiteSpace(SelectedIncomingRequest.Id));

        DeclineFriendRequestCommand = new AsyncRelayCommand(
            DeclineSelectedIncomingRequestAsync,
            () => CanUseSocialApi && SelectedIncomingRequest is not null && !string.IsNullOrWhiteSpace(SelectedIncomingRequest.Id));

        RemoveFriendCommand = new AsyncRelayCommand(
            RemoveSelectedFriendAsync,
            () => CanUseSocialApi && SelectedFriend is not null && !string.IsNullOrWhiteSpace(SelectedFriend.Id));
    }

    private void EnsureSocialUiHooks()
    {
        if (_socialHooksInstalled) return;
        _socialHooksInstalled = true;

        void RaiseCounts()
        {
            Raise(nameof(FriendsCount));
            Raise(nameof(IncomingRequestsCount));
            Raise(nameof(OutgoingRequestsCount));
            Raise(nameof(FriendsSummaryText));
        }

        Friends.CollectionChanged += (_, __) => RaiseCounts();
        IncomingFriendRequests.CollectionChanged += (_, __) => RaiseCounts();
        OutgoingFriendRequests.CollectionChanged += (_, __) => RaiseCounts();
    }

    private void ClearSocialUi()
    {
        PostToUi(() =>
        {
            Friends.Clear();
            IncomingFriendRequests.Clear();
            OutgoingFriendRequests.Clear();

            SelectedFriend = null;
            SelectedIncomingRequest = null;
            SelectedOutgoingRequest = null;

            Raise(nameof(FriendsCount));
            Raise(nameof(IncomingRequestsCount));
            Raise(nameof(OutgoingRequestsCount));
            Raise(nameof(FriendsSummaryText));
        });
    }

    private void ScheduleSocialRefresh()
    {
        if (_isClosing) return;
        if (!TryGetAccessToken(out _)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, _lifetimeCts.Token).ConfigureAwait(false);
            }
            catch { }

            if (_isClosing) return;
            await RefreshSocialAsync().ConfigureAwait(false);
        });
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

    private static FriendEntry ToFriendEntry(FriendDto dto)
        => new()
        {
            Id = (dto.Id ?? "").Trim(),
            Name = string.IsNullOrWhiteSpace(dto.Name) ? "Без имени" : dto.Name.Trim(),
            AvatarUrl = NormalizePublicUrl(dto.Image),
            PublicId = string.IsNullOrWhiteSpace(dto.PublicId) ? null : dto.PublicId.Trim(),
            Status = string.IsNullOrWhiteSpace(dto.Status) ? null : dto.Status.Trim()
        };

    private async Task RefreshSocialAsync()
    {
        if (_isClosing) return;
        if (!TryGetAccessToken(out var token)) return;

        if (Interlocked.Exchange(ref _socialRefreshGuard, 1) == 1)
            return;

        try
        {
            var friendsResp = await _site.GetFriendsAsync(token, _lifetimeCts.Token).ConfigureAwait(false);
            var reqResp = await _site.GetFriendRequestsAsync(token, _lifetimeCts.Token).ConfigureAwait(false);

            PostToUi(() =>
            {
                Friends.Clear();
                if (friendsResp is not null && friendsResp.Ok && friendsResp.Friends is not null)
                {
                    foreach (var f in friendsResp.Friends.Select(ToFriendEntry))
                        if (!string.IsNullOrWhiteSpace(f.Id))
                            Friends.Add(f);
                }

                IncomingFriendRequests.Clear();
                OutgoingFriendRequests.Clear();

                if (reqResp is not null && reqResp.Ok)
                {
                    if (reqResp.Incoming is not null)
                    {
                        foreach (var r in reqResp.Incoming.Select(ToFriendEntry))
                            if (!string.IsNullOrWhiteSpace(r.Id))
                                IncomingFriendRequests.Add(r);
                    }

                    if (reqResp.Outgoing is not null)
                    {
                        foreach (var r in reqResp.Outgoing.Select(ToFriendEntry))
                            if (!string.IsNullOrWhiteSpace(r.Id))
                                OutgoingFriendRequests.Add(r);
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
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
            Interlocked.Exchange(ref _socialRefreshGuard, 0);
            RefreshCanStates();
        }
    }

    private async Task SendFriendRequestAsync()
    {
        if (_isClosing) return;
        if (!TryGetAccessToken(out var token)) return;

        var q = (FriendQuery ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
            return;

        try
        {
            var resp = await _site.SendFriendRequestAsync(token, q, _lifetimeCts.Token).ConfigureAwait(false);

            if (resp is not null && resp.Ok)
            {
                AppendLog("Друзья: заявка отправлена.");
                StatusText = "Заявка отправлена.";
                FriendQuery = "";
                await RefreshSocialAsync().ConfigureAwait(false);
            }
            else
            {
                var err = resp?.Error ?? "Не удалось отправить заявку.";
                StatusText = err;
                AppendLog("Друзья: " + err);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = "Ошибка отправки заявки.";
            AppendLog("Друзья: ошибка send request: " + ex.Message);
        }
        finally
        {
            RefreshCanStates();
        }
    }

    private async Task AcceptSelectedIncomingRequestAsync()
    {
        if (_isClosing) return;
        if (!TryGetAccessToken(out var token)) return;

        var id = (SelectedIncomingRequest?.Id ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id))
            return;

        try
        {
            var resp = await _site.AcceptFriendRequestAsync(token, id, _lifetimeCts.Token).ConfigureAwait(false);

            if (resp is not null && resp.Ok)
            {
                AppendLog("Друзья: заявка принята.");
                StatusText = "Заявка принята.";
                await RefreshSocialAsync().ConfigureAwait(false);
            }
            else
            {
                var err = resp?.Error ?? "Не удалось принять заявку.";
                StatusText = err;
                AppendLog("Друзья: " + err);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = "Ошибка принятия заявки.";
            AppendLog("Друзья: ошибка accept: " + ex.Message);
        }
        finally
        {
            RefreshCanStates();
        }
    }

    private async Task DeclineSelectedIncomingRequestAsync()
    {
        if (_isClosing) return;
        if (!TryGetAccessToken(out var token)) return;

        var id = (SelectedIncomingRequest?.Id ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id))
            return;

        try
        {
            var resp = await _site.DeclineFriendRequestAsync(token, id, _lifetimeCts.Token).ConfigureAwait(false);

            if (resp is not null && resp.Ok)
            {
                AppendLog("Друзья: заявка отклонена.");
                StatusText = "Заявка отклонена.";
                await RefreshSocialAsync().ConfigureAwait(false);
            }
            else
            {
                var err = resp?.Error ?? "Не удалось отклонить заявку.";
                StatusText = err;
                AppendLog("Друзья: " + err);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = "Ошибка отклонения заявки.";
            AppendLog("Друзья: ошибка decline: " + ex.Message);
        }
        finally
        {
            RefreshCanStates();
        }
    }

    private async Task RemoveSelectedFriendAsync()
    {
        if (_isClosing) return;
        if (!TryGetAccessToken(out var token)) return;

        var id = (SelectedFriend?.Id ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id))
            return;

        try
        {
            var resp = await _site.RemoveFriendAsync(token, id, _lifetimeCts.Token).ConfigureAwait(false);

            if (resp is not null && resp.Ok)
            {
                AppendLog("Друзья: удалён.");
                StatusText = "Друг удалён.";
                await RefreshSocialAsync().ConfigureAwait(false);
            }
            else
            {
                var err = resp?.Error ?? "Не удалось удалить друга.";
                StatusText = err;
                AppendLog("Друзья: " + err);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = "Ошибка удаления друга.";
            AppendLog("Друзья: ошибка remove: " + ex.Message);
        }
        finally
        {
            RefreshCanStates();
        }
    }

    // =========================
    // Token resolving (no hard dependency on TokenStore API)
    // =========================

    private bool TryGetAccessToken(out string token)
    {
        token = "";

        try
        {
            // 1) Частый кейс: в другом partial есть поле "_tokens" с HasAccessToken/SafeAccessToken/AccessToken
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
            // 2) Fallback: пробуем достать токен из _tokenStore (как бы он ни был реализован)
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

        // прямой string
        if (obj is string s)
        {
            s = s.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(s)) { token = s; return true; }
            return false;
        }

        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = obj.GetType();

        // 1) SafeAccessToken / HasAccessToken
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

        // 2) AccessToken
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

        // 3) вложенные контейнеры: Current / Value / Token / Tokens
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
