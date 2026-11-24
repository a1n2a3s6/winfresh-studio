using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WinFresh.Models;

namespace WinFresh.Engine;

public sealed class TelemetryService
{
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public (double UsedGb, double TotalGb, double Percent) GetMemorySnapshot()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem))
            return (0, 0, 0);

        double totalGb = mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
        double availGb = mem.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
        double usedGb = Math.Max(0, totalGb - availGb);
        double pct = totalGb > 0 ? (usedGb / totalGb) * 100.0 : 0;

        return (usedGb, totalGb, pct);
    }

    public HealthReport CollectHealthReport(long detectedJunkBytes, int enabledStartupCount, int activeTweaksCount, int totalTweaksCount)
    {
        var (ramUsedGb, ramTotalGb, ramPct) = GetMemorySnapshot();

        double diskFreeGb = 0;
        double diskTotalGb = 0;
        double diskUsedPct = 0;
        try
        {
            string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(sysRoot);
            if (drive.IsReady)
            {
                diskTotalGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                diskFreeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                diskUsedPct = diskTotalGb > 0 ? ((diskTotalGb - diskFreeGb) / diskTotalGb) * 100.0 : 0;
            }
        }
        catch { }

        int processCount = 0;
        try
        {
            processCount = Process.GetProcesses().Length;
        }
        catch { }

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        string uptimeText = uptime.Days > 0
            ? $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m"
            : $"{uptime.Hours}h {uptime.Minutes}m";

        int score = CalculateFreshnessScore(ramPct, detectedJunkBytes, enabledStartupCount, activeTweaksCount, totalTweaksCount);
        var (label, color) = score switch
        {
            >= 85 => ("Fresh & Snappy", "#10B981"),
            >= 65 => ("Good Condition", "#38BDF8"),
            >= 45 => ("Needs Tune-Up", "#F59E0B"),
            _ => ("Heavy Clutter", "#EF4444")
        };

        return new HealthReport
        {
            RamUsedGb = ramUsedGb,
            RamTotalGb = ramTotalGb,
            RamUsagePercent = ramPct,
            DiskFreeGb = diskFreeGb,
            DiskTotalGb = diskTotalGb,
            DiskUsedPercent = diskUsedPct,
            ActiveProcesses = processCount,
            UptimeFormatted = uptimeText,
            FreshnessScore = score,
            FreshnessLabel = label,
            FreshnessColorHex = color
        };
    }

    private static int CalculateFreshnessScore(double ramPercent, long junkBytes, int enabledStartupCount, int activeTweaks, int totalTweaks)
    {
        double score = 100.0;

        double junkGb = junkBytes / (1024.0 * 1024.0 * 1024.0);
        score -= Math.Min(30.0, junkGb * 6.0);

        if (ramPercent > 50.0)
            score -= Math.Min(25.0, (ramPercent - 50.0) * 0.55);

        if (enabledStartupCount > 4)
            score -= Math.Min(25.0, (enabledStartupCount - 4) * 3.0);

        int applicable = Math.Max(1, totalTweaks - 1);
        double tweakRatio = Math.Clamp((double)activeTweaks / applicable, 0.0, 1.0);
        score -= (1.0 - tweakRatio) * 20.0;

        return (int)Math.Clamp(Math.Round(score), 0, 100);
    }
}
