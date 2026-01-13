using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LegendBorn.Models;

namespace LegendBorn.Services;

public sealed class TokenStore
{
    // ВАЖНО: после релиза не менять, иначе старые токены не расшифруются
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LegendBornLauncher.v1");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly string _filePath;

    public TokenStore(string filePath) => _filePath = filePath;

    public void Save(AuthTokens tokens)
    {
        try
        {
            if (tokens is null) return;

            var json = JsonSerializer.Serialize(tokens, JsonOpts);
            var data = Encoding.UTF8.GetBytes(json);

            var protectedBytes = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var tmp = _filePath + ".tmp";
            File.WriteAllBytes(tmp, protectedBytes);

            if (File.Exists(_filePath))
                File.Replace(tmp, _filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, _filePath);
        }
        catch
        {
            // не валим запуск
            try
            {
                var tmp = _filePath + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch { }
        }
    }

    public AuthTokens? Load()
    {
        if (!File.Exists(_filePath))
            return null;

        try
        {
            var protectedBytes = File.ReadAllBytes(_filePath);
            var data = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);

            var json = Encoding.UTF8.GetString(data);
            var tokens = JsonSerializer.Deserialize<AuthTokens>(json, JsonOpts);

            if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
                return null;

            return tokens;
        }
        catch
        {
            // файл битый — удаляем, чтобы не зацикливать автологин
            try { File.Delete(_filePath); } catch { }
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);

            var tmp = _filePath + ".tmp";
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
        catch { }
    }
}
