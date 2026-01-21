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

                // DPAPI (как основной путь)
                byte[] payload;
                try
                {
                    payload = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
                }
                catch
                {
                    // fallback plaintext (редко, но лучше чем потерять токен)
                    payload = data;
                }

                EnsureParentDir(_filePath);

                var tmp = _filePath + ".tmp";

                // ✅ надёжнее записываем на диск (flush)
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(payload, 0, payload.Length);
                    fs.Flush(flushToDisk: true);
                }

                ReplaceOrMoveAtomic(tmp, _filePath);

                TryDeleteQuiet(tmp);
            }
            catch
            {
                TryDeleteQuiet(_filePath + ".tmp");
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
                var payload = File.ReadAllBytes(_filePath);

                // 1) пробуем DPAPI unprotect
                byte[] data;
                try
                {
                    data = ProtectedData.Unprotect(payload, Entropy, DataProtectionScope.CurrentUser);
                }
                catch
                {
                    // 2) fallback: возможно это plaintext
                    data = payload;
                }

                var raw = Encoding.UTF8.GetString(data);

                // json?
                var tokens = TryDeserializeTokens(raw);
                if (tokens is not null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
                    return tokens;

                // legacy: одиночная строка токена
                var maybeToken = raw?.Trim();
                if (!string.IsNullOrWhiteSpace(maybeToken) && !maybeToken.StartsWith("{"))
                    return new AuthTokens { AccessToken = maybeToken };

                return null;
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

    private void ClearInternal()
    {
        TryDeleteQuiet(_filePath);
        TryDeleteQuiet(_filePath + ".tmp");
        TryDeleteQuiet(_filePath + ".bak");
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

    private void TryBackupBroken()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var bak = _filePath + ".broken." + ts + ".bak";

            EnsureParentDir(_filePath);
            File.Copy(_filePath, bak, overwrite: true);
        }
        catch { }
    }

    private static void EnsureParentDir(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
        }
        catch { }
    }

    private static void ReplaceOrMoveAtomic(string sourceTmp, string destPath)
    {
        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(destPath))
            {
                var backup = destPath + ".bak";
                try
                {
                    TryDeleteQuiet(backup);
                    File.Replace(sourceTmp, destPath, backup, ignoreMetadataErrors: true);
                }
                finally
                {
                    TryDeleteQuiet(backup);
                }
                return;
            }

            File.Move(sourceTmp, destPath, overwrite: true);
        }
        catch
        {
            // fallback
            try
            {
                if (File.Exists(destPath))
                    File.Delete(destPath);

                File.Move(sourceTmp, destPath);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void TryDeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
