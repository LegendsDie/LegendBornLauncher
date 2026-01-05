using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using System.Diagnostics;

namespace LegendBorn.Services;

public sealed class MinecraftService
{
    private readonly MinecraftLauncher _launcher;

    public event EventHandler<string>? Log;
    public event EventHandler<int>? ProgressPercent;

    public MinecraftService(string gameDir)
    {
        var path = new MinecraftPath(gameDir);
        _launcher = new MinecraftLauncher(path);

        _launcher.FileProgressChanged += (_, args) =>
        {
            var percent = args.TotalTasks > 0
                ? (int)Math.Round(args.ProgressedTasks * 100.0 / args.TotalTasks)
                : 0;

            ProgressPercent?.Invoke(this, Math.Clamp(percent, 0, 100));
            Log?.Invoke(this, $"{args.EventType}: {args.Name} ({args.ProgressedTasks}/{args.TotalTasks})");
        };

        _launcher.ByteProgressChanged += (_, args) =>
        {
            if (args.TotalBytes <= 0) return;
            var percent = (int)Math.Round(args.ProgressedBytes * 100.0 / args.TotalBytes);
            ProgressPercent?.Invoke(this, Math.Clamp(percent, 0, 100));
        };
    }

    public async Task<IReadOnlyList<string>> GetAllVersionNamesAsync()
    {
        var versions = await _launcher.GetAllVersionsAsync();
        return versions.Select(v => v.Name).ToList();
    }

    public async Task InstallAsync(string version)
    {
        Log?.Invoke(this, $"Установка версии {version}...");
        await _launcher.InstallAsync(version);
        ProgressPercent?.Invoke(this, 100);
        Log?.Invoke(this, "Установка завершена.");
    }

    public async Task<Process> BuildAndLaunchAsync(string version, string username, int ramMb, string? serverIp = null)
    {
        var opt = new MLaunchOption
        {
            Session = MSession.CreateOfflineSession(username),
            MaximumRamMb = ramMb
        };

        if (!string.IsNullOrWhiteSpace(serverIp))
            opt.ServerIp = serverIp;

        var process = await _launcher.BuildProcessAsync(version, opt);
        process.EnableRaisingEvents = true;
        process.Start();
        return process;
    }
}
