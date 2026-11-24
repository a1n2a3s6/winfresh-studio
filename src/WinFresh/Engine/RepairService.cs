using System.Diagnostics;
using System.IO;
using System.Text;

namespace WinFresh.Engine;

public sealed class RepairService
{
    public Task<int> CreateRestorePointAsync(Action<string> onOutput, CancellationToken ct = default)
    {
        onOutput("[WinFresh] Requesting Windows System Restore Point...");
        string psScript =
            "Enable-ComputerRestore -Drive 'C:\\' -ErrorAction SilentlyContinue; " +
            "Checkpoint-Computer -Description 'WinFresh Studio Safe Restore Point' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop; " +
            "Write-Output 'Restore Point created successfully.'";

        return StreamProcessAsync(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript}\"",
            onOutput,
            ct);
    }

    public Task<int> OptimizeSsdTrimAsync(Action<string> onOutput, CancellationToken ct = default)
    {
        onOutput("[WinFresh] Running SSD ReTrim optimization on system drive C:...");
        return StreamProcessAsync(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"Optimize-Volume -DriveLetter C -ReTrim -Verbose\"",
            onOutput,
            ct);
    }

    public Task<int> RunSfcScanAsync(Action<string> onOutput, CancellationToken ct = default)
    {
        onOutput("[WinFresh] Starting System File Checker (sfc /scannow). This may take several minutes...");
        return StreamProcessAsync("sfc.exe", "/scannow", onOutput, ct, useUnicodeOutput: true);
    }

    public Task<int> RunDismRestoreHealthAsync(Action<string> onOutput, CancellationToken ct = default)
    {
        onOutput("[WinFresh] Starting Windows Image Repair (DISM /Online /Cleanup-Image /RestoreHealth)...");
        return StreamProcessAsync("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", onOutput, ct);
    }

    public async Task<int> RebuildIconAndExplorerCacheAsync(Action<string> onOutput, CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            onOutput("[WinFresh] Stopping File Explorer gracefully...");
            foreach (var proc in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
                catch { }
                finally { proc.Dispose(); }
            }

            await Task.Delay(600, ct);

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string explorerCacheDir = Path.Combine(localAppData, "Microsoft", "Windows", "Explorer");
            int removed = 0;

            string legacyIconDb = Path.Combine(localAppData, "IconCache.db");
            if (File.Exists(legacyIconDb))
            {
                try
                {
                    File.SetAttributes(legacyIconDb, FileAttributes.Normal);
                    File.Delete(legacyIconDb);
                    removed++;
                    onOutput($"Deleted {legacyIconDb}");
                }
                catch (Exception ex)
                {
                    onOutput($"Skipped IconCache.db: {ex.Message}");
                }
            }

            if (Directory.Exists(explorerCacheDir))
            {
                foreach (string pattern in new[] { "iconcache_*.db", "thumbcache_*.db" })
                {
                    foreach (string file in Directory.GetFiles(explorerCacheDir, pattern))
                    {
                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                            removed++;
                            onOutput($"Cleared {Path.GetFileName(file)}");
                        }
                        catch
                        {
                            onOutput($"Skipped locked file: {Path.GetFileName(file)}");
                        }
                    }
                }
            }

            onOutput($"[WinFresh] Removed {removed} cache databases. Restarting File Explorer...");
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            onOutput("[WinFresh] Explorer & Icon Cache rebuilt successfully.");
            return 0;
        }, ct);
    }

    private static async Task<int> StreamProcessAsync(
        string fileName,
        string arguments,
        Action<string> onOutput,
        CancellationToken ct,
        bool useUnicodeOutput = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = useUnicodeOutput ? Encoding.Unicode : Encoding.UTF8,
            StandardErrorEncoding = useUnicodeOutput ? Encoding.Unicode : Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                onOutput(e.Data.Trim());
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                onOutput($"[stderr] {e.Data.Trim()}");
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var reg = ct.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
            });

            await process.WaitForExitAsync(ct);
            onOutput($"[WinFresh] Process exited with code {process.ExitCode}.");
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            onOutput("[WinFresh] Operation cancelled by user.");
            return -1;
        }
        catch (Exception ex)
        {
            onOutput($"[WinFresh] Execution error: {ex.Message}");
            return -1;
        }
    }
}
