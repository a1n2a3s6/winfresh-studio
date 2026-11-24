using System.Diagnostics;
using System.Runtime.InteropServices;
using WinFresh.Models;

namespace WinFresh.Engine;

public sealed class RamOptimizer
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_SET_QUOTA = 0x0100;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "smss", "csrss", "wininit", "services",
        "lsass", "winlogon", "svchost", "dwm", "fontdrvhost", "sihost",
        "Memory Compression", "explorer", "SecurityHealthService", "MsMpEng", "WinFresh"
    };

    private readonly TelemetryService _telemetry;

    public RamOptimizer(TelemetryService telemetry)
    {
        _telemetry = telemetry;
    }

    public Task<List<ProcessMemoryItem>> GetTopMemoryProcessesAsync(int count = 12)
    {
        return Task.Run(() =>
        {
            int currentPid = Environment.ProcessId;
            var items = new List<ProcessMemoryItem>();

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    long ws = proc.WorkingSet64;
                    bool isProtected = proc.Id <= 4
                        || proc.Id == currentPid
                        || ProtectedProcessNames.Contains(proc.ProcessName);

                    items.Add(new ProcessMemoryItem
                    {
                        Pid = proc.Id,
                        Name = proc.ProcessName,
                        MemoryMb = ws / (1024.0 * 1024.0),
                        IsProtected = isProtected
                    });
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }

            return items
                .OrderByDescending(p => p.MemoryMb)
                .Take(count)
                .ToList();
        });
    }

    public (bool Success, string Message) EndProcessSafe(ProcessMemoryItem item)
    {
        if (item.IsProtected || item.Pid <= 4 || item.Pid == Environment.ProcessId)
            return (false, $"{item.Name} is a protected Windows process and cannot be terminated.");

        try
        {
            using var proc = Process.GetProcessById(item.Pid);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(3000);
            return (true, $"Ended {item.Name} (PID {item.Pid}) cleanly.");
        }
        catch (Exception ex)
        {
            return (false, $"Could not end {item.Name}: {ex.Message}");
        }
    }

    public async Task<RamTrimResult> TrimRamAsync()
    {
        return await Task.Run(async () =>
        {
            var before = _telemetry.GetMemorySnapshot();
            int trimmedCount = 0;
            int currentPid = Environment.ProcessId;

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.Id <= 4 || proc.Id == currentPid) continue;
                    if (ProtectedProcessNames.Contains(proc.ProcessName)) continue;

                    IntPtr handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, proc.Id);
                    if (handle != IntPtr.Zero)
                    {
                        try
                        {
                            if (EmptyWorkingSet(handle))
                                trimmedCount++;
                        }
                        finally
                        {
                            CloseHandle(handle);
                        }
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }

            await Task.Delay(300);
            var after = _telemetry.GetMemorySnapshot();
            double freedMb = Math.Max(0, (before.UsedGb - after.UsedGb) * 1024.0);

            return new RamTrimResult
            {
                MemoryFreedMb = freedMb,
                ProcessesTrimmed = trimmedCount,
                RamBeforeGb = before.UsedGb,
                RamAfterGb = after.UsedGb
            };
        });
    }
}
