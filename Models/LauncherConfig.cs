using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LegendBorn.Models;

public sealed class LauncherConfig
{
    // ✅ bump schema (добавили RamGb и улучшили авто-алгоритм/кроссплатформ)
    public const string CurrentSchemaVersion = "0.2.8";

    public string ConfigSchemaVersion { get; set; } = CurrentSchemaVersion;

    public string? LastServerId { get; set; }

    public bool AutoLogin { get; set; } = true;

    public string? LastUsername { get; set; }

    public int LastMenuIndex { get; set; } = 0;

    public string? GameRootPath { get; set; }

    /// <summary>
    /// RAM в мегабайтах.
    /// 0 или меньше => AUTO.
    /// </summary>
    public int RamMb { get; set; } = RamMinMb; // дефолт: 4GB

    /// <summary>
    /// Устаревшее поле (старые конфиги могли хранить GB).
    /// Если задано (4..16), будет мигрировано в RamMb при Normalize().
    /// </summary>
    public int? RamGb { get; set; }

    public string? JavaPath { get; set; }

    public string? LastServerIp { get; set; }

    public bool AutoConnect { get; set; } = true;

    public DateTimeOffset? LastSuccessfulLoginUtc { get; set; }

    public DateTimeOffset? LastLauncherStartUtc { get; set; }

    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    public string? LastLauncherVersion { get; set; }

    // ✅ ТЗ: ручной диапазон RAM
    public const int RamMinMb = 4096;   // 4GB
    public const int RamMaxMb = 16384;  // 16GB

    // сколько оставить ОС минимум (2GB)
    private const int OsReserveMb = 2048;

    // шаг округления RAM
    private const int RamStepMb = 256;

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

        // ✅ миграция старого поля RamGb -> RamMb
        if (RamGb is int gb && gb is >= 1 and <= 128)
        {
            // если в старом конфиге RamMb пустое/авто — берём gb
            if (RamMb == 0 || RamMb == RamMinMb) // RamMinMb был дефолтом, но миграцию делаем осторожно
            {
                RamMb = gb * 1024;
            }
            RamGb = null; // убираем, чтобы дальше не мешало
        }

        RamMb = NormalizeRamMb(RamMb);

        // пути: чуть-чуть подчистим мусор
        if (!string.IsNullOrWhiteSpace(GameRootPath))
            GameRootPath = NormalizePath(GameRootPath);

        if (!string.IsNullOrWhiteSpace(JavaPath))
            JavaPath = NormalizePath(JavaPath);

        LastSuccessfulLoginUtc = NormalizeUtc(LastSuccessfulLoginUtc);
        LastLauncherStartUtc = NormalizeUtc(LastLauncherStartUtc);
        LastUpdateCheckUtc = NormalizeUtc(LastUpdateCheckUtc);
    }

    public bool HasServerIpOverride => !string.IsNullOrWhiteSpace(LastServerIp);

    public void ClearServerIpOverride() => LastServerIp = null;

    public bool IsRamAuto => RamMb <= 0;

    public void SetAutoRam() => RamMb = 0;

    public void SetManualRamMb(int mb) => RamMb = NormalizeRamMb(mb);

    public void SetManualRamGb(int gb) => RamMb = NormalizeRamMb(gb * 1024);

    /// <summary>
    /// Итоговая RAM для запуска:
    /// - если RamMb вручную => нормализованное значение
    /// - если AUTO => рассчитывается по физической памяти
    /// </summary>
    public int GetEffectiveRamMb()
    {
        if (!IsRamAuto)
            return NormalizeRamMb(RamMb);

        var total = TryGetTotalPhysicalMemoryMb();
        if (total <= 0)
            return RamMinMb;

        // ✅ Auto-логика:
        // - берём 50% от физической RAM
        // - оставляем системе минимум 2GB
        // - жёсткие рамки 4..16GB
        // - округление до 256MB
        var rec = (int)Math.Round(total * 0.50);

        var maxByReserve = Math.Max(RamMinMb, total - OsReserveMb);
        var hardMax = Math.Min(RamMaxMb, maxByReserve);

        rec = Clamp(rec, RamMinMb, hardMax);
        rec = RoundDownToStep(rec, RamStepMb);

        return Clamp(rec, RamMinMb, RamMaxMb);
    }

    private static int NormalizeRamMb(int mb)
    {
        // AUTO
        if (mb <= 0)
            return 0;

        // защита от “вбили 4..16 и подразумевали GB”
        // если указали маленькое число 4..64 — вероятнее всего это GB, а не MB
        if (mb is >= 4 and <= 64)
            mb *= 1024;

        mb = Clamp(mb, RamMinMb, RamMaxMb);

        var total = TryGetTotalPhysicalMemoryMb();
        if (total > 0)
        {
            // запас минимум 2GB
            var maxAllowed = Math.Max(RamMinMb, total - OsReserveMb);
            maxAllowed = Math.Min(maxAllowed, RamMaxMb);

            if (mb > maxAllowed)
                mb = maxAllowed;
        }

        mb = RoundDownToStep(mb, RamStepMb);

        return Clamp(mb, RamMinMb, RamMaxMb);
    }

    private static int RoundDownToStep(int value, int step)
    {
        if (step <= 1) return value;
        return (value / step) * step;
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

    private static string? NormalizePath(string? path)
    {
        try
        {
            var p = (path ?? "").Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(p)) return null;

            // не делаем Path.GetFullPath если это может взорваться на кривом вводе — но попробуем безопасно
            p = p.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            // убираем завершающие слеши (кроме корня)
            while (p.Length > 3 && (p.EndsWith("\\") || p.EndsWith("/")))
                p = p[..^1];

            return string.IsNullOrWhiteSpace(p) ? null : p;
        }
        catch
        {
            return NormalizeOptional(path);
        }
    }

    private static int TryGetTotalPhysicalMemoryMb()
    {
        // ✅ кроссплатформ: Windows через GlobalMemoryStatusEx, Linux через /proc/meminfo
        try
        {
            if (OperatingSystem.IsWindows())
                return TryGetTotalPhysicalMemoryMb_Windows();

            if (OperatingSystem.IsLinux())
                return TryGetTotalPhysicalMemoryMb_Linux();

            // macOS/прочее — пока без нативных вызовов, вернём 0 => fallback на 4GB
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int TryGetTotalPhysicalMemoryMb_Linux()
    {
        try
        {
            const string memInfo = "/proc/meminfo";
            if (!File.Exists(memInfo))
                return 0;

            // MemTotal:       16322464 kB
            foreach (var line in File.ReadLines(memInfo))
            {
                if (!line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return 0;

                if (!long.TryParse(parts[1], out var kb) || kb <= 0)
                    return 0;

                var mb = kb / 1024;
                if (mb <= 0) return 0;
                if (mb > int.MaxValue) return int.MaxValue;
                return (int)mb;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int TryGetTotalPhysicalMemoryMb_Windows()
    {
        try
        {
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
