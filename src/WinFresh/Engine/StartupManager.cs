using System.IO;
using Microsoft.Win32;
using WinFresh.Models;

namespace WinFresh.Engine;

public sealed class StartupManager
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DisabledBackupSubKey = @"Software\WinFresh\DisabledStartup";

    private static readonly string DisabledFolderDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinFresh",
        "DisabledStartupFiles");

    private static readonly string[] HighImpactKeywords =
    {
        "steam", "discord", "teams", "onedrive", "adobe", "epicgames", "spotify",
        "slack", "docker", "chrome", "msedge", "riot", "battle.net", "origin", "ea", "zoom"
    };

    private static readonly string[] MediumImpactKeywords =
    {
        "update", "helper", "sync", "cloud", "tray", "nvidia", "razer", "logi", "corsair", "icue"
    };

    public Task<List<StartupEntry>> LoadStartupEntriesAsync()
    {
        return Task.Run(() =>
        {
            var list = new List<StartupEntry>();

            ReadRegistryRunKey(Registry.CurrentUser, RunSubKey, "HKCU", "HKCU\\...\\Run", isEnabled: true, list);
            ReadRegistryRunKey(Registry.LocalMachine, RunSubKey, "HKLM", "HKLM\\...\\Run", isEnabled: true, list);

            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            ReadStartupFolder(startupFolder, isEnabled: true, list);

            ReadDisabledRegistryBackups(list);
            ReadStartupFolder(DisabledFolderDir, isEnabled: false, list);

            return list
                .OrderByDescending(x => x.IsEnabled)
                .ThenBy(x => x.Impact == "High" ? 0 : x.Impact == "Medium" ? 1 : 2)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });
    }

    public Task<bool> SetStartupEnabledAsync(StartupEntry entry, bool enable)
    {
        return Task.Run(() =>
        {
            try
            {
                if (entry.SourceType == "FOLDER")
                {
                    Directory.CreateDirectory(DisabledFolderDir);
                    string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                    string fileName = Path.GetFileName(entry.FilePath);
                    string activePath = Path.Combine(startupFolder, fileName);
                    string backupPath = Path.Combine(DisabledFolderDir, fileName);

                    if (enable)
                    {
                        if (File.Exists(backupPath))
                            File.Move(backupPath, activePath, overwrite: true);
                    }
                    else
                    {
                        if (File.Exists(activePath))
                            File.Move(activePath, backupPath, overwrite: true);
                    }

                    entry.IsEnabled = enable;
                    return true;
                }

                RegistryKey hive = entry.SourceType == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                string backupValueKey = $"{entry.SourceType}|{entry.ValueName}";

                if (enable)
                {
                    using var backupKey = Registry.CurrentUser.CreateSubKey(DisabledBackupSubKey);
                    string? savedCmd = backupKey?.GetValue(backupValueKey)?.ToString();
                    string commandToRestore = !string.IsNullOrWhiteSpace(savedCmd) ? savedCmd : entry.Command;

                    using var runKey = hive.CreateSubKey(RunSubKey);
                    runKey?.SetValue(entry.ValueName, commandToRestore, RegistryValueKind.String);
                    backupKey?.DeleteValue(backupValueKey, throwOnMissingValue: false);
                }
                else
                {
                    using var runKey = hive.OpenSubKey(RunSubKey, writable: true);
                    string? existingCmd = runKey?.GetValue(entry.ValueName)?.ToString() ?? entry.Command;

                    using var backupKey = Registry.CurrentUser.CreateSubKey(DisabledBackupSubKey);
                    backupKey?.SetValue(backupValueKey, existingCmd, RegistryValueKind.String);

                    runKey?.DeleteValue(entry.ValueName, throwOnMissingValue: false);
                }

                entry.IsEnabled = enable;
                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    private static void ReadRegistryRunKey(
        RegistryKey hive,
        string subKey,
        string sourceType,
        string locationLabel,
        bool isEnabled,
        List<StartupEntry> list)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey, writable: false);
            if (key == null) return;

            foreach (string valueName in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName)) continue;
                string cmd = key.GetValue(valueName)?.ToString() ?? string.Empty;
                var (impact, color) = EstimateImpact(valueName, cmd);

                list.Add(new StartupEntry
                {
                    Id = $"{sourceType}:{valueName}",
                    Name = valueName,
                    Command = cmd,
                    Location = locationLabel,
                    SourceType = sourceType,
                    ValueName = valueName,
                    Impact = impact,
                    ImpactColorHex = color,
                    IsEnabled = isEnabled
                });
            }
        }
        catch { }
    }

    private static void ReadDisabledRegistryBackups(List<StartupEntry> list)
    {
        try
        {
            using var backupKey = Registry.CurrentUser.OpenSubKey(DisabledBackupSubKey, writable: false);
            if (backupKey == null) return;

            foreach (string encodedName in backupKey.GetValueNames())
            {
                int sep = encodedName.IndexOf('|');
                if (sep <= 0 || sep >= encodedName.Length - 1) continue;

                string sourceType = encodedName[..sep];
                string valueName = encodedName[(sep + 1)..];
                string cmd = backupKey.GetValue(encodedName)?.ToString() ?? string.Empty;
                var (impact, color) = EstimateImpact(valueName, cmd);

                list.Add(new StartupEntry
                {
                    Id = $"{sourceType}:{valueName}",
                    Name = valueName,
                    Command = cmd,
                    Location = sourceType == "HKLM" ? "HKLM\\...\\Run (Paused)" : "HKCU\\...\\Run (Paused)",
                    SourceType = sourceType,
                    ValueName = valueName,
                    Impact = impact,
                    ImpactColorHex = color,
                    IsEnabled = false
                });
            }
        }
        catch { }
    }

    private static void ReadStartupFolder(string folder, bool isEnabled, List<StartupEntry> list)
    {
        if (!Directory.Exists(folder)) return;
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Equals("desktop", StringComparison.OrdinalIgnoreCase)) continue;

                var (impact, color) = EstimateImpact(name, file);
                list.Add(new StartupEntry
                {
                    Id = $"FOLDER:{Path.GetFileName(file)}",
                    Name = name,
                    Command = file,
                    Location = isEnabled ? "Startup Folder" : "Startup Folder (Paused)",
                    SourceType = "FOLDER",
                    ValueName = Path.GetFileName(file),
                    FilePath = file,
                    Impact = impact,
                    ImpactColorHex = color,
                    IsEnabled = isEnabled
                });
            }
        }
        catch { }
    }

    private static (string Impact, string ColorHex) EstimateImpact(string name, string command)
    {
        string combined = $"{name} {command}".ToLowerInvariant();
        if (HighImpactKeywords.Any(k => combined.Contains(k)))
            return ("High", "#EF4444");
        if (MediumImpactKeywords.Any(k => combined.Contains(k)))
            return ("Medium", "#F59E0B");
        return ("Low", "#10B981");
    }
}
