using System;
using System.Globalization;
using System.Linq;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    public bool ProfileMinecraftOnline => Profile?.MinecraftStatus?.Online == true;
    public string ProfileMinecraftStateText => ProfileMinecraftOnline ? "В ИГРЕ" : "НЕ В ИГРЕ";
    public string ProfileMinecraftServerId => Clean(Profile?.MinecraftStatus?.ServerId) ?? SelectedServer?.Id ?? "—";

    public string ProfileMinecraftServerName
    {
        get
        {
            var serverId = Clean(Profile?.MinecraftStatus?.ServerId);
            if (serverId is not null)
            {
                var match = Servers.FirstOrDefault(server =>
                    string.Equals(server.Id, serverId, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match.Name;
            }

            return SelectedServer?.Name ?? "LegendCraft";
        }
    }

    public string ProfileMinecraftWorldText => FormatDimension(Profile?.MinecraftStatus?.Dimension);

    public string ProfileMinecraftCoordinatesText
    {
        get
        {
            var status = Profile?.MinecraftStatus;
            if (status?.X is not double x || status.Y is not double y || status.Z is not double z)
                return "—";
            return $"X {Math.Round(x):N0}   Y {Math.Round(y):N0}   Z {Math.Round(z):N0}";
        }
    }

    public string ProfileMinecraftHealthText
    {
        get
        {
            var status = Profile?.MinecraftStatus;
            if (status?.Health is not double health) return "—";
            if (status.MaxHealth is double maxHealth)
                return $"{FormatCompact(health)} / {FormatCompact(maxHealth)} HP";
            return $"{FormatCompact(health)} HP";
        }
    }

    public string ProfileMinecraftFoodText
    {
        get
        {
            var food = Profile?.MinecraftStatus?.Food;
            return food is double value ? $"{FormatCompact(value)} / 20" : "—";
        }
    }

    public string ProfileMinecraftExperienceText
    {
        get
        {
            var status = Profile?.MinecraftStatus;
            if (status?.ExperienceLevel is not double level) return "—";
            var roundedLevel = Math.Max(0, (int)Math.Round(level));
            if (status.ExperienceProgress is not double progress)
                return $"Уровень {roundedLevel:N0}";
            return $"Уровень {roundedLevel:N0} • {Math.Clamp(progress, 0, 1) * 100:0}%";
        }
    }

    public string ProfileMinecraftSessionText
    {
        get
        {
            var startedAt = Profile?.MinecraftStatus?.SessionStartedAt;
            if (!ProfileMinecraftOnline || startedAt is null) return "—";
            var elapsed = DateTimeOffset.UtcNow - startedAt.Value.ToUniversalTime();
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            if (elapsed.TotalHours >= 1)
                return $"{(int)elapsed.TotalHours} ч {elapsed.Minutes} мин";
            return $"{Math.Max(0, elapsed.Minutes)} мин";
        }
    }

    public string ProfileMinecraftUpdatedText
    {
        get
        {
            var seenAt = Profile?.MinecraftStatus?.SeenAt;
            if (seenAt is null) return "Нет данных от сервера";
            var age = DateTimeOffset.UtcNow - seenAt.Value.ToUniversalTime();
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            if (age.TotalSeconds < 20) return "Обновлено только что";
            if (age.TotalMinutes < 1) return $"Обновлено {Math.Max(1, (int)age.TotalSeconds)} сек назад";
            if (age.TotalHours < 1) return $"Обновлено {(int)age.TotalMinutes} мин назад";
            return $"Обновлено {seenAt.Value.ToLocalTime():dd.MM HH:mm}";
        }
    }

    public bool HasProfileMinecraftTelemetry
    {
        get
        {
            var status = Profile?.MinecraftStatus;
            return status is not null &&
                   (Clean(status.Dimension) is not null || status.X.HasValue || status.Health.HasValue ||
                    status.Food.HasValue || status.ExperienceLevel.HasValue);
        }
    }

    private static string FormatDimension(string? raw)
    {
        var value = Clean(raw);
        if (value is null) return "—";
        return value.ToLowerInvariant() switch
        {
            "minecraft:overworld" => "Верхний мир",
            "minecraft:the_nether" => "Незер",
            "minecraft:the_end" => "Энд",
            _ => value
        };
    }

    private static string? Clean(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string FormatCompact(double value)
    {
        var rounded = Math.Round(value, 1);
        return Math.Abs(rounded - Math.Round(rounded)) < 0.001
            ? Math.Round(rounded).ToString("N0", CultureInfo.CurrentCulture)
            : rounded.ToString("N1", CultureInfo.CurrentCulture);
    }

    private void RaiseProfileStatusPresentation()
    {
        Raise(nameof(ProfileMinecraftOnline));
        Raise(nameof(ProfileMinecraftStateText));
        Raise(nameof(ProfileMinecraftServerId));
        Raise(nameof(ProfileMinecraftServerName));
        Raise(nameof(ProfileMinecraftWorldText));
        Raise(nameof(ProfileMinecraftCoordinatesText));
        Raise(nameof(ProfileMinecraftHealthText));
        Raise(nameof(ProfileMinecraftFoodText));
        Raise(nameof(ProfileMinecraftExperienceText));
        Raise(nameof(ProfileMinecraftSessionText));
        Raise(nameof(ProfileMinecraftUpdatedText));
        Raise(nameof(HasProfileMinecraftTelemetry));
    }
}
