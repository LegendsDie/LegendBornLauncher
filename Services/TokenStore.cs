using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LegendBorn.Models;

namespace LegendBorn.Services;

public sealed class TokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LegendBornLauncher.v1");
    private readonly string _filePath;

    public TokenStore(string filePath) => _filePath = filePath;

    public void Save(AuthTokens tokens)
    {
        var json = JsonSerializer.Serialize(tokens);
        var data = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(_filePath, protectedBytes);
    }

    public AuthTokens? Load()
    {
        if (!File.Exists(_filePath)) return null;

        try
        {
            var protectedBytes = File.ReadAllBytes(_filePath);
            var data = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AuthTokens>(Encoding.UTF8.GetString(data));
        }
        catch
        {
            return null;
        }
    }

    public void Clear()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }
}