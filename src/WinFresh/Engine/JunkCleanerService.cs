using System.IO;
using System.Runtime.InteropServices;
using WinFresh.Models;

namespace WinFresh.Engine;

public sealed class JunkCleanerService
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    public List<CleanItem> BuildDefaultCategories()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        return new List<CleanItem>
        {
            new()
            {
                Id = "user_temp",
                Name = "User Temporary Files",
                Description = "Leftover installer and app scratch files in %TEMP%.",
                SafetyNote = "100% Safe • In-use files are skipped automatically",
                TargetPaths = new List<string> { Path.GetTempPath() }
            },
            new()
            {
                Id = "win_temp",
                Name = "Windows System Temp",
                Description = "OS-level temporary build and setup files in C:\\Windows\\Temp.",
                SafetyNote = "100% Safe • Requires Admin (granted)",
                TargetPaths = new List<string> { Path.Combine(winDir, "Temp") }
            },
            new()
            {
                Id = "wu_cache",
                Name = "Windows Update Download Cache",
                Description = "Already-installed update packages cached in SoftwareDistribution\\Download.",
                SafetyNote = "100% Safe • Windows re-downloads only if needed",
                TargetPaths = new List<string> { Path.Combine(winDir, "SoftwareDistribution", "Download") }
            },
            new()
            {
                Id = "do_cache",
                Name = "Delivery Optimization Cache",
                Description = "Peer-to-peer Windows Update & Store distribution fragments.",
                SafetyNote = "100% Safe • Frees multi-GB cache safely",
                TargetPaths = new List<string> { Path.Combine(winDir, "SoftwareDistribution", "DeliveryOptimization") }
            },
            new()
            {
                Id = "browser_cache",
                Name = "Browser Disk Caches (Chrome, Edge, Brave)",
                Description = "Temporary web images & script caches only (Cache_Data & Code Cache).",
                SafetyNote = "NEVER touches Cookies, Passwords, Bookmarks, or History",
                TargetPaths = ResolveBrowserCachePaths(localAppData)
            },
            new()
            {
                Id = "shader_cache",
                Name = "DirectX & GPU Shader Cache",
                Description = "Compiled Direct3D shader cache files in %LocalAppData%\\D3DSCache.",
                SafetyNote = "100% Safe • Rebuilt automatically by games & apps",
                TargetPaths = new List<string> { Path.Combine(localAppData, "D3DSCache") }
            },
            new()
            {
                Id = "crash_dumps",
                Name = "Crash Dumps & Error Reports",
                Description = "Old minidumps and Windows Error Reporting (WER) diagnostic archives.",
                SafetyNote = "100% Safe • Diagnostic leftovers only",
                TargetPaths = new List<string>
                {
                    Path.Combine(localAppData, "CrashDumps"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue")
                }
            },
            new()
            {
                Id = "recycle_bin",
                Name = "Windows Recycle Bin",
                Description = "Permanently clears files already moved to the Recycle Bin.",
                SafetyNote = "Uncheck if you still need deleted files",
                IsRecycleBin = true,
                IsSelected = true
            }
        };
    }

    private static List<string> ResolveBrowserCachePaths(string localAppData)
    {
        var results = new List<string>();
        string[] userDataRoots =
        {
            Path.Combine(localAppData, "Google", "Chrome", "User Data"),
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
            Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data")
        };

        foreach (string root in userDataRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var profileDirs = Directory.EnumerateDirectories(root)
                    .Where(d =>
                    {
                        string name = Path.GetFileName(d);
                        return name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
                    });

                foreach (string profile in profileDirs)
                {
                    string cacheData = Path.Combine(profile, "Cache", "Cache_Data");
                    string codeCache = Path.Combine(profile, "Code Cache");
                    if (Directory.Exists(cacheData)) results.Add(cacheData);
                    if (Directory.Exists(codeCache)) results.Add(codeCache);
                }
            }
            catch { }
        }

        return results;
    }

    public Task ScanAllAsync(
        IList<CleanItem> items,
        IProgress<(int Percent, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            int total = Math.Max(1, items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = items[i];
                progress?.Report(((int)((i / (double)total) * 100), $"Scanning {item.Name}..."));

                if (item.IsRecycleBin)
                {
                    var rbInfo = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                    int hr = SHQueryRecycleBin(null, ref rbInfo);
                    if (hr == 0)
                    {
                        item.SizeBytes = Math.Max(0, rbInfo.i64Size);
                        item.FileCount = (int)Math.Clamp(rbInfo.i64NumItems, 0, int.MaxValue);
                        item.StatusText = item.SizeBytes > 0 ? "Ready to empty" : "Already empty";
                    }
                    else
                    {
                        item.SizeBytes = 0;
                        item.FileCount = 0;
                        item.StatusText = "Empty";
                    }
                    continue;
                }

                long totalBytes = 0;
                int fileCount = 0;

                foreach (string path in item.TargetPaths)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!Directory.Exists(path)) continue;
                    ScanDirectoryRecursive(path, ref totalBytes, ref fileCount, ct);
                }

                item.SizeBytes = totalBytes;
                item.FileCount = fileCount;
                item.StatusText = totalBytes > 0 ? "Ready to clean" : "Clean";
            }

            progress?.Report((100, "Scan complete"));
        }, ct);
    }

    public Task<CleanResult> CleanSelectedAsync(
        IEnumerable<CleanItem> items,
        IProgress<(int Percent, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var selected = items.Where(x => x.IsSelected).ToList();
            int total = Math.Max(1, selected.Count);
            long totalFreed = 0;
            int totalDeleted = 0;
            int totalSkipped = 0;

            for (int i = 0; i < selected.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = selected[i];
                progress?.Report(((int)((i / (double)total) * 100), $"Cleaning {item.Name}..."));

                if (item.IsRecycleBin)
                {
                    long before = item.SizeBytes;
                    uint res = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                    if (res == 0 || before == 0)
                    {
                        totalFreed += before;
                        totalDeleted += item.FileCount;
                        item.SizeBytes = 0;
                        item.FileCount = 0;
                        item.StatusText = "Emptied";
                    }
                    else
                    {
                        item.StatusText = "Already empty";
                    }
                    continue;
                }

                long catFreed = 0;
                int catDeleted = 0;
                int catSkipped = 0;

                foreach (string path in item.TargetPaths)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!Directory.Exists(path)) continue;
                    CleanDirectoryRecursive(path, deleteRoot: false, ref catFreed, ref catDeleted, ref catSkipped, ct);
                }

                totalFreed += catFreed;
                totalDeleted += catDeleted;
                totalSkipped += catSkipped;

                long remBytes = 0;
                int remFiles = 0;
                foreach (string path in item.TargetPaths)
                {
                    if (Directory.Exists(path))
                        ScanDirectoryRecursive(path, ref remBytes, ref remFiles, ct);
                }

                item.SizeBytes = remBytes;
                item.FileCount = remFiles;
                item.StatusText = catSkipped > 0
                    ? $"Cleaned {CleanItem.FormatBytes(catFreed)} ({catSkipped} in-use skipped)"
                    : $"Cleaned {CleanItem.FormatBytes(catFreed)}";
            }

            progress?.Report((100, $"Cleaned {CleanItem.FormatBytes(totalFreed)}"));
            return new CleanResult
            {
                BytesFreed = totalFreed,
                FilesDeleted = totalDeleted,
                FilesSkipped = totalSkipped
            };
        }, ct);
    }

    private static void ScanDirectoryRecursive(string dir, ref long bytes, ref int count, CancellationToken ct)
    {
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", opts))
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var fi = new FileInfo(file);
                    bytes += fi.Length;
                    count++;
                }
                catch { }
            }

            foreach (string subDir in Directory.EnumerateDirectories(dir, "*", opts))
            {
                if (ct.IsCancellationRequested) return;
                ScanDirectoryRecursive(subDir, ref bytes, ref count, ct);
            }
        }
        catch { }
    }

    private static void CleanDirectoryRecursive(
        string dir,
        bool deleteRoot,
        ref long freedBytes,
        ref int deletedFiles,
        ref int skippedFiles,
        CancellationToken ct)
    {
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", opts))
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var fi = new FileInfo(file);
                    long len = fi.Length;
                    if (fi.IsReadOnly) fi.IsReadOnly = false;
                    fi.Delete();
                    freedBytes += len;
                    deletedFiles++;
                }
                catch
                {
                    skippedFiles++;
                }
            }

            foreach (string subDir in Directory.EnumerateDirectories(dir, "*", opts))
            {
                if (ct.IsCancellationRequested) return;
                CleanDirectoryRecursive(subDir, deleteRoot: true, ref freedBytes, ref deletedFiles, ref skippedFiles, ct);
            }

            if (deleteRoot)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir, false);
                }
                catch { }
            }
        }
        catch { }
    }
}
