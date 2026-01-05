namespace LegendBorn.Models;

public sealed class AuthTokens
{
    public string AccessToken { get; set; } = "";
    public long ExpiresAtUnix { get; set; }
}