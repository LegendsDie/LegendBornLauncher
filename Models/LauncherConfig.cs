// File: Models/LauncherConfig.cs
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LegendBorn.Models;

public sealed class LauncherConfig
{
    // ✅ bump schema (миграции RAM, кэш физ.памяти, более предсказуемая нормализация)
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
    /// Если задано (1..128), будет мигрировано в RamMb при Normalize().
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
    public const int RamMinMb = 4096;   // 4GB (минималка фикс)
    public const int RamMaxMb = 16384;  // 16GB

    // сколько оставить ОС минимум (2GB)
    private const int OsReserveMb = 2048;

    // шаг округления RAM
    private const int RamStepMb = 256;

    // ✅ кэш физической памяти (чтобы не дёргать /proc/meminfo или WinAPI слишком часто)
    private static readonly object _memCacheLock = new();
    private static int _cachedTotalPhysicalMb;
    private static long _cachedTotalPhysicalAtTicks; // Environment.TickCount64
    private const int MemCacheTtlMs = 30_000; // 30 секунд

    public void Normalize()
    {
        var oldSchema = NormalizeRequired(ConfigSchemaVersion, "0.0.0");
        ConfigSchemaVersion = oldSchema;

        LastServerId = NormalizeOptional(LastServerId);
        LastServerIp = NormalizeOptional(LastServerIp);
        GameRootPath = NormalizeOptional(GameRootPath);
        JavaPath = NormalizeOptional(JavaPath);
        LastUsername = NormalizeOptional(LastUsername);
        LastLauncherVersion = NormalizeOptional(LastLauncherVersion);

        LastMenuIndex = Clamp(LastMenuIndex, min: 0, max: 50);

        // ✅ миграция старого поля RamGb -> RamMb
        // Важно: не перетирать ручной RamMb=4096, если конфиг уже новый.
        if (RamGb is int gb && gb is >= 1 and <= 128)
        {
            var isOldConfig = CompareSemVer(oldSchema, "0.2.8") < 0;

            // мигрируем только если RamMb = AUTO/пусто или конфиг реально старый и RamMb выглядит дефолтом
            if (RamMb <= 0 || (isOldConfig && RamMb == RamMinMb))
            {
                RamMb = gb * 1024;
            }

            RamGb = null; // убираем, чтобы дальше не мешало
        }

        // ✅ защита от старого UI/конфигов, где могли писать "16" и подразумевать GB:
        // делаем это как миграцию только для СТАРЫХ схем.
        if (CompareSemVer(oldSchema, "0.2.8") < 0)
        {
            if (RamMb is >= 4 and <= 64)
                RamMb *= 1024;
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

        // ✅ после нормализации считаем, что конфиг уже на текущей схеме
        ConfigSchemaVersion = CurrentSchemaVersion;
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

        var total = TryGetTotalPhysicalMemoryMbCached();
        if (total <= 0)
            return RamMinMb;

        // ✅ Auto-логика:
        // - берём 50% от физической RAM
        // - оставляем системе минимум 2GB
        // - жёсткие рамки 4..16GB
        // - округление вниз до 256MB
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

        // ❗️Важно: тут больше НЕ угадываем "это GB".
        // Все эвристики/миграции делаем в Normalize() по версии схемы.

        mb = Clamp(mb, RamMinMb, RamMaxMb);

        var total = TryGetTotalPhysicalMemoryMbCached();
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

            p = p.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            // убираем завершающие слеши (кроме "корня")
            // Windows: "C:\" (len=3) оставляем; Linux: "/" (len=1) оставляем
            while (p.Length > 3 && (p.EndsWith("\\", StringComparison.Ordinal) || p.EndsWith("/", StringComparison.Ordinal)))
                p = p[..^1];

            return string.IsNullOrWhiteSpace(p) ? null : p;
        }
        catch
        {
            return NormalizeOptional(path);
        }
    }

    // =========================
    // Physical memory (cached)
    // =========================

    private static int TryGetTotalPhysicalMemoryMbCached()
    {
        try
        {
            var now = Environment.TickCount64;

            lock (_memCacheLock)
            {
                if (_cachedTotalPhysicalMb > 0 &&
                    (now - _cachedTotalPhysicalAtTicks) >= 0 &&
                    (now - _cachedTotalPhysicalAtTicks) < MemCacheTtlMs)
                {
                    return _cachedTotalPhysicalMb;
                }

                var mb = TryGetTotalPhysicalMemoryMb_NoCache();
                if (mb > 0)
                {
                    _cachedTotalPhysicalMb = mb;
                    _cachedTotalPhysicalAtTicks = now;
                }
                else
                {
                    // если не смогли определить — не кэшируем надолго (чтобы можно было попытаться снова)
                    _cachedTotalPhysicalMb = 0;
                    _cachedTotalPhysicalAtTicks = now;
                }

                return mb;
            }
        }
        catch
        {
            return 0;
        }
    }

    private static int TryGetTotalPhysicalMemoryMb_NoCache()
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

                // минимальные аллокации: всё равно ок, но без Split массивов
                // найдём первое число в строке
                int i = 0;
                while (i < line.Length && (line[i] < '0' || line[i] > '9')) i++;
                if (i >= line.Length) return 0;

                int j = i;
                while (j < line.Length && (line[j] >= '0' && line[j] <= '9')) j++;

                if (!long.TryParse(line.AsSpan(i, j - i), out var kb) || kb <= 0)
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

    // =========================
    // SemVer-like compare (0.2.8)
    // =========================

    // Возвращает:
    // <0 если a < b
    //  0 если a == b
    // >0 если a > b
    private static int CompareSemVer(string? a, string? b)
    {
        var va = ParseSemVer(a);
        var vb = ParseSemVer(b);

        var c = va.Major.CompareTo(vb.Major);
        if (c != 0) return c;

        c = va.Minor.CompareTo(vb.Minor);
        if (c != 0) return c;

        return va.Patch.CompareTo(vb.Patch);
    }

    private static (int Major, int Minor, int Patch) ParseSemVer(string? v)
    {
        v = (v ?? "").Trim();
        if (string.IsNullOrWhiteSpace(v))
            return (0, 0, 0);

        // допускаем "0.2.8" / "0.2" / "0"
        var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int major = 0, minor = 0, patch = 0;

        if (parts.Length >= 1) int.TryParse(parts[0], out major);
        if (parts.Length >= 2) int.TryParse(parts[1], out minor);
        if (parts.Length >= 3) int.TryParse(parts[2], out patch);

        if (major < 0) major = 0;
        if (minor < 0) minor = 0;
        if (patch < 0) patch = 0;

        return (major, minor, patch);
    }

    // =========================
    // WinAPI structs
    // =========================

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
