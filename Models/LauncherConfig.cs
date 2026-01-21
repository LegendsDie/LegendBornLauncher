using System;
using System.Runtime.InteropServices;

namespace LegendBorn.Models;

public sealed class LauncherConfig
{
    public string ConfigSchemaVersion { get; set; } = CurrentSchemaVersion;

    // ✅ bump schema (изменили правила RAM)
    public const string CurrentSchemaVersion = "0.2.7";

    public string? LastServerId { get; set; }

    public bool AutoLogin { get; set; } = true;

    public string? LastUsername { get; set; }

    public int LastMenuIndex { get; set; } = 0;

    public string? GameRootPath { get; set; }

    // RamMb <= 0 => AUTO
    // ✅ ТЗ: ручной ввод 4..16 GB
    public int RamMb { get; set; } = RamMinMb; // ✅ дефолт теперь 4GB (не 1GB)

    public string? JavaPath { get; set; }

    public string? LastServerIp { get; set; }

    public bool AutoConnect { get; set; } = true;

    public DateTimeOffset? LastSuccessfulLoginUtc { get; set; }

    public DateTimeOffset? LastLauncherStartUtc { get; set; }

    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    public string? LastLauncherVersion { get; set; }

    // ✅ ТЗ: RAM диапазон
    public const int RamMinMb = 4096;   // 4GB
    public const int RamMaxMb = 16384;  // 16GB

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
            return RamMinMb; // ✅ минимум 4GB

        // ✅ Auto: 50% от RAM, но:
        // - минимум 4GB
        // - максимум 16GB
        // - оставляем системе минимум 2GB
        var rec = (int)Math.Round(total * 0.50);

        var maxByReserve = Math.Max(RamMinMb, total - 2048);
        var hardMax = Math.Min(RamMaxMb, maxByReserve);

        rec = Clamp(rec, RamMinMb, hardMax);

        rec = (rec / 256) * 256;
        if (rec < RamMinMb) rec = RamMinMb;
        if (rec > RamMaxMb) rec = RamMaxMb;

        return rec;
    }

    public void SetAutoRam() => RamMb = 0;

    public void SetManualRamMb(int mb) => RamMb = NormalizeRamMb(mb);

    private static int NormalizeRamMb(int mb)
    {
        // AUTO
        if (mb <= 0)
            return 0;

        mb = Clamp(mb, min: RamMinMb, max: RamMaxMb);

        var total = TryGetTotalPhysicalMemoryMb();
        if (total > 0)
        {
            // запас минимум 2GB
            var maxAllowed = Math.Max(RamMinMb, total - 2048);
            maxAllowed = Math.Min(maxAllowed, RamMaxMb);

            if (mb > maxAllowed)
                mb = maxAllowed;
        }

        mb = (mb / 256) * 256;
        if (mb < RamMinMb) mb = RamMinMb;
        if (mb > RamMaxMb) mb = RamMaxMb;

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
