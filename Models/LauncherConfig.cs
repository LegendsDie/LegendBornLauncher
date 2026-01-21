using System;
using System.Runtime.InteropServices;

namespace LegendBorn.Models;

public sealed class LauncherConfig
{
    public string ConfigSchemaVersion { get; set; } = CurrentSchemaVersion;

    public const string CurrentSchemaVersion = "0.2.6";

    public string? LastServerId { get; set; }

    public bool AutoLogin { get; set; } = true;

    public string? LastUsername { get; set; }

    public int LastMenuIndex { get; set; } = 0;

    public string? GameRootPath { get; set; }

    // RamMb <= 0 => AUTO
    public int RamMb { get; set; } = 0;

    public string? JavaPath { get; set; }

    public string? LastServerIp { get; set; }

    public bool AutoConnect { get; set; } = true;

    public DateTimeOffset? LastSuccessfulLoginUtc { get; set; }

    public DateTimeOffset? LastLauncherStartUtc { get; set; }

    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    public string? LastLauncherVersion { get; set; }

    public void Normalize()
    {
        ConfigSchemaVersion = NormalizeRequired(ConfigSchemaVersion, "0.0.0");

        LastServerId = NormalizeOptional(LastServerId);
        LastServerIp = NormalizeOptional(LastServerIp);
        GameRootPath = NormalizeOptional(GameRootPath);
        JavaPath = NormalizeOptional(JavaPath);
        LastUsername = NormalizeOptional(LastUsername);
        LastLauncherVersion = NormalizeOptional(LastLauncherVersion);

        LastMenuIndex = Clamp(LastMenuIndex, min: 0, max: 50);

        RamMb = NormalizeRamMb(RamMb);

        LastSuccessfulLoginUtc = NormalizeUtc(LastSuccessfulLoginUtc);
        LastLauncherStartUtc = NormalizeUtc(LastLauncherStartUtc);
        LastUpdateCheckUtc = NormalizeUtc(LastUpdateCheckUtc);
    }

    public bool HasServerIpOverride => !string.IsNullOrWhiteSpace(LastServerIp);

    public void ClearServerIpOverride() => LastServerIp = null;

    public bool IsRamAuto => RamMb <= 0;

    public int GetEffectiveRamMb()
    {
        if (!IsRamAuto)
            return NormalizeRamMb(RamMb);

        var total = TryGetTotalPhysicalMemoryMb();
        if (total <= 0)
            return 4096;

        // Цель: не душить ОС и оставить запас, но дать адекватно для модпака.
        //  - минимум 2048
        //  - максимум 24576 (24GB) по умолчанию
        //  - не больше (total - 3072) чтобы ОС/драйверам хватало
        var cap = Math.Max(2048, total - 3072);
        var rec = (int)Math.Round(total * 0.50);
        rec = Clamp(rec, 2048, 24576);
        rec = Math.Min(rec, cap);

        if (rec < 2048) rec = 2048;
        return rec;
    }

    public void SetAutoRam() => RamMb = 0;

    public void SetManualRamMb(int mb) => RamMb = NormalizeRamMb(mb);

    private static int NormalizeRamMb(int mb)
    {
        // AUTO
        if (mb <= 0)
            return 0;

        mb = Clamp(mb, min: 1024, max: 65536);

        var total = TryGetTotalPhysicalMemoryMb();
        if (total > 0)
        {
            // Не даём поставить больше физической памяти с запасом на ОС
            var maxAllowed = Math.Max(1024, total - 1536);
            if (mb > maxAllowed)
                mb = maxAllowed;
        }

        if (mb < 1024) mb = 1024;
        return mb;
    }

    private static string NormalizeRequired(string? value, string fallback)
    {
        var v = (value ?? "").Trim();
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    private static string? NormalizeOptional(string? value)
    {
        var v = (value ?? "").Trim();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static int Clamp(int v, int min, int max)
    {
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }

    private static DateTimeOffset? NormalizeUtc(DateTimeOffset? v)
    {
        if (v is null) return null;

        try
        {
            var utc = v.Value.ToUniversalTime();
            if (utc.Year < 2000) return null;
            if (utc > DateTimeOffset.UtcNow.AddDays(2)) return null;
            return utc;
        }
        catch
        {
            return null;
        }
    }

    private static int TryGetTotalPhysicalMemoryMb()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return 0;

            var ms = new MEMORYSTATUSEX();
            ms.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();

            if (!GlobalMemoryStatusEx(ref ms))
                return 0;

            var totalBytes = ms.ullTotalPhys;
            if (totalBytes <= 0)
                return 0;

            var mb = (long)(totalBytes / (1024UL * 1024UL));
            if (mb <= 0) return 0;
            if (mb > int.MaxValue) return int.MaxValue;
            return (int)mb;
        }
        catch
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
