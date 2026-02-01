// File: Models/AuthTokens.cs
using System;
using System.Text.Json.Serialization;

namespace LegendBorn.Models;

public sealed class AuthTokens
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "";

    // Может прийти как seconds или milliseconds (на всякий случай)
    [JsonPropertyName("expiresAtUnix")]
    public long ExpiresAtUnix { get; set; }

    [JsonIgnore]
    public string SafeAccessToken => (AccessToken ?? "").Trim().Trim('"');

    [JsonIgnore]
    public bool HasAccessToken => !string.IsNullOrWhiteSpace(SafeAccessToken);

    /// <summary>
    /// ExpiresAtUnix, приведённый к unix-seconds (если прилетели milliseconds — конвертируем).
    /// </summary>
    [JsonIgnore]
    public long ExpiresAtUnixSeconds => NormalizeUnixSeconds(ExpiresAtUnix);

    [JsonIgnore]
    public DateTimeOffset? ExpiresAtUtc
    {
        get
        {
            var sec = ExpiresAtUnixSeconds;
            if (sec <= 0) return null;

            try { return DateTimeOffset.FromUnixTimeSeconds(sec); }
            catch { return null; }
        }
    }

    /// <summary>
    /// Проверка истечения с запасом (по умолчанию 30 сек).
    /// Если ExpiresAtUnix <= 0 — считаем "не истекает" (совместимость).
    /// </summary>
    public bool IsExpired(int skewSeconds = 30)
    {
        var sec = ExpiresAtUnixSeconds;
        if (sec <= 0) return false;

        try
        {
            var exp = DateTimeOffset.FromUnixTimeSeconds(sec)
                .AddSeconds(-Math.Max(0, skewSeconds));

            return DateTimeOffset.UtcNow >= exp;
        }
        catch
        {
            return false;
        }
    }

    private static long NormalizeUnixSeconds(long unix)
    {
        if (unix <= 0) return 0;

        // "миллисекунды" обычно 13 цифр (>= 10_000_000_000)
        // seconds в 2026 ~ 1_7xx_... (10 цифр максимум)
        if (unix >= 10_000_000_000L)
            return unix / 1000;

        return unix;
    }
}