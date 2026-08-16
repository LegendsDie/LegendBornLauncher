using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using LegendBorn.Mvvm;
using LegendBorn.Services;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private readonly LauncherMinecraftStatusService _minecraftStatusApi = new();
    private LauncherMinecraftStatusService.StatusResponse? _minecraftStatus;
    private bool _isMinecraftStatusBusy;
    private AsyncRelayCommand? _refreshMinecraftStatusCommand;

    public AsyncRelayCommand RefreshMinecraftStatusCommand => _refreshMinecraftStatusCommand ??= new AsyncRelayCommand(
        RefreshMinecraftStatusAsync,
        () => !_isClosing && IsLoggedIn && HasSiteToken && !_isMinecraftStatusBusy);

    public bool IsMinecraftStatusBusy => _isMinecraftStatusBusy;
    public bool HasMinecraftSnapshot => _minecraftStatus?.Snapshot is not null;
    public string MinecraftStatusServerName => SelectedServer?.Name ?? "LegendCraft";
    public string MinecraftStatusServerBuild => BuildDisplayName;
    public string? MinecraftStatusSkinUrl => NormalizePublicUrl(
        _minecraftStatus?.SelectedSkin?.SkinUrl ??
        _minecraftStatus?.SelectedSkin?.PreviewUrl) ?? DashboardSkinUrl;

    public string MinecraftStatusStateText
    {
        get
        {
            if (_isMinecraftStatusBusy) return "Обновляю состояние персонажа…";
            if (_minecraftStatus is null) return "Статус ещё не загружен.";
            if (!_minecraftStatus.Ok) return "Игровая телеметрия временно недоступна.";
            if (!_minecraftStatus.Linked) return "Minecraft-аккаунт не привязан.";
            return HasMinecraftSnapshot
                ? "Последний снимок состояния с сервера."
                : "Сервер ещё не прислал снимок состояния персонажа.";
        }
    }

    public string MinecraftStatusCapturedText
        => _minecraftStatus?.Snapshot?.CapturedAt is { } captured
            ? $"Обновлено {captured.ToLocalTime():dd.MM.yyyy HH:mm:ss}"
            : "Обновление: —";

    public string MinecraftStatusHealth => FormatCurrentMax(_minecraftStatus?.Snapshot?.Health, 20);
    public string MinecraftStatusFood => FormatCurrentMax(_minecraftStatus?.Snapshot?.Food, 20);
    public string MinecraftStatusLevel => FormatNumber(_minecraftStatus?.Snapshot?.Level);
    public string MinecraftStatusXp => _minecraftStatus?.Snapshot?.Xp is { } xp ? $"{xp:0.#}%" : "—";
    public string MinecraftStatusDimension => FormatDimension(_minecraftStatus?.Snapshot?.Dimension);
    public string MinecraftStatusPosition => FormatPosition(_minecraftStatus?.Snapshot?.Position);
    public string MinecraftStatusPlayTime => FormatDuration(_minecraftStatus?.Snapshot?.PlayTimeSeconds);
    public string MinecraftStatusDeaths => FormatJsonNumber(_minecraftStatus?.Snapshot?.Deaths ?? default);
    public string MinecraftStatusPlayerKills => FormatJsonNumber(_minecraftStatus?.Snapshot?.PlayerKills ?? default);
    public string MinecraftStatusMobKills => FormatJsonNumber(_minecraftStatus?.Snapshot?.MobKills ?? default);
    public string MinecraftStatusTotalKills => FormatJsonNumber(_minecraftStatus?.Snapshot?.TotalKills ?? default);
    public string MinecraftStatusJumps => FormatJsonNumber(_minecraftStatus?.Snapshot?.Jumps ?? default);
    public string MinecraftStatusWalk => FormatDistance(_minecraftStatus?.Snapshot?.WalkMeters);
    public string MinecraftStatusFly => FormatDistance(_minecraftStatus?.Snapshot?.FlyMeters);

    public string MinecraftStatusKd
    {
        get
        {
            var snapshot = _minecraftStatus?.Snapshot;
            if (snapshot is null) return "—";
            if (snapshot.Kd is { } kd) return kd.ToString("0.00", CultureInfo.InvariantCulture);

            var deaths = ReadJsonDouble(snapshot.Deaths);
            var kills = ReadJsonDouble(snapshot.TotalKills);
            return deaths == 0 && kills is > 0 ? "∞" : "—";
        }
    }

    private async Task RefreshMinecraftStatusAsync()
    {
        if (!TryGetAccessToken(out var token)) return;

        SetMinecraftStatusBusy(true);
        try
        {
            var response = await _minecraftStatusApi.GetAsync(token, _lifetimeCts.Token).ConfigureAwait(false);
            PostToUi(() =>
            {
                _minecraftStatus = response;
                RaiseMinecraftStatusPresentation();
            });
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppendLog("Minecraft статус: " + ex.Message);
            PostToUi(() =>
            {
                _minecraftStatus = new LauncherMinecraftStatusService.StatusResponse
                {
                    Ok = false,
                    Error = ex.Message
                };
                RaiseMinecraftStatusPresentation();
            });
        }
        finally
        {
            SetMinecraftStatusBusy(false);
        }
    }

    private void SetMinecraftStatusBusy(bool value)
    {
        PostToUi(() =>
        {
            _isMinecraftStatusBusy = value;
            Raise(nameof(IsMinecraftStatusBusy));
            Raise(nameof(MinecraftStatusStateText));
            _refreshMinecraftStatusCommand?.RaiseCanExecuteChanged();
        });
    }

    private void RaiseMinecraftStatusPresentation()
    {
        Raise(nameof(HasMinecraftSnapshot));
        Raise(nameof(MinecraftStatusStateText));
        Raise(nameof(MinecraftStatusCapturedText));
        Raise(nameof(MinecraftStatusHealth));
        Raise(nameof(MinecraftStatusFood));
        Raise(nameof(MinecraftStatusLevel));
        Raise(nameof(MinecraftStatusXp));
        Raise(nameof(MinecraftStatusDimension));
        Raise(nameof(MinecraftStatusPosition));
        Raise(nameof(MinecraftStatusPlayTime));
        Raise(nameof(MinecraftStatusDeaths));
        Raise(nameof(MinecraftStatusPlayerKills));
        Raise(nameof(MinecraftStatusMobKills));
        Raise(nameof(MinecraftStatusTotalKills));
        Raise(nameof(MinecraftStatusJumps));
        Raise(nameof(MinecraftStatusWalk));
        Raise(nameof(MinecraftStatusFly));
        Raise(nameof(MinecraftStatusKd));
        Raise(nameof(MinecraftStatusSkinUrl));
        Raise(nameof(MinecraftStatusServerName));
        Raise(nameof(MinecraftStatusServerBuild));
    }

    private static string FormatCurrentMax(double? value, double max)
        => value is { } number ? $"{number:0.#} / {max:0.#}" : "—";

    private static string FormatNumber(double? value)
        => value is { } number ? Math.Round(number).ToString(CultureInfo.InvariantCulture) : "—";

    private static string FormatDimension(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0) return "—";
        return value.ToLowerInvariant() switch
        {
            "minecraft:overworld" or "overworld" => "Обычный мир",
            "minecraft:the_nether" or "the_nether" or "nether" => "Незер",
            "minecraft:the_end" or "the_end" or "end" => "Край",
            _ => value
        };
    }

    private static string FormatPosition(LauncherMinecraftStatusService.PositionDto? position)
    {
        if (position?.X is not { } x) return "—";
        var y = position.Y ?? 0;
        var z = position.Z ?? 0;
        return $"X {Math.Round(x):0} · Y {Math.Round(y):0} · Z {Math.Round(z):0}";
    }

    private static string FormatDuration(double? seconds)
    {
        if (seconds is not { } raw || raw < 0) return "—";
        var span = TimeSpan.FromSeconds(raw);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}ч {span.Minutes}м"
            : span.TotalMinutes >= 1
                ? $"{span.Minutes}м {span.Seconds}с"
                : $"{span.Seconds}с";
    }

    private static string FormatDistance(double? meters)
    {
        if (meters is not { } value || value < 0) return "—";
        return value >= 1000
            ? $"{value / 1000.0:0.0} км"
            : $"{value:0} м";
    }

    private static string FormatJsonNumber(JsonElement value)
    {
        var number = ReadJsonDouble(value);
        return number is { } n ? Math.Round(n).ToString("N0", CultureInfo.CurrentCulture) : "—";
    }

    private static double? ReadJsonDouble(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return number;
        return null;
    }
}
