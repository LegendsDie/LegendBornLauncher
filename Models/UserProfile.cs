namespace LegendBorn.Models;

public sealed class UserProfile
{
    // cuid
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

    // приходит строкой/JSON из БД на сайте (у тебя это raw JSON-string)
    public string? FeaturedAchievements { get; set; }

    // баланс резонита (рассчитанный баланс кошелька)
    public long Rezonite { get; set; }

    public bool CanPlay { get; set; } = true;
    public string? Reason { get; set; }

    // удобно для UI: гарантированно отдаёт имя
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(UserName) ? UserName : "Unknown";

    // удобно для MC: если MinecraftName пустой — берём DisplayName
    public string EffectiveMinecraftName =>
        !string.IsNullOrWhiteSpace(MinecraftName) ? MinecraftName! : DisplayName;
}