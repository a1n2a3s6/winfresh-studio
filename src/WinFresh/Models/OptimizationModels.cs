using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace WinFresh.Models;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public sealed class HealthReport
{
    public double RamUsedGb { get; set; }
    public double RamTotalGb { get; set; }
    public double RamUsagePercent { get; set; }
    public double DiskFreeGb { get; set; }
    public double DiskTotalGb { get; set; }
    public double DiskUsedPercent { get; set; }
    public int ActiveProcesses { get; set; }
    public string UptimeFormatted { get; set; } = "0h 0m";
    public int FreshnessScore { get; set; }
    public string FreshnessLabel { get; set; } = "Analyzing...";
    public string FreshnessColorHex { get; set; } = "#10B981";
}

public sealed class CleanItem : ObservableObject
{
    private bool _isSelected = true;
    private int _fileCount;
    private long _sizeBytes;
    private string _statusText = "Ready to scan";

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SafetyNote { get; init; } = string.Empty;
    public bool IsRecycleBin { get; init; }
    public List<string> TargetPaths { get; init; } = new();

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public int FileCount
    {
        get => _fileCount;
        set
        {
            if (SetField(ref _fileCount, value))
                OnPropertyChanged(nameof(FileCountFormatted));
        }
    }

    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (SetField(ref _sizeBytes, value))
                OnPropertyChanged(nameof(SizeFormatted));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string FileCountFormatted => IsRecycleBin
        ? $"{FileCount:N0} items"
        : $"{FileCount:N0} files";

    public string SizeFormatted => FormatBytes(SizeBytes);

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double val = bytes;
        int idx = 0;
        while (val >= 1024 && idx < units.Length - 1)
        {
            val /= 1024.0;
            idx++;
        }
        return idx >= 3 ? $"{val:F2} {units[idx]}" : $"{val:F1} {units[idx]}";
    }
}

public sealed class StartupEntry : ObservableObject
{
    private bool _isEnabled;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string ValueName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Impact { get; init; } = "Low";
    public string ImpactColorHex { get; init; } = "#10B981";

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }
}

public sealed class TweakDefinition : ObservableObject
{
    private bool _isEnabled;
    private string _statusMessage = string.Empty;

    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ImpactBadge { get; init; } = "Safe";
    public bool RecommendedForBoost { get; init; } = true;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }
}

public sealed class RegistryBackupRecord
{
    public string Hive { get; set; } = "HKCU";
    public string SubKey { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;
    public bool Existed { get; set; }
    public RegistryValueKind ValueKind { get; set; } = RegistryValueKind.DWord;
    public string? SerializedValue { get; set; }
}

public sealed class UndoSnapshot
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? PreviousPowerSchemeGuid { get; set; }
    public string? PreviousDiagTrackStartType { get; set; }
    public Dictionary<string, RegistryBackupRecord> RegistryBackups { get; set; } = new();
}

public sealed class ProcessMemoryItem
{
    public int Pid { get; init; }
    public string Name { get; init; } = string.Empty;
    public double MemoryMb { get; init; }
    public string MemoryFormatted => $"{MemoryMb:F1} MB";
    public bool IsProtected { get; init; }
    public bool CanEndTask => !IsProtected;
    public string ActionLabel => IsProtected ? "Protected" : "End Task";
}

public sealed class CleanResult
{
    public long BytesFreed { get; init; }
    public int FilesDeleted { get; init; }
    public int FilesSkipped { get; init; }
}

public sealed class RamTrimResult
{
    public double MemoryFreedMb { get; init; }
    public int ProcessesTrimmed { get; init; }
    public double RamBeforeGb { get; init; }
    public double RamAfterGb { get; init; }
}
