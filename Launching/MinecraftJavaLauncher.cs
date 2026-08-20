using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.VersionLoader;
using LegendBorn.Services;

namespace LegendBorn.Launching;

public static class MinecraftJavaLauncher
{
    private static readonly HttpClient Http = CreateHttp();

    public static async Task<Process> BuildAndLaunchAsync(
        MinecraftService minecraft,
        string version,
        string username,
        int ramMb,
        string javaPath,
        string? serverIp = null,
        MinecraftService.LegendCoreSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(minecraft);
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("version is empty", nameof(version));
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("username is empty", nameof(username));
        if (string.IsNullOrWhiteSpace(javaPath)) throw new ArgumentException("javaPath is empty", nameof(javaPath));
        if (!File.Exists(javaPath) && !string.Equals(Path.GetFileName(javaPath), "java.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Выбранная Java не найдена.", javaPath);

        ramMb = Math.Clamp(ramMb <= 0 ? MinecraftService.MinRamMb : ramMb, MinecraftService.MinRamMb, MinecraftService.MaxRamMb);
        minecraft.ClearLegendCoreSession();
        if (session is not null) minecraft.WriteLegendCoreSession(session);

        var path = new MinecraftPath(minecraft.GameDir);
        var parameters = MinecraftLauncherParameters.CreateDefault(path, Http);
        if (parameters.VersionLoader is MojangJsonVersionLoaderV2 versionLoader)
            versionLoader.UseLocalManifestWhenError = true;

        var launcher = new MinecraftLauncher(parameters);
        var options = new MLaunchOption
        {
            Session = MSession.CreateOfflineSession(username.Trim()),
            MaximumRamMb = ramMb,
            JavaPath = javaPath.Trim()
        };
        if (!string.IsNullOrWhiteSpace(serverIp)) options.ServerIp = serverIp.Trim();

        var process = await launcher.BuildProcessAsync(version, options).ConfigureAwait(false);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        SanitizeJavaEnvironment(process);
        process.EnableRaisingEvents = true;

        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить процесс Minecraft.");

        try
        {
            process.Exited += (_, _) =>
            {
                try { minecraft.ClearLegendCoreSession(); } catch { }
            };
        }
        catch { }

        return process;
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8
        };
        var http = new HttpClient(new MinecraftDistributionHttpHandler(handler))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LegendBornLauncher-CmlLib/1.0");
        return http;
    }

    private static void SanitizeJavaEnvironment(Process process)
    {
        try
        {
            var env = process.StartInfo.Environment;
            env.Remove("JAVA_TOOL_OPTIONS");
            env.Remove("_JAVA_OPTIONS");
            env.Remove("JDK_JAVA_OPTIONS");
        }
        catch { }
    }
}
