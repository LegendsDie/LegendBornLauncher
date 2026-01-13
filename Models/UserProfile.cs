namespace LegendBorn.Models;

public sealed class UserProfile
{
    // cuid / uuid
    public string Id { get; set; } = "";

    // может быть 0/не задан в каких-то средах — safer как nullable
    public int? PublicId { get; set; }

    // "USER" | "ADMIN" | ...
    public string Role { get; set; } = "USER";

    // то, что показываем в лаунчере
    public string UserName { get; set; } = "Unknown";

    // ник для Minecraft (может прийти пустым)
    public string? MinecraftName { get; set; }

    // ссылки с сайта
    public string? AvatarUrl { get; set; }
    public string? BannerImage { get; set; }
    public string? ProfileThemeKey { get; set; }

    // приходит строкой/JSON из БД на сайте (raw JSON-string)
    public string? FeaturedAchievements { get; set; }

    // баланс
    public long Rezonite { get; set; }

    // доступ к игре + причина
    public bool CanPlay { get; set; } = true;
    public string? Reason { get; set; }

    // ===== Release-safe helpers =====

    public string SafeId => (Id ?? "").Trim();

    public string SafeRole
    {
        get
        {
            var r = (Role ?? "").Trim();
            return string.IsNullOrWhiteSpace(r) ? "USER" : r;
        }
    }

    public string SafeUserName
    {
        get
        {
            var n = (UserName ?? "").Trim();
            return string.IsNullOrWhiteSpace(n) ? "Unknown" : n;
        }
    }

    public string? SafeMinecraftName
    {
        get
        {
            var n = (MinecraftName ?? "").Trim();
            return string.IsNullOrWhiteSpace(n) ? null : n;
        }
    }

    public string? SafeAvatarUrl
    {
        get
        {
            var u = (AvatarUrl ?? "").Trim();
            return string.IsNullOrWhiteSpace(u) ? null : u;
        }
    }

    public string? SafeBannerImage
    {
        get
        {
            var u = (BannerImage ?? "").Trim();
            return string.IsNullOrWhiteSpace(u) ? null : u;
        }
    }

    public bool HasAvatar => SafeAvatarUrl is not null;
    public bool HasBanner => SafeBannerImage is not null;

    /// <summary>
    /// Удобно для UI: гарантированно отдаёт имя
    /// </summary>
    public string DisplayName => SafeUserName;

    /// <summary>
    /// Удобно для MC: если MinecraftName пустой — берём DisplayName
    /// </summary>
    public string EffectiveMinecraftName => SafeMinecraftName ?? DisplayName;

    /// <summary>
    /// Полезно для UI: если доступ запрещён — вернуть человекочитаемую причину
    /// </summary>
    public string DenyReason
    {
        get
        {
            if (CanPlay) return "";
            var r = (Reason ?? "").Trim();
            return string.IsNullOrWhiteSpace(r) ? "Доступ к игре ограничен." : r;
        }
    }
}
