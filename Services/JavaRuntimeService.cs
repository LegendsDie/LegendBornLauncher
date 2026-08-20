using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Models;

namespace LegendBorn.Services;

public sealed class JavaRuntimeService
{
    public const int RequiredMajor = 21;
    public const string ModeAutomatic = "automatic";
    public const string ModeSystem = "system";
    public const string ModeCustom = "custom";
    public const string SystemSentinel = "@system";

    private const long MaxArchiveBytes = 300L * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static readonly Regex VersionQuoted = new("version \\\"(?<version>[^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HttpClient Http = CreateHttp();

    private static string JavaRuntimeDir => Path.Combine(LauncherPaths.LocalDir, "runtime", "java");
    private static string ManagedJava21Dir => Path.Combine(JavaRuntimeDir, "temurin-21");

    public sealed record RuntimeInfo(
        string JavaExe,
        int Major,
        bool Is64Bit,
        string Version,
        string Vendor,
        string Source,
        bool Managed)
    {
        public string DisplayName
        {
            get
            {
                var vendor = string.IsNullOrWhiteSpace(Vendor) ? "Java" : Vendor.Trim();
                var version = string.IsNullOrWhiteSpace(Version) ? Major.ToString() : Version.Trim();
                return $"{vendor} {version} · 64-bit";
            }
        }
    }

    public static string ModeFromConfig(string? javaPath)
    {
        var value = (javaPath ?? string.Empty).Trim();
        if (value.Length == 0 || value.Equals(ModeAutomatic, StringComparison.OrdinalIgnoreCase))
            return ModeAutomatic;
        if (value.Equals(SystemSentinel, StringComparison.OrdinalIgnoreCase) || value.Equals(ModeSystem, StringComparison.OrdinalIgnoreCase))
            return ModeSystem;
        return ModeCustom;
    }

    public async Task<RuntimeInfo> ResolveAsync(
        LauncherConfig config,
        string gameDir,
        bool installIfMissing,
        Action<int>? downloadProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var mode = ModeFromConfig(config.JavaPath);

        if (mode == ModeCustom)
        {
            var custom = NormalizeJavaExecutable(config.JavaPath);
            if (custom.Length == 0)
                throw new InvalidOperationException("Выберите файл javaw.exe или java.exe.");

            var info = await ProbeAsync(custom, "Выбрана вручную", managed: IsManagedPath(custom), ct).ConfigureAwait(false);
            return RequireCompatible(info, "Выбранная Java");
        }

        if (mode == ModeAutomatic)
        {
            var managed = await ProbeFirstAsync(ManagedCandidates(), "Java LegendBorn", managed: true, ct).ConfigureAwait(false);
            if (IsCompatible(managed)) return managed!;
        }

        var system = await ProbeFirstAsync(SystemCandidates(gameDir), "Установлена в системе", managed: false, ct).ConfigureAwait(false);
        if (IsCompatible(system)) return system!;

        if (mode == ModeSystem)
            throw new InvalidOperationException("Java 21 (64-bit) не найдена. Выберите автоматическую установку или укажите Java вручную.");

        if (!installIfMissing)
            throw new InvalidOperationException("Java 21 будет установлена автоматически перед первым запуском.");

        return await InstallManagedAsync(downloadProgress, ct).ConfigureAwait(false);
    }

    public async Task<RuntimeInfo?> TryResolveInstalledAsync(LauncherConfig config, string gameDir, CancellationToken ct)
    {
        try
        {
            return await ResolveAsync(config, gameDir, installIfMissing: false, downloadProgress: null, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static RuntimeInfo RequireCompatible(RuntimeInfo? info, string label)
    {
        if (info is null) throw new InvalidOperationException($"{label} не запускается.");
        if (info.Major < RequiredMajor) throw new InvalidOperationException($"{label}: требуется Java {RequiredMajor} или новее.");
        if (!info.Is64Bit) throw new InvalidOperationException($"{label}: требуется 64-битная Java.");
        return info;
    }

    private static bool IsCompatible(RuntimeInfo? info)
        => info is { Major: >= RequiredMajor, Is64Bit: true };

    private async Task<RuntimeInfo> InstallManagedAsync(Action<int>? progress, CancellationToken ct)
    {
        await InstallGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await ProbeFirstAsync(ManagedCandidates(), "Java LegendBorn", managed: true, ct).ConfigureAwait(false);
            if (IsCompatible(existing)) return existing!;

            if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
                throw new PlatformNotSupportedException("Автоматическая установка Java поддерживается для Windows 64-bit.");

            Directory.CreateDirectory(JavaRuntimeDir);
            Directory.CreateDirectory(LauncherPaths.CacheDir);

            var archivePath = Path.Combine(LauncherPaths.CacheDir, "temurin-21-jre-x64.zip");
            var staging = Path.Combine(JavaRuntimeDir, ".install-" + Guid.NewGuid().ToString("N"));
            TryDeleteFile(archivePath);
            TryDeleteDirectory(staging);

            try
            {
                var finalDownloadUri = await DownloadTemurinAsync(archivePath, progress, ct).ConfigureAwait(false);
                await VerifyTemurinChecksumAsync(archivePath, finalDownloadUri, ct).ConfigureAwait(false);

                Directory.CreateDirectory(staging);
                ExtractZipSafe(archivePath, staging);

                var javaExe = Directory.EnumerateFiles(staging, "java.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "bin", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(javaExe))
                    throw new InvalidOperationException("В загруженном архиве Java не найден java.exe.");

                var runtimeRoot = Directory.GetParent(Path.GetDirectoryName(javaExe)!)?.FullName;
                if (string.IsNullOrWhiteSpace(runtimeRoot) || !IsInside(runtimeRoot, staging))
                    throw new InvalidOperationException("Некорректная структура архива Java.");

                var probe = await ProbeAsync(javaExe, "Java LegendBorn", managed: true, ct).ConfigureAwait(false);
                RequireCompatible(probe, "Загруженная Java");

                TryDeleteDirectory(ManagedJava21Dir);
                Directory.Move(runtimeRoot, ManagedJava21Dir);

                var installedJava = Path.Combine(ManagedJava21Dir, "bin", "java.exe");
                var installed = await ProbeAsync(installedJava, "Java LegendBorn", managed: true, ct).ConfigureAwait(false);
                progress?.Invoke(100);
                return RequireCompatible(installed, "Установленная Java");
            }
            finally
            {
                TryDeleteFile(archivePath);
                TryDeleteDirectory(staging);
            }
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private static async Task<Uri> DownloadTemurinAsync(string archivePath, Action<int>? progress, CancellationToken ct)
    {
        var endpoint = new Uri("https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse", UriKind.Absolute);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(DownloadTimeout);
        using var response = await Http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var length = response.Content.Headers.ContentLength;
        if (length is > MaxArchiveBytes) throw new InvalidOperationException("Архив Java слишком большой.");

        await using var input = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        await using var output = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long total = 0;
        int last = -1;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), linked.Token).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > MaxArchiveBytes) throw new InvalidOperationException("Архив Java превышает безопасный размер.");
            await output.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);

            if (length is > 0)
            {
                var p = Math.Clamp((int)Math.Round(total * 100.0 / length.Value), 0, 99);
                if (p != last) { last = p; progress?.Invoke(p); }
            }
        }
        await output.FlushAsync(linked.Token).ConfigureAwait(false);
        return response.RequestMessage?.RequestUri ?? endpoint;
    }

    private static async Task VerifyTemurinChecksumAsync(string archivePath, Uri finalDownloadUri, CancellationToken ct)
    {
        var checksumUri = new Uri(finalDownloadUri.ToString() + ".sha256.txt", UriKind.Absolute);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        var text = (await Http.GetStringAsync(checksumUri, linked.Token).ConfigureAwait(false)).Trim();
        var expected = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToLowerInvariant() ?? string.Empty;
        if (expected.Length != 64 || expected.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidOperationException("Не удалось проверить контрольную сумму Java.");

        await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("Контрольная сумма Java не совпала.");
    }

    private static void ExtractZipSafe(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Архив Java содержит небезопасный путь.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static IEnumerable<string> ManagedCandidates()
    {
        yield return Path.Combine(ManagedJava21Dir, "bin", "java.exe");
        yield return Path.Combine(ManagedJava21Dir, "bin", "javaw.exe");
    }

    private static IEnumerable<string> SystemCandidates(string gameDir)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        void Add(string? path)
        {
            path = NormalizeJavaExecutable(path);
            if (path.Length > 0 && seen.Add(path)) list.Add(path);
        }

        try
        {
            var runtime = Path.Combine(gameDir ?? string.Empty, "runtime");
            if (Directory.Exists(runtime))
                foreach (var path in Directory.EnumerateFiles(runtime, "java.exe", SearchOption.AllDirectories).Take(16)) Add(path);
        }
        catch { }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) Add(Path.Combine(javaHome, "bin", "java.exe"));

        foreach (var baseDir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
                     .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var vendorDir in new[] { "Eclipse Adoptium", "Java", "Microsoft", "Zulu" })
            {
                var root = Path.Combine(baseDir, vendorDir);
                if (!Directory.Exists(root)) continue;
                try { foreach (var child in Directory.EnumerateDirectories(root).Take(20)) Add(Path.Combine(child, "bin", "java.exe")); } catch { }
            }
        }

        Add("java.exe");
        return list;
    }

    private static async Task<RuntimeInfo?> ProbeFirstAsync(IEnumerable<string> candidates, string source, bool managed, CancellationToken ct)
    {
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var probe = await ProbeAsync(candidate, source, managed, ct).ConfigureAwait(false);
            if (IsCompatible(probe)) return probe;
        }
        return null;
    }

    public static async Task<RuntimeInfo?> ProbeAsync(string javaPath, string source, bool managed, CancellationToken ct)
    {
        javaPath = NormalizeJavaExecutable(javaPath);
        if (javaPath.Length == 0) return null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(ProbeTimeout);
            var psi = new ProcessStartInfo(javaPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("-XshowSettings:properties");
            psi.ArgumentList.Add("-version");
            using var process = Process.Start(psi);
            if (process is null) return null;
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var text = (await stdoutTask.ConfigureAwait(false)) + "\n" + (await stderrTask.ConfigureAwait(false));
            if (process.ExitCode != 0) return null;

            var version = ReadProperty(text, "java.version") ?? VersionQuoted.Match(text).Groups["version"].Value;
            var vendor = ReadProperty(text, "java.vendor") ?? "Java";
            var arch = ReadProperty(text, "os.arch") ?? string.Empty;
            var major = ParseMajor(version);
            var x64 = text.Contains("sun.arch.data.model = 64", StringComparison.OrdinalIgnoreCase)
                      || arch.Contains("amd64", StringComparison.OrdinalIgnoreCase)
                      || arch.Contains("x86_64", StringComparison.OrdinalIgnoreCase)
                      || arch.Contains("aarch64", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("64-Bit Server VM", StringComparison.OrdinalIgnoreCase);
            return major > 0 ? new RuntimeInfo(javaPath, major, x64, version, vendor, source, managed) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static string? ReadProperty(string text, string property)
    {
        var prefix = property + " =";
        foreach (var line in text.Split('\n'))
        {
            var value = line.Trim();
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return value[prefix.Length..].Trim();
        }
        return null;
    }

    private static int ParseMajor(string? version)
    {
        version = (version ?? string.Empty).Trim();
        if (version.Length == 0) return 0;
        var parts = version.Split('.', '-', '+');
        if (!int.TryParse(parts[0], out var major)) return 0;
        if (major != 1) return major;
        return parts.Length > 1 && int.TryParse(parts[1], out var legacy) ? legacy : 0;
    }

    public static string NormalizeJavaExecutable(string? path)
    {
        var value = (path ?? string.Empty).Trim().Trim('"');
        if (value.Length == 0 || value.Equals(SystemSentinel, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (value.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase) || value.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase)) return value;
        if (Directory.Exists(value))
        {
            var javaw = Path.Combine(value, "bin", "javaw.exe");
            if (File.Exists(javaw)) return javaw;
            var java = Path.Combine(value, "bin", "java.exe");
            if (File.Exists(java)) return java;
        }
        return value;
    }

    private static bool IsManagedPath(string path)
    {
        try { return IsInside(path, JavaRuntimeDir); } catch { return false; }
    }

    private static bool IsInside(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LegendBornLauncher-JavaRuntime/1.0");
        return http;
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }
}
