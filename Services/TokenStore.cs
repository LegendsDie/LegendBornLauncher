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
            // Если токенов нет или AccessToken пустой — чистим, чтобы не ломать автологин.
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
                    // backup помогает, если что-то пойдет не так на файловой системе
                    var bak = _filePath + ".bak";
                    try
                    {
                        File.Replace(tmp, _filePath, bak, ignoreMetadataErrors: true);
                    }
                    catch
                    {
                        // если Replace не поддерживается (редко) — fallback на Move
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
                // не валим запуск
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
                // читаем байты
                var protectedBytes = File.ReadAllBytes(_filePath);

                // расшифровка DPAPI
                var data = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);

                var raw = Encoding.UTF8.GetString(data);

                // 1) основной формат: JSON AuthTokens
                var tokens = TryDeserializeTokens(raw);
                if (tokens is not null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
                    return tokens;

                // 2) fallback: если вдруг когда-то сохраняли просто строковый токен
                var maybeToken = raw?.Trim();
                if (!string.IsNullOrWhiteSpace(maybeToken) && !maybeToken.StartsWith("{"))
                    return new AuthTokens { AccessToken = maybeToken };

                return null;
            }
            catch
            {
                // файл битый — удаляем, чтобы не зацикливать автологин
                TryDelete(_filePath);
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
        // backup можно оставлять, но обычно лучше чистить только tmp
        // TryDelete(_filePath + ".bak");
    }

    private static AuthTokens? TryDeserializeTokens(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            // Если это JSON — пробуем распарсить
            if (raw.TrimStart().StartsWith("{"))
                return JsonSerializer.Deserialize<AuthTokens>(raw, JsonOpts);
        }
        catch
        {
            // ignore
        }

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
