using System;

namespace LegendBorn.Models;

public sealed class AuthTokens
{
    public string AccessToken { get; set; } = "";
    public long ExpiresAtUnix { get; set; }

    public string SafeAccessToken => (AccessToken ?? "").Trim();
    public bool HasAccessToken => !string.IsNullOrWhiteSpace(SafeAccessToken);

    public DateTimeOffset? ExpiresAtUtc
    {
        get
        {
            if (ExpiresAtUnix <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix); }
            catch { return null; }
        }
    }

    /// <summary>
    /// Проверка истечения с запасом (по умолчанию 30 сек).
    /// Если ExpiresAtUnix <= 0 — считаем "не истекает" (совместимость).
    /// </summary>
    public bool IsExpired(int skewSeconds = 30)
    {
        if (ExpiresAtUnix <= 0) return false;

        try
        {
            var exp = DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix)
                .AddSeconds(-Math.Max(0, skewSeconds));
            return DateTimeOffset.UtcNow >= exp;
        }
        catch
        {
            return false;
        }
    }
}