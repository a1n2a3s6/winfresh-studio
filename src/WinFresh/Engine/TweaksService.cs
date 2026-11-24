using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinFresh.Models;

namespace WinFresh.Engine;

public sealed class TweaksService
{
    private const string UltimatePerfTemplateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private static readonly string BackupDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinFresh");

    public static string BackupFilePath => Path.Combine(BackupDir, "undo-backup.json");

    private static readonly string[] ContentDeliveryKeys =
    {
        "SubscribedContent-338388Enabled",
        "SubscribedContent-338389Enabled",
        "SubscribedContent-353694Enabled",
        "SubscribedContent-353696Enabled",
        "SoftLandingEnabled",
        "SystemPaneSuggestionsEnabled",
        "SilentInstalledAppsEnabled"
    };

    public List<TweakDefinition> CreateDefinitions() => new()
    {
        new()
        {
            Id = "disable_bing",
            Title = "Disable Bing Web Search in Start Menu",
            Description = "Stops Start Menu from querying Bing online on every keystroke so local app & file search feels instant.",
            ImpactBadge = "Instant Search",
            RecommendedForBoost = true
        },
        new()
        {
            Id = "no_startup_delay",
            Title = "Remove Startup App Launch Delay",
            Description = "Sets StartupDelayInMSec = 0 so Windows launches your desktop and startup apps immediately after sign-in.",
            ImpactBadge = "Faster Boot",
            RecommendedForBoost = true
        },
        new()
        {
            Id = "ultimate_power",
            Title = "Unlock & Activate Ultimate Performance Power Plan",
            Description = "Unlocks Microsoft's hidden workstation power scheme to eliminate micro-latencies and CPU parking lag.",
            ImpactBadge = "Max CPU Response",
            RecommendedForBoost = false
        },
        new()
        {
            Id = "disable_telemetry",
            Title = "Disable Windows Telemetry & Diagnostics",
            Description = "Sets AllowTelemetry = 0 and stops the background DiagTrack service to cut background disk & network chatter.",
            ImpactBadge = "Privacy + CPU",
            RecommendedForBoost = true
        },
        new()
        {
            Id = "disable_tips_ads",
            Title = "Disable Background Tips, Ads & Consumer Bloat",
            Description = "Disables ContentDeliveryManager suggestions, lock-screen tips, and silent promoted app installs.",
            ImpactBadge = "Zero Bloat",
            RecommendedForBoost = true
        },
        new()
        {
            Id = "snappy_animations",
            Title = "Snappy Window Animations (Keep Sharp Fonts)",
            Description = "Removes sluggish window minimize/maximize fade delays and lowers menu delay while keeping ClearType crisp.",
            ImpactBadge = "UI Snappiness",
            RecommendedForBoost = true
        },
        new()
        {
            Id = "disable_gamedvr",
            Title = "Disable GameDVR / Background Recording Overhead",
            Description = "Turns off always-on Xbox Game Bar background video capture to improve gaming FPS and reduce frame-time spikes.",
            ImpactBadge = "Gaming FPS",
            RecommendedForBoost = true
        },
        new()
        {
            Id = "flush_dns",
            Title = "Flush DNS & Reset Network Resolver Cache",
            Description = "Clears stale DNS records via ipconfig /flushdns to fix sluggish page loads and broken lookups.",
            ImpactBadge = "Network",
            RecommendedForBoost = true
        }
    };

    public Task RefreshStatesAsync(IList<TweakDefinition> tweaks)
    {
        return Task.Run(() =>
        {
            foreach (var tweak in tweaks)
                tweak.IsEnabled = DetectTweakState(tweak.Id);
        });
    }

    public Task<bool> ApplyTweakAsync(TweakDefinition tweak, bool enable)
    {
        return Task.Run(() =>
        {
            try
            {
                var snapshot = LoadOrCreateSnapshot();

                switch (tweak.Id)
                {
                    case "disable_bing":
                        BackupAndSet(snapshot, "HKCU", @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", enable ? 1 : 0, RegistryValueKind.DWord);
                        BackupAndSet(snapshot, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", enable ? 0 : 1, RegistryValueKind.DWord);
                        break;

                    case "no_startup_delay":
                        if (enable)
                        {
                            BackupAndSet(snapshot, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0, RegistryValueKind.DWord);
                        }
                        else
                        {
                            RestoreSpecificKey(snapshot, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", defaultDeleteIfMissing: true);
                        }
                        break;

                    case "ultimate_power":
                        ApplyUltimatePowerPlan(snapshot, enable);
                        break;

                    case "disable_telemetry":
                        BackupAndSet(snapshot, "HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", enable ? 0 : 1, RegistryValueKind.DWord);
                        if (string.IsNullOrEmpty(snapshot.PreviousDiagTrackStartType))
                            snapshot.PreviousDiagTrackStartType = "auto";
                        if (enable)
                        {
                            RunProcessCapture("sc.exe", "config DiagTrack start= disabled");
                            RunProcessCapture("sc.exe", "stop DiagTrack");
                        }
                        else
                        {
                            RunProcessCapture("sc.exe", $"config DiagTrack start= {snapshot.PreviousDiagTrackStartType}");
                            RunProcessCapture("sc.exe", "start DiagTrack");
                        }
                        break;

                    case "disable_tips_ads":
                        foreach (string keyName in ContentDeliveryKeys)
                            BackupAndSet(snapshot, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", keyName, enable ? 0 : 1, RegistryValueKind.DWord);
                        break;

                    case "snappy_animations":
                        BackupAndSet(snapshot, "HKCU", @"Control Panel\Desktop\WindowMetrics", "MinAnimate", enable ? "0" : "1", RegistryValueKind.String);
                        BackupAndSet(snapshot, "HKCU", @"Control Panel\Desktop", "MenuShowDelay", enable ? "20" : "400", RegistryValueKind.String);
                        BackupAndSet(snapshot, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", enable ? 0 : 1, RegistryValueKind.DWord);
                        break;

                    case "disable_gamedvr":
                        BackupAndSet(snapshot, "HKCU", @"System\GameConfigStore", "GameDVR_Enabled", enable ? 0 : 1, RegistryValueKind.DWord);
                        BackupAndSet(snapshot, "HKLM", @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", enable ? 0 : 1, RegistryValueKind.DWord);
                        break;

                    case "flush_dns":
                        RunProcessCapture("ipconfig.exe", "/flushdns");
                        tweak.StatusMessage = $"Flushed at {DateTime.Now:HH:mm:ss}";
                        tweak.IsEnabled = true;
                        SaveSnapshot(snapshot);
                        return true;
                }

                SaveSnapshot(snapshot);
                tweak.IsEnabled = enable;
                tweak.StatusMessage = enable ? "Applied & backed up" : "Reverted";
                return true;
            }
            catch (Exception ex)
            {
                tweak.StatusMessage = $"Error: {ex.Message}";
                return false;
            }
        });
    }

    public Task<(int RestoredCount, string Message)> UndoAllAsync(IList<TweakDefinition> tweaks)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(BackupFilePath))
                return (0, "No backup snapshot found yet. Apply a tweak first.");

            try
            {
                string json = File.ReadAllText(BackupFilePath);
                var snapshot = JsonSerializer.Deserialize<UndoSnapshot>(json);
                if (snapshot == null)
                    return (0, "Backup file was empty.");

                int restored = 0;

                foreach (var record in snapshot.RegistryBackups.Values)
                {
                    try
                    {
                        RegistryKey root = record.Hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                        if (!record.Existed)
                        {
                            using var key = root.OpenSubKey(record.SubKey, writable: true);
                            key?.DeleteValue(record.ValueName, throwOnMissingValue: false);
                        }
                        else
                        {
                            using var key = root.CreateSubKey(record.SubKey);
                            if (key != null)
                            {
                                object val = DeserializeRegValue(record.ValueKind, record.SerializedValue);
                                key.SetValue(record.ValueName, val, record.ValueKind);
                            }
                        }
                        restored++;
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(snapshot.PreviousPowerSchemeGuid))
                {
                    RunProcessCapture("powercfg.exe", $"/setactive {snapshot.PreviousPowerSchemeGuid}");
                    restored++;
                }

                if (!string.IsNullOrWhiteSpace(snapshot.PreviousDiagTrackStartType))
                {
                    RunProcessCapture("sc.exe", $"config DiagTrack start= {snapshot.PreviousDiagTrackStartType}");
                    RunProcessCapture("sc.exe", "start DiagTrack");
                    restored++;
                }

                foreach (var tweak in tweaks)
                {
                    tweak.IsEnabled = DetectTweakState(tweak.Id);
                    tweak.StatusMessage = "Restored to default";
                }

                return (restored, $"Successfully restored {restored} original Windows settings.");
            }
            catch (Exception ex)
            {
                return (0, $"Undo failed: {ex.Message}");
            }
        });
    }

    private bool DetectTweakState(string id)
    {
        try
        {
            return id switch
            {
                "disable_bing" => ReadDword(Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions") == 1,
                "no_startup_delay" => ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec") == 0,
                "ultimate_power" => IsUltimatePerformanceActive(),
                "disable_telemetry" => ReadDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry") == 0,
                "disable_tips_ads" => ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled") == 0,
                "snappy_animations" => ReadString(Registry.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate") == "0",
                "disable_gamedvr" => ReadDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled") == 0,
                "flush_dns" => false,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private void ApplyUltimatePowerPlan(UndoSnapshot snapshot, bool enable)
    {
        if (string.IsNullOrWhiteSpace(snapshot.PreviousPowerSchemeGuid))
            snapshot.PreviousPowerSchemeGuid = GetActivePowerSchemeGuid() ?? "381b4222-f694-41f0-9685-ff5bb260df2e";

        if (!enable)
        {
            string fallback = snapshot.PreviousPowerSchemeGuid ?? "381b4222-f694-41f0-9685-ff5bb260df2e";
            RunProcessCapture("powercfg.exe", $"/setactive {fallback}");
            return;
        }

        string existing = FindPowerSchemeByName("Ultimate Performance");
        if (string.IsNullOrEmpty(existing))
        {
            string dupOutput = RunProcessCapture("powercfg.exe", $"-duplicatescheme {UltimatePerfTemplateGuid}");
            existing = ExtractFirstGuid(dupOutput);
        }

        if (!string.IsNullOrEmpty(existing))
            RunProcessCapture("powercfg.exe", $"/setactive {existing}");
    }

    private bool IsUltimatePerformanceActive()
    {
        string output = RunProcessCapture("powercfg.exe", "/getactivescheme");
        return output.Contains("Ultimate Performance", StringComparison.OrdinalIgnoreCase);
    }

    private string? GetActivePowerSchemeGuid()
    {
        string output = RunProcessCapture("powercfg.exe", "/getactivescheme");
        string guid = ExtractFirstGuid(output);
        return string.IsNullOrEmpty(guid) ? null : guid;
    }

    private string FindPowerSchemeByName(string name)
    {
        string output = RunProcessCapture("powercfg.exe", "/list");
        foreach (string line in output.Split('\n'))
        {
            if (line.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                string guid = ExtractFirstGuid(line);
                if (!string.IsNullOrEmpty(guid)) return guid;
            }
        }
        return string.Empty;
    }

    private static string ExtractFirstGuid(string text)
    {
        var match = Regex.Match(text, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return match.Success ? match.Value : string.Empty;
    }

    private static void BackupAndSet(
        UndoSnapshot snapshot,
        string hiveName,
        string subKey,
        string valueName,
        object newValue,
        RegistryValueKind kind)
    {
        RegistryKey root = hiveName == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
        string lookupKey = $"{hiveName}\\{subKey}\\{valueName}";

        if (!snapshot.RegistryBackups.ContainsKey(lookupKey))
        {
            using var existingKey = root.OpenSubKey(subKey, writable: false);
            object? existingVal = existingKey?.GetValue(valueName);
            var record = new RegistryBackupRecord
            {
                Hive = hiveName,
                SubKey = subKey,
                ValueName = valueName,
                Existed = existingVal != null,
                ValueKind = existingVal != null && existingKey != null
                    ? existingKey.GetValueKind(valueName)
                    : kind,
                SerializedValue = existingVal?.ToString()
            };
            snapshot.RegistryBackups[lookupKey] = record;
        }

        using var writeKey = root.CreateSubKey(subKey);
        writeKey?.SetValue(valueName, newValue, kind);
    }

    private static void RestoreSpecificKey(
        UndoSnapshot snapshot,
        string hiveName,
        string subKey,
        string valueName,
        bool defaultDeleteIfMissing)
    {
        RegistryKey root = hiveName == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
        string lookupKey = $"{hiveName}\\{subKey}\\{valueName}";

        if (snapshot.RegistryBackups.TryGetValue(lookupKey, out var record) && record.Existed)
        {
            using var key = root.CreateSubKey(subKey);
            key?.SetValue(valueName, DeserializeRegValue(record.ValueKind, record.SerializedValue), record.ValueKind);
        }
        else if (defaultDeleteIfMissing)
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    private static object DeserializeRegValue(RegistryValueKind kind, string? raw)
    {
        return kind switch
        {
            RegistryValueKind.DWord => int.TryParse(raw, out int d) ? d : 0,
            RegistryValueKind.QWord => long.TryParse(raw, out long q) ? q : 0L,
            _ => raw ?? string.Empty
        };
    }

    private static int? ReadDword(RegistryKey root, string subKey, string valueName)
    {
        using var key = root.OpenSubKey(subKey, writable: false);
        object? val = key?.GetValue(valueName);
        return val is int i ? i : null;
    }

    private static string? ReadString(RegistryKey root, string subKey, string valueName)
    {
        using var key = root.OpenSubKey(subKey, writable: false);
        return key?.GetValue(valueName)?.ToString();
    }

    private static UndoSnapshot LoadOrCreateSnapshot()
    {
        try
        {
            if (File.Exists(BackupFilePath))
            {
                string json = File.ReadAllText(BackupFilePath);
                var existing = JsonSerializer.Deserialize<UndoSnapshot>(json);
                if (existing != null) return existing;
            }
        }
        catch { }

        return new UndoSnapshot();
    }

    private static void SaveSnapshot(UndoSnapshot snapshot)
    {
        Directory.CreateDirectory(BackupDir);
        string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(BackupFilePath, json);
    }

    private static string RunProcessCapture(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return string.Empty;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }
}
