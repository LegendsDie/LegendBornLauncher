using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LegendBorn.Mvvm;
using LegendBorn.Services;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private const int DefaultServerNickMinLength = 3;
    private const int DefaultServerNickMaxLength = 24;

    private static readonly Regex ServerNickPattern = new(
        @"^[\p{L}\p{N}_][\p{L}\p{N}_ .-]*[\p{L}\p{N}_]$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedServerNicks = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin",
        "administrator",
        "console",
        "mod",
        "moderator",
        "owner",
        "server",
        "system"
    };

    private readonly LauncherServerNickService _serverNickApi = new();

    private bool _isServerNickBusy;
    private string _serverNickDraft = string.Empty;
    private string? _configuredServerNick;
    private string? _effectiveServerNick;
    private string? _serverNickMinecraftUsername;
    private int _serverNickMinLength = DefaultServerNickMinLength;
    private int _serverNickMaxLength = DefaultServerNickMaxLength;
    private string _serverNickStatusText = "Игровой ник хранится в аккаунте LegendBorn.";

    private AsyncRelayCommand? _refreshServerNickCommand;
    private AsyncRelayCommand? _saveServerNickCommand;
    private AsyncRelayCommand? _resetServerNickCommand;

    public bool IsServerNickBusy => _isServerNickBusy;

    /// <summary>
    /// Editable server display name. This is intentionally separate from Username:
    /// Username is the technical Minecraft launch/link identity while serverNick is the
    /// authoritative LegendBorn in-game display name and may contain Unicode/spaces.
    /// </summary>
    public string ServerNickDraft
    {
        get => _serverNickDraft;
        set
        {
            if (!Set(ref _serverNickDraft, value ?? string.Empty))
                return;

            Raise(nameof(ServerNickValidationText));
            RaiseServerNickCanExecute();
        }
    }

    public string ServerNickDisplayName
    {
        get
        {
            var effective = Clean(_effectiveServerNick);
            if (effective.Length > 0) return effective;

            effective = Clean(Profile?.Minecraft?.EffectiveServerNick);
            if (effective.Length > 0) return effective;

            effective = Clean(Profile?.ServerNick);
            if (effective.Length > 0) return effective;

            effective = Clean(Profile?.Minecraft?.ServerNick);
            if (effective.Length > 0) return effective;

            var minecraft = Clean(Profile?.Minecraft?.Username ?? Profile?.MinecraftName);
            if (minecraft.Length > 0) return minecraft;

            var local = Clean(Username);
            return local.Length > 0 ? local : "Player";
        }
    }

    public string ServerNickMinecraftAccountText
    {
        get
        {
            var minecraft = Clean(_serverNickMinecraftUsername);
            if (minecraft.Length == 0)
                minecraft = Clean(Profile?.Minecraft?.Username ?? Profile?.MinecraftName);
            if (minecraft.Length == 0)
                minecraft = Clean(Username);

            return minecraft.Length > 0 ? $"Minecraft · {minecraft}" : "Minecraft · не привязан";
        }
    }

    public string ServerNickStatusText
    {
        get => _serverNickStatusText;
        private set => Set(ref _serverNickStatusText, value ?? string.Empty);
    }

    public string ServerNickRulesText
        => $"{_serverNickMinLength}–{_serverNickMaxLength} символов · буквы, цифры, пробел, _, . и -";

    public string ServerNickValidationText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ServerNickDraft))
                return "Введите игровой ник.";

            var normalized = NormalizeServerNick(ServerNickDraft);
            return TryValidateServerNick(normalized, out var error) ? string.Empty : error;
        }
    }

    public bool HasCustomServerNick
    {
        get
        {
            var configured = Clean(_configuredServerNick);
            if (configured.Length > 0) return true;
            return Clean(Profile?.Minecraft?.ServerNick ?? Profile?.ServerNick).Length > 0;
        }
    }

    public AsyncRelayCommand RefreshServerNickCommand => _refreshServerNickCommand ??= new AsyncRelayCommand(
        RefreshServerNickAsync,
        () => !_isClosing && IsLoggedIn && HasSiteToken && !_isServerNickBusy);

    public AsyncRelayCommand SaveServerNickCommand => _saveServerNickCommand ??= new AsyncRelayCommand(
        SaveServerNickAsync,
        CanSaveServerNick);

    public AsyncRelayCommand ResetServerNickCommand => _resetServerNickCommand ??= new AsyncRelayCommand(
        ResetServerNickAsync,
        () => !_isClosing && IsLoggedIn && HasSiteToken && !_isServerNickBusy && HasCustomServerNick);

    private bool CanSaveServerNick()
    {
        if (_isClosing || !IsLoggedIn || !HasSiteToken || _isServerNickBusy)
            return false;

        var normalized = NormalizeServerNick(ServerNickDraft);
        if (!TryValidateServerNick(normalized, out _))
            return false;

        var configured = Clean(_configuredServerNick);
        if (configured.Length == 0)
            configured = Clean(Profile?.Minecraft?.ServerNick ?? Profile?.ServerNick);

        return !string.Equals(configured, normalized, StringComparison.Ordinal);
    }

    private async Task RefreshServerNickAsync()
    {
        if (!TryGetAccessToken(out var token))
            return;

        SetServerNickBusy(true);
        SetServerNickStatus("Загружаю игровой ник…");

        try
        {
            var response = await _serverNickApi.GetAsync(token, _lifetimeCts.Token).ConfigureAwait(false);
            if (!response.Ok)
            {
                SetServerNickStatus(MapServerNickError(response));
                return;
            }

            ApplyServerNickResponse(response, resetDraft: true);
            SetServerNickStatus(response.ServerNick is null
                ? "Используется ник привязанного Minecraft-аккаунта."
                : "Игровой ник синхронизирован с аккаунтом.");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppendLog("Игровой ник: ошибка загрузки: " + ex.Message);
            SetServerNickStatus("Не удалось загрузить игровой ник.");
        }
        finally
        {
            SetServerNickBusy(false);
        }
    }

    private async Task SaveServerNickAsync()
    {
        if (!TryGetAccessToken(out var token))
            return;

        var normalized = NormalizeServerNick(ServerNickDraft);
        if (!TryValidateServerNick(normalized, out var validationError))
        {
            SetServerNickStatus(validationError);
            return;
        }

        SetServerNickBusy(true);
        SetServerNickStatus("Сохраняю игровой ник…");

        try
        {
            var response = await _serverNickApi.PutAsync(token, normalized, _lifetimeCts.Token).ConfigureAwait(false);
            if (!response.Ok)
            {
                SetServerNickStatus(MapServerNickError(response));
                return;
            }

            ApplyServerNickResponse(response, resetDraft: true);

            // Refresh the regular launcher snapshot too, so every launcher screen continues to read
            // the same authoritative account state without inventing a parallel local nickname.
            await RefreshProfileSnapshotAsync(token).ConfigureAwait(false);
            RaiseServerNickPresentation();

            // Hotfix 0.4.5: if this launcher owns a running Minecraft process, immediately issue a
            // fresh one-time join-ticket and atomically rewrite .legendcore/session.json. This avoids
            // LS-AUTH-002 when the player reconnects immediately after changing public serverNick.
            var sessionOutcome = await RefreshRunningLegendCoreSessionAfterServerNickChangeAsync(token)
                .ConfigureAwait(false);
            SetServerNickStatus(ServerNickMutationStatus(sessionOutcome, reset: false));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppendLog("Игровой ник: ошибка сохранения: " + ex.Message);
            SetServerNickStatus("Не удалось сохранить игровой ник.");
        }
        finally
        {
            SetServerNickBusy(false);
        }
    }

    private async Task ResetServerNickAsync()
    {
        if (!TryGetAccessToken(out var token))
            return;

        SetServerNickBusy(true);
        SetServerNickStatus("Возвращаю ник Minecraft-аккаунта…");

        try
        {
            var response = await _serverNickApi.PutAsync(token, serverNick: null, _lifetimeCts.Token).ConfigureAwait(false);
            if (!response.Ok)
            {
                SetServerNickStatus(MapServerNickError(response));
                return;
            }

            ApplyServerNickResponse(response, resetDraft: true);

            await RefreshProfileSnapshotAsync(token).ConfigureAwait(false);
            RaiseServerNickPresentation();

            var sessionOutcome = await RefreshRunningLegendCoreSessionAfterServerNickChangeAsync(token)
                .ConfigureAwait(false);
            SetServerNickStatus(ServerNickMutationStatus(sessionOutcome, reset: true));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppendLog("Игровой ник: ошибка сброса: " + ex.Message);
            SetServerNickStatus("Не удалось сбросить игровой ник.");
        }
        finally
        {
            SetServerNickBusy(false);
        }
    }

    private void ApplyServerNickResponse(
        LauncherServerNickService.ServerNickResponse response,
        bool resetDraft)
    {
        PostToUi(() =>
        {
            _configuredServerNick = NullIfBlank(response.ServerNick);
            _effectiveServerNick = NullIfBlank(response.EffectiveServerNick);

            var minecraftUsername = NullIfBlank(response.MinecraftUsername);
            if (minecraftUsername is not null)
                _serverNickMinecraftUsername = minecraftUsername;

            if (response.Rules is { } rules)
            {
                if (rules.MinLength > 0)
                    _serverNickMinLength = rules.MinLength;
                if (rules.MaxLength >= _serverNickMinLength)
                    _serverNickMaxLength = rules.MaxLength;
            }

            if (Profile is { } profile)
            {
                profile.ServerNick = _configuredServerNick;
                if (profile.Minecraft is { } minecraft)
                {
                    minecraft.ServerNick = _configuredServerNick;
                    minecraft.EffectiveServerNick = _effectiveServerNick;
                }
            }

            if (resetDraft)
            {
                _serverNickDraft = _configuredServerNick
                                   ?? _effectiveServerNick
                                   ?? _serverNickMinecraftUsername
                                   ?? Clean(Profile?.Minecraft?.Username ?? Profile?.MinecraftName)
                                   ?? Clean(Username);
            }

            RaiseServerNickPresentation();
        });
    }

    private void SetServerNickBusy(bool value)
    {
        PostToUi(() =>
        {
            _isServerNickBusy = value;
            Raise(nameof(IsServerNickBusy));
            RaiseServerNickCanExecute();
        });
    }

    private void SetServerNickStatus(string value)
        => PostToUi(() => ServerNickStatusText = value);

    private void RaiseServerNickPresentation()
    {
        Raise(nameof(ServerNickDraft));
        Raise(nameof(ServerNickDisplayName));
        Raise(nameof(ServerNickMinecraftAccountText));
        Raise(nameof(ServerNickRulesText));
        Raise(nameof(ServerNickValidationText));
        Raise(nameof(HasCustomServerNick));
        Raise(nameof(ServerNickStatusText));
        RaiseServerNickCanExecute();
    }

    private void RaiseServerNickCanExecute()
    {
        _refreshServerNickCommand?.RaiseCanExecuteChanged();
        _saveServerNickCommand?.RaiseCanExecuteChanged();
        _resetServerNickCommand?.RaiseCanExecuteChanged();
    }

    private bool TryValidateServerNick(string normalized, out string error)
    {
        var length = CountRunes(normalized);
        if (length < _serverNickMinLength)
        {
            error = $"Минимум {_serverNickMinLength} символа.";
            return false;
        }

        if (length > _serverNickMaxLength)
        {
            error = $"Максимум {_serverNickMaxLength} символов.";
            return false;
        }

        if (!ServerNickPattern.IsMatch(normalized))
        {
            error = "Разрешены буквы, цифры, пробел, _, . и -. Ник должен начинаться и заканчиваться буквой, цифрой или _.";
            return false;
        }

        if (ReservedServerNicks.Contains(normalized))
        {
            error = "Этот ник зарезервирован системой.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string NormalizeServerNick(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.Length == 0) return string.Empty;
        return Regex.Replace(normalized, @"\s+", " ", RegexOptions.CultureInvariant);
    }

    private static int CountRunes(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
            count++;
        return count;
    }

    private static string MapServerNickError(LauncherServerNickService.ServerNickResponse response)
    {
        var code = Clean(response.Code).ToLowerInvariant();
        if (code.Length > 0)
        {
            return code switch
            {
                "too_short" => "Ник слишком короткий.",
                "too_long" => "Ник слишком длинный.",
                "invalid_chars" => "В нике есть недопустимые символы.",
                "reserved" => "Этот ник зарезервирован системой.",
                "already_used" => "Этот игровой ник уже занят.",
                _ => Clean(response.Error ?? response.Message) is { Length: > 0 } message
                    ? message
                    : "Не удалось изменить игровой ник."
            };
        }

        var raw = Clean(response.Error ?? response.Message);
        if (raw.Contains("not linked", StringComparison.OrdinalIgnoreCase))
            return "Сначала привяжите Minecraft-аккаунт.";
        if (raw.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("EMPTY_TOKEN", StringComparison.OrdinalIgnoreCase))
            return "Сессия истекла. Войдите в аккаунт снова.";

        return raw.Length > 0 ? raw : "Не удалось изменить игровой ник.";
    }

    private static string Clean(string? value)
        => (value ?? string.Empty).Trim();

    private static string? NullIfBlank(string? value)
    {
        var clean = Clean(value);
        return clean.Length == 0 ? null : clean;
    }
}
