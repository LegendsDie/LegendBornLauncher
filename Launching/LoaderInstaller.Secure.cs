using CmlLib.Core;
using LegendBorn.Services;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Launching;

/// <summary>
/// NeoForge installer implementation backed by the authoritative launcher catalog.
/// Original installer bytes are SHA-256 verified before Java starts. The JAR is never mutated.
/// </summary>
public sealed class LoaderInstaller
{
    private const long MaxInstallerBytes = 100L * 1024 * 1024;
    private const long MaxMetadataEntryBytes = 4L * 1024 * 1024;
    private const int OutputCapChars = 512 * 1024;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan OfficialDownloadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InstallOverallTimeout = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan JavaProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly MinecraftPath _path;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallLocks = new(StringComparer.OrdinalIgnoreCase);

    public LoaderInstaller(MinecraftPath path, HttpClient http, Action<string>? log = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log;
    }

    public async Task<string> EnsureInstalledAsync(
        string minecraftVersion,
        string loaderType,
        string loaderVersion,
        string installerUrl,
        CancellationToken ct)
    {
        minecraftVersion = (minecraftVersion ?? "").Trim();
        loaderType = (loaderType ?? "vanilla").Trim().ToLowerInvariant();
        loaderVersion = (loaderVersion ?? "").Trim();
        installerUrl = (installerUrl ?? "").Trim();

        if (loaderType == "vanilla") return minecraftVersion;
        if (loaderType != "neoforge") throw new NotSupportedException($"Loader '{loaderType}' не поддерживается.");
        if (minecraftVersion.Length == 0 || loaderVersion.Length == 0)
            throw new InvalidOperationException("NeoForge version contract incomplete.");

        if (!NeoForgeDistributionBootstrap.TryResolve(loaderVersion, out var distribution))
            throw new InvalidOperationException($"Для NeoForge {loaderVersion} отсутствует доверенный distribution contract.");
        if (!NeoForgeDistributionBootstrap.IsSha256(distribution.InstallerSha256))
            throw new InvalidOperationException("NeoForge installer SHA-256 отсутствует или повреждён.");

        var compatibilityUrl = NeoForgeDistributionBootstrap.NormalizeHttpsUrl(installerUrl);
        if (compatibilityUrl.Length > 0 &&
            !distribution.InstallerMirrors.Contains(compatibilityUrl, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("loader.installerUrl не входит в catalog installerMirrors.");

        var expectedId = $"{minecraftVersion}-neoforge-{loaderVersion}";
        var gate = InstallLocks.GetOrAdd(expectedId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsVersionPresent(expectedId)) return expectedId;

            var installerPath = await DownloadVerifiedInstallerAsync(minecraftVersion, distribution, ct).ConfigureAwait(false);
            RequireInstallerHash(installerPath, distribution.InstallerSha256);
            ValidateInstallerMetadata(installerPath, loaderVersion);

            var installed = await InstallOriginalJarAsync(installerPath, loaderVersion, expectedId, distribution, ct)
                .ConfigureAwait(false);
            return installed ?? throw new InvalidOperationException("NeoForge installer завершился, но version JSON не найден.");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> DownloadVerifiedInstallerAsync(
        string minecraftVersion,
        NeoForgeDistributionSpec distribution,
        CancellationToken ct)
    {
        var root = _path.BasePath ?? throw new InvalidOperationException("MinecraftPath.BasePath пустой.");
        var dir = Path.Combine(root, "launcher", "installers", "neoforge", minecraftVersion, distribution.LoaderVersion);
        Directory.CreateDirectory(dir);
        var finalPath = Path.Combine(dir, $"neoforge-{distribution.LoaderVersion}-installer.jar");

        if (File.Exists(finalPath))
        {
            try
            {
                RequireInstallerHash(finalPath, distribution.InstallerSha256);
                ValidateInstallerMetadata(finalPath, distribution.LoaderVersion);
                return finalPath;
            }
            catch
            {
                TryDelete(finalPath);
            }
        }

        Exception? last = null;
        foreach (var candidate in distribution.InstallerMirrors.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var normalized = NeoForgeDistributionBootstrap.NormalizeHttpsUrl(candidate);
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) continue;
            var temp = finalPath + ".tmp";
            TryDelete(temp);

            try
            {
                var source = NeoForgeDistributionBootstrap.DescribeSource(normalized);
                _log?.Invoke($"NeoForge installer: {source}");
                if (source == "BMCLAPI") _log?.Invoke("Источник загрузки: BMCLAPI");
                await DownloadAsync(uri, temp, ct).ConfigureAwait(false);
                RequireInstallerHash(temp, distribution.InstallerSha256);
                ValidateInstallerMetadata(temp, distribution.LoaderVersion);
                File.Move(temp, finalPath, overwrite: true);
                return finalPath;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDelete(temp);
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                TryDelete(temp);
                _log?.Invoke($"NeoForge installer mirror failed: {ex.Message}");
            }
        }

        throw new InvalidOperationException("Ни одно installer mirror не отдало JAR с ожидаемым SHA-256.", last);
    }

    private async Task DownloadAsync(Uri uri, string path, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(
            uri.Host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase)
                ? OfficialDownloadTimeout
                : DownloadTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/java-archive"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > MaxInstallerBytes))
            throw new InvalidOperationException($"Некорректный installer size: {length}");

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Вместо NeoForge installer получена HTML-страница.");

        await using var input = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        await using var output = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[]? buffer = null;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), linked.Token).ConfigureAwait(false);
                if (read <= 0) break;
                total += read;
                if (total > MaxInstallerBytes)
                    throw new InvalidOperationException("NeoForge installer превышает безопасный размер.");
                await output.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
            }
            await output.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void RequireInstallerHash(string path, string expected)
    {
        expected = NeoForgeDistributionBootstrap.NormalizeSha256(expected);
        if (!NeoForgeDistributionBootstrap.IsSha256(expected))
            throw new InvalidOperationException("Invalid expected installer SHA-256.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"NeoForge SHA-256 mismatch: expected={expected}, actual={actual}");
    }

    private static void ValidateInstallerMetadata(string path, string loaderVersion)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries.Where(static entry =>
                     (entry.FullName.EndsWith("install_profile.json", StringComparison.OrdinalIgnoreCase) ||
                      entry.FullName.EndsWith("version.json", StringComparison.OrdinalIgnoreCase)) &&
                     entry.Length > 0 && entry.Length <= MaxMetadataEntryBytes))
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var text = reader.ReadToEnd();
            if (text.Contains("neoforge", StringComparison.OrdinalIgnoreCase) &&
                text.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase))
                return;
        }

        throw new InvalidOperationException("NeoForge installer metadata does not match requested version.");
    }

    private async Task<string?> InstallOriginalJarAsync(
        string installerPath,
        string loaderVersion,
        string expectedId,
        NeoForgeDistributionSpec distribution,
        CancellationToken ct)
    {
        var root = _path.BasePath ?? throw new InvalidOperationException("MinecraftPath.BasePath пустой.");
        var java = await FindJavaAsync(ct).ConfigureAwait(false);
        Exception? last = null;

        foreach (var mirror in NeoForgeDistributionBootstrap.GetEffectiveMavenMirrors(distribution))
        {
            ct.ThrowIfCancellationRequested();
            var normalized = NeoForgeDistributionBootstrap.NormalizeHttpsBase(mirror);
            if (normalized.Length == 0) continue;

            var source = NeoForgeDistributionBootstrap.DescribeSource(normalized);
            _log?.Invoke($"NeoForge Maven: {source}");
            if (source == "BMCLAPI") _log?.Invoke("Источник зависимостей: BMCLAPI");

            var appData = Path.Combine(root, "launcher", "tmp", "neoforge", Guid.NewGuid().ToString("N"));
            var tempMc = Path.Combine(appData, ".minecraft");
            try
            {
                Directory.CreateDirectory(tempMc);
                WriteLauncherProfile(tempMc);
                var env = new Dictionary<string, string>
                {
                    ["APPDATA"] = appData,
                    ["LOCALAPPDATA"] = appData
                };

                var result = await RunJavaAsync(
                        java,
                        new[]
                        {
                            "-jar",
                            installerPath,
                            "--installClient",
                            distribution.InstallerMirrorArgument,
                            normalized
                        },
                        root,
                        env,
                        ct)
                    .ConfigureAwait(false);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException(result.Error.Length > 0 ? result.Error : result.Output);

                var installed = FindNeoForgeVersion(tempMc, loaderVersion);
                if (installed is null)
                    throw new InvalidOperationException("Installer did not create NeoForge version JSON.");

                MergeDir(Path.Combine(tempMc, "versions"), Path.Combine(root, "versions"));
                MergeDir(Path.Combine(tempMc, "libraries"), Path.Combine(root, "libraries"));
                MergeDir(Path.Combine(tempMc, "assets"), Path.Combine(root, "assets"));

                if (IsVersionPresent(expectedId)) return expectedId;
                if (IsVersionPresent(installed)) return installed;
                return FindNeoForgeVersion(root, loaderVersion);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                _log?.Invoke($"NeoForge Maven mirror {source} failed: {ex.Message}");
            }
            finally
            {
                try { if (Directory.Exists(appData)) Directory.Delete(appData, true); } catch { }
            }
        }

        throw new InvalidOperationException("NeoForge installation failed on all Maven mirrors.", last);
    }

    private async Task<string> FindJavaAsync(CancellationToken ct)
    {
        var candidates = new List<string>();
        var root = _path.BasePath ?? "";

        try
        {
            var runtime = Path.Combine(root, "runtime");
            if (Directory.Exists(runtime))
                candidates.AddRange(Directory.EnumerateFiles(runtime, "java.exe", SearchOption.AllDirectories));
        }
        catch { }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            candidates.Add(Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"));

        candidates.Add(OperatingSystem.IsWindows() ? "java.exe" : "java");

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var probe = await ProbeJavaAsync(candidate, ct).ConfigureAwait(false);
            if (probe is { Major: >= 21, Is64Bit: true })
            {
                _log?.Invoke($"Java: найден {probe.Major} x64 ({candidate}).");
                return candidate;
            }
        }

        throw new InvalidOperationException("Требуется Java 21+ x64.");
    }

    private sealed record JavaProbe(int Major, bool Is64Bit);

    private static async Task<JavaProbe?> ProbeJavaAsync(string java, CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(JavaProbeTimeout);

            var psi = new ProcessStartInfo(java)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-XshowSettings:properties");
            psi.ArgumentList.Add("-version");

            using var process = Process.Start(psi);
            if (process is null) return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            var text = (await stdoutTask.ConfigureAwait(false)) + "\n" +
                       (await stderrTask.ConfigureAwait(false));
            if (process.ExitCode != 0) return null;

            var major = ParseJavaMajor(text);
            var x64 =
                text.Contains("sun.arch.data.model = 64", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("os.arch = amd64", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("os.arch = x86_64", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("os.arch = aarch64", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("64-Bit Server VM", StringComparison.OrdinalIgnoreCase);

            return major > 0 ? new JavaProbe(major, x64) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static int ParseJavaMajor(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var value = line.Trim();
            if (value.StartsWith("java.version =", StringComparison.OrdinalIgnoreCase))
            {
                var version = value["java.version =".Length..].Trim();
                var token = version.Split('.', '-', '+')[0];
                if (int.TryParse(token, out var major))
                {
                    if (major != 1) return major;
                    var parts = version.Split('.');
                    if (parts.Length > 1 && int.TryParse(parts[1], out var legacy)) return legacy;
                }
            }

            var marker = value.IndexOf("version \"", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                var start = marker + "version \"".Length;
                var end = value.IndexOf('"', start);
                if (end > start)
                {
                    var token = value[start..end].Split('.', '-', '+')[0];
                    if (int.TryParse(token, out var quotedMajor)) return quotedMajor;
                }
            }
        }

        return 0;
    }

    private async Task<(int ExitCode, string Output, string Error)> RunJavaAsync(
        string java,
        IEnumerable<string> args,
        string workingDirectory,
        IDictionary<string, string> env,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(InstallOverallTimeout);

        var psi = new ProcessStartInfo(java)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        foreach (var pair in env) psi.Environment[pair.Key] = pair.Value;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить Java.");
        var outputTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var errorTask = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"NeoForge installer timeout after {InstallOverallTimeout}.");
        }

        var stdout = await outputTask.ConfigureAwait(false);
        var stderr = await errorTask.ConfigureAwait(false);
        if (stdout.Length > OutputCapChars) stdout = stdout[^OutputCapChars..];
        if (stderr.Length > OutputCapChars) stderr = stderr[^OutputCapChars..];
        return (process.ExitCode, stdout.Trim(), stderr.Trim());
    }

    private bool IsVersionPresent(string id)
    {
        var root = _path.BasePath ?? "";
        return File.Exists(Path.Combine(root, "versions", id, id + ".json"));
    }

    private static string? FindNeoForgeVersion(string root, string loaderVersion)
    {
        var versions = Path.Combine(root, "versions");
        if (!Directory.Exists(versions)) return null;
        return Directory.EnumerateDirectories(versions)
            .Select(Path.GetFileName)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .FirstOrDefault(value =>
                value.Contains("neoforge", StringComparison.OrdinalIgnoreCase) &&
                value.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteLauncherProfile(string mcDir)
    {
        var json = JsonSerializer.Serialize(new
        {
            profiles = new Dictionary<string, object>(),
            settings = new Dictionary<string, object>(),
            launcherVersion = new { name = "LegendBorn", format = 21 }
        });
        File.WriteAllText(Path.Combine(mcDir, "launcher_profiles.json"), json);
        File.WriteAllText(Path.Combine(mcDir, "launcher_profiles_microsoft_store.json"), json);
    }

    private static void MergeDir(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
