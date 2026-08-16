using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Services;

/// <summary>
/// Final fail-closed pack reconciliation performed after MinecraftService has synchronized the
/// authoritative manifest. It derives the exact managed path set from the pack_state.json written
/// by that same sync run, so cleanup cannot race a second manifest request or use a different CDN
/// revision. A managed .pending file means the new revision is not active yet and launch is blocked.
/// NeoForge's technical config is sanitized separately so stale dependencyOverrides cannot survive
/// merely because normal config/ files are intentionally user-owned.
/// </summary>
public static class ManagedPackStateVerifier
{
    private static readonly string[] ManagedRoots = { "mods/", "kubejs/", "scripts/" };
    private const long MaxPackStateBytes = 16L * 1024 * 1024;

    public static async Task<ManagedPackCleanupService.CleanupResult> ReconcileAsync(
        string gameDir,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
            throw new ArgumentException("gameDir is empty", nameof(gameDir));

        ct.ThrowIfCancellationRequested();

        var wanted = ReadManagedPathsFromPackState(gameDir);
        var result = await ManagedPackCleanupService.ReconcileLocalManagedRootsAsync(
                gameDir,
                wanted,
                ct)
            .ConfigureAwait(false);

        // config/ stays user-owned, but config/fml.toml is NeoForge loader state. Remove only
        // dependencyOverrides whose target mod no longer exists; all unrelated settings survive.
        await FmlConfigHygieneService.SanitizeAsync(gameDir, log, ct).ConfigureAwait(false);

        var pending = FindManagedPendingFiles(gameDir).Take(8).ToArray();
        if (pending.Length > 0)
        {
            throw new IOException(
                "Новая версия сборки скачана, но часть файлов ещё занята другим процессом: " +
                string.Join(", ", pending.Take(4)) +
                ". Закрой Minecraft/Java и повтори проверку — старая и новая версии не будут запущены вместе.");
        }

        log?.Invoke(result.RemovedFiles > 0
            ? $"Сборка: финальная сверка убрала {result.RemovedFiles} устаревших файлов из активных папок."
            : "Сборка: финальная сверка пройдена, активные папки точно соответствуют текущей версии.");

        return result;
    }

    private static HashSet<string> ReadManagedPathsFromPackState(string gameDir)
    {
        var statePath = Path.Combine(gameDir, "launcher", "pack_state.json");
        if (!File.Exists(statePath))
            throw new InvalidDataException(
                "После синхронизации не найден pack_state.json. Запуск без финальной сверки сборки запрещён.");

        var info = new FileInfo(statePath);
        if (info.Length <= 0 || info.Length > MaxPackStateBytes)
            throw new InvalidDataException("pack_state.json имеет некорректный размер.");

        using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("files", out var files) ||
            files.ValueKind != JsonValueKind.Object)
        {
            // System.Text.Json normally preserves C# property casing. Accept the existing PackState
            // serializer shape as well as a lower-case future shape without weakening validation.
            if (!document.RootElement.TryGetProperty("Files", out files) ||
                files.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("pack_state.json не содержит списка файлов текущей сборки.");
            }
        }

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in files.EnumerateObject())
        {
            var rel = NormalizeRel(property.Name);
            if (IsSafeRelativeFile(rel) && IsManaged(rel))
                wanted.Add(rel);
        }

        return wanted;
    }

    private static IEnumerable<string> FindManagedPendingFiles(string gameDir)
    {
        foreach (var root in ManagedRoots)
        {
            var rootPath = Path.Combine(gameDir, root.TrimEnd('/'));
            if (!Directory.Exists(rootPath))
                continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(rootPath, "*.pending", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(rootPath, "*.pending.sha256", SearchOption.AllDirectories));
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
                yield return NormalizeRel(Path.GetRelativePath(gameDir, file));
        }
    }

    private static bool IsManaged(string rel)
        => ManagedRoots.Any(root => rel.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeRelativeFile(string rel)
    {
        if (rel.Length == 0 || rel.EndsWith('/')) return false;
        if (rel.StartsWith('/') || Path.IsPathRooted(rel) || rel.Contains(':')) return false;
        return !rel.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static part => part is "." or "..");
    }

    private static string NormalizeRel(string? value)
        => (value ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
}
