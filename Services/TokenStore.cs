// TokenStore.cs
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LegendBorn.Models;

namespace LegendBorn.Services;

public sealed class TokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LegendBornLauncher.v1");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _filePath;
    private readonly object _sync = new();

    public TokenStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("filePath is null/empty", nameof(filePath));

        _filePath = filePath;
    }

    public void Save(AuthTokens? tokens)
    {
        lock (_sync)
        {
            if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
            {
                ClearInternal();
                return;
            }

            try
            {
                var json = JsonSerializer.Serialize(tokens, JsonOpts);
                var data = Encoding.UTF8.GetBytes(json);

                var protectedBytes = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var tmp = _filePath + ".tmp";
                File.WriteAllBytes(tmp, protectedBytes);

                if (File.Exists(_filePath))
                {
                    var bak = _filePath + ".bak";
                    try
                    {
                        File.Replace(tmp, _filePath, bak, ignoreMetadataErrors: true);
                    }
                    catch
                    {
                        File.Delete(_filePath);
                        File.Move(tmp, _filePath);
                    }
                }
                else
                {
                    File.Move(tmp, _filePath);
                }
            }
            catch
            {
                TryDelete(_filePath + ".tmp");
            }
        }
    }

    public AuthTokens? Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_filePath))
                return null;

            try
            {
                var protectedBytes = File.ReadAllBytes(_filePath);
                var data = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                var raw = Encoding.UTF8.GetString(data);

                var tokens = TryDeserializeTokens(raw);
                if (tokens is not null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
                    return tokens;

                var maybeToken = raw?.Trim();
                if (!string.IsNullOrWhiteSpace(maybeToken) && !maybeToken.StartsWith("{"))
                    return new AuthTokens { AccessToken = maybeToken };

                return null;
            }
            catch
            {
                TryDelete(_filePath);
                TryDelete(_filePath + ".tmp");
                return null;
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            ClearInternal();
        }
    }

    private void ClearInternal()
    {
        TryDelete(_filePath);
        TryDelete(_filePath + ".tmp");
    }

    private static AuthTokens? TryDeserializeTokens(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            if (raw.TrimStart().StartsWith("{"))
                return JsonSerializer.Deserialize<AuthTokens>(raw, JsonOpts);
        }
        catch { }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
