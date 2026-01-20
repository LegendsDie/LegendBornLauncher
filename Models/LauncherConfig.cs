using System;

namespace LegendBorn.Models;

/// <summary>
/// Конфигурация лаунчера (launcher.config.json).
/// ВАЖНО: версия схемы = ConfigSchemaVersion (не версия приложения).
/// </summary>
public sealed class LauncherConfig
{
    // ===== Schema =====

    /// <summary>
    /// Версия схемы launcher.config.json (НЕ версия лаунчера).
    /// Меняется только при реальном изменении структуры/миграциях.
    /// </summary>
    public string ConfigSchemaVersion { get; set; } = CurrentSchemaVersion;

    public const string CurrentSchemaVersion = "0.2.6";

    // ===== UI/UX =====

    /// <summary>Последний выбранный сервер (Id из servers.json).</summary>
    public string? LastServerId { get; set; }

    /// <summary>Запускать автологин при старте (если есть сохранённые токены).</summary>
    public bool AutoLogin { get; set; } = true;

    /// <summary>Последний введённый ник игрока (опционально).</summary>
    public string? LastUsername { get; set; }

    /// <summary>Последняя выбранная вкладка/страница меню (0..N).</summary>
    public int LastMenuIndex { get; set; } = 0;

    // ===== Minecraft =====

    /// <summary>
    /// Папка игры. Если пусто/не задано — используем LauncherPaths.DefaultGameDir.
    /// (Рекомендуется LocalAppData)
    /// </summary>
    public string? GameRootPath { get; set; }

    /// <summary>Память в МБ.</summary>
    public int RamMb { get; set; } = 4096;

    /// <summary>
    /// Явный путь к javaw/java (опционально). Если пусто — авто-поиск/логика MinecraftService.
    /// </summary>
    public string? JavaPath { get; set; }

    // ===== Connection =====

    /// <summary>Последний IP/адрес сервера (может быть ручным override).</summary>
    public string? LastServerIp { get; set; }

    /// <summary>
    /// Автоподключение к серверу при запуске игры.
    /// Если false — игра запускается без auto-connect.
    /// </summary>
    public bool AutoConnect { get; set; } = true;

    // ===== Misc / Diagnostics =====

    /// <summary>Последний успешный вход (UTC).</summary>
    public DateTimeOffset? LastSuccessfulLoginUtc { get; set; }

    /// <summary>Время последнего запуска лаунчера (UTC) — полезно для диагностики.</summary>
    public DateTimeOffset? LastLauncherStartUtc { get; set; }

    /// <summary>Время последней успешной проверки обновлений лаунчера (UTC).</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>Последняя версия лаунчера, которую видели/запускали (информативно).</summary>
    public string? LastLauncherVersion { get; set; }

    // ===== Helpers =====

    /// <summary>
    /// Валидация/нормализация значений (вызывать после Load).
    /// </summary>
    public void Normalize()
    {
        // schema
        ConfigSchemaVersion = NormalizeRequired(ConfigSchemaVersion, "0.0.0");

        // strings
        LastServerId = NormalizeOptional(LastServerId);
        LastServerIp = NormalizeOptional(LastServerIp);
        GameRootPath = NormalizeOptional(GameRootPath);
        JavaPath = NormalizeOptional(JavaPath);
        LastUsername = NormalizeOptional(LastUsername);
        LastLauncherVersion = NormalizeOptional(LastLauncherVersion);

        // ints / bounds
        RamMb = Clamp(RamMb, min: 1024, max: 65536);
        LastMenuIndex = Clamp(LastMenuIndex, min: 0, max: 50);

        // date sanity (защита от битых дат)
        LastSuccessfulLoginUtc = NormalizeUtc(LastSuccessfulLoginUtc);
        LastLauncherStartUtc = NormalizeUtc(LastLauncherStartUtc);
        LastUpdateCheckUtc = NormalizeUtc(LastUpdateCheckUtc);
    }

    /// <summary>
    /// Удобный признак: есть ли ручной override IP.
    /// (Если LastServerIp пустой — считаем, что override нет.)
    /// </summary>
    public bool HasServerIpOverride => !string.IsNullOrWhiteSpace(LastServerIp);

    /// <summary>
    /// Быстро сбросить “пользовательский override” IP (чтобы опять брался адрес из сервера).
    /// </summary>
    public void ClearServerIpOverride() => LastServerIp = null;

    private static string NormalizeRequired(string? value, string fallback)
    {
        var v = (value ?? "").Trim();
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    private static string? NormalizeOptional(string? value)
    {
        var v = (value ?? "").Trim();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static int Clamp(int v, int min, int max)
    {
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }

    private static DateTimeOffset? NormalizeUtc(DateTimeOffset? v)
    {
        if (v is null) return null;

        try
        {
            // приводим к UTC и фильтруем “совсем бред”
            var utc = v.Value.ToUniversalTime();
            if (utc.Year < 2000) return null;
            if (utc > DateTimeOffset.UtcNow.AddDays(2)) return null;
            return utc;
        }
        catch
        {
            return null;
        }
    }
}
