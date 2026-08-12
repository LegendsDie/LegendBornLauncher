using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LegendBorn.Models;

namespace LegendBorn.Services;

/// <summary>
/// Persists launcher authentication tokens for the current Windows user.
/// Tokens are never intentionally written to disk in plaintext.
/// </summary>
public sealed class TokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LegendBornLauncher.v1");
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private const int MaxTokenFileBytes = 64 * 1024;

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
            SaveInternal(tokens);
        }
    }

    private void SaveInternal(AuthTokens? tokens)
    {
        if (tokens is null || !tokens.HasAccessToken)
        {
            ClearInternal();
            return;
        }

        var tmp = _filePath + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(tokens, JsonOpts);
            var data = Encoding.UTF8.GetBytes(json);

            // Security invariant: persistence is allowed only when DPAPI protection succeeds.
            // If DPAPI is unavailable, keep the token in memory for this process, but do not
            // downgrade to an unencrypted file on disk.
            var payload = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

            if (payload.Length <= 0 || payload.Length > MaxTokenFileBytes)
                throw new InvalidOperationException("Protected token payload has an invalid size.");

            EnsureParentDir(_filePath);
            TryDeleteQuiet(tmp);

            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(payload, 0, payload.Length);
                fs.Flush(flushToDisk: true);
            }

            ReplaceOrMoveAtomic(tmp, _filePath);
            TryDeleteQuiet(tmp);
        }
        catch
        {
            // Never leave a plaintext or partial token file behind.
            TryDeleteQuiet(tmp);
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
                var info = new FileInfo(_filePath);
                if (info.Length <= 0 || info.Length > MaxTokenFileBytes)
                {
                    TryBackupBroken();
                    ClearInternal();
                    return null;
                }

                var payload = File.ReadAllBytes(_filePath);

                try
                {
                    var data = ProtectedData.Unprotect(payload, Entropy, DataProtectionScope.CurrentUser);
                    return ParseTokenPayload(data);
                }
                catch (CryptographicException)
                {
                    // Compatibility migration for old launcher builds which could write plaintext
                    // when DPAPI failed. Read it only if it is strict UTF-8 and structurally valid,
                    // then immediately remove the plaintext copy and try to re-save securely.
                    var legacy = TryParseLegacyPlaintext(payload);
                    if (legacy is not null && legacy.HasAccessToken)
                    {
                        ClearInternal();
                        SaveInternal(legacy);
                        return legacy;
                    }

                    TryBackupBroken();
                    ClearInternal();
                    return null;
                }
            }
            catch
            {
                TryBackupBroken();
                ClearInternal();
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

    private static AuthTokens? ParseTokenPayload(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxTokenFileBytes)
            return null;

        var raw = Encoding.UTF8.GetString(data);
        return ParseTokenText(raw, allowLegacySingleToken: false);
    }

    private static AuthTokens? TryParseLegacyPlaintext(byte[] payload)
    {
        try
        {
            if (payload.Length == 0 || payload.Length > MaxTokenFileBytes)
                return null;

            var raw = StrictUtf8.GetString(payload);
            return ParseTokenText(raw, allowLegacySingleToken: true);
        }
        catch
        {
            return null;
        }
    }

    private static AuthTokens? ParseTokenText(string? raw, bool allowLegacySingleToken)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();

        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                var tokens = JsonSerializer.Deserialize<AuthTokens>(trimmed, JsonOpts);
                return tokens is not null && tokens.HasAccessToken ? tokens : null;
            }
            catch
            {
                return null;
            }
        }

        if (!allowLegacySingleToken)
            return null;

        // Very old format: a single token line. Reject control characters and multiline data
        // so arbitrary/corrupt binary is never interpreted as a credential.
        if (trimmed.Length is < 16 or > 16_384)
            return null;

        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
                return null;
        }

        return new AuthTokens { AccessToken = trimmed };
    }

    private void ClearInternal()
    {
        TryDeleteQuiet(_filePath);
        TryDeleteQuiet(_filePath + ".tmp");
        TryDeleteQuiet(_filePath + ".bak");
    }

    private void TryBackupBroken()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var bak = _filePath + ".broken." + ts + ".bak";

            EnsureParentDir(_filePath);
            File.Copy(_filePath, bak, overwrite: true);
        }
        catch
        {
        }
    }

    private static void EnsureParentDir(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
    }

    private static void ReplaceOrMoveAtomic(string sourceTmp, string destPath)
    {
        if (OperatingSystem.IsWindows() && File.Exists(destPath))
        {
            var backup = destPath + ".bak";
            TryDeleteQuiet(backup);

            try
            {
                File.Replace(sourceTmp, destPath, backup, ignoreMetadataErrors: true);
                return;
            }
            finally
            {
                TryDeleteQuiet(backup);
            }
        }

        File.Move(sourceTmp, destPath, overwrite: true);
    }

    private static void TryDeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
