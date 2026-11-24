using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinFresh.Engine;
using WinFresh.Models;

namespace WinFresh.Views;

public partial class MainWindow : Window
{
    private readonly TelemetryService _telemetry = new();
    private readonly JunkCleanerService _cleaner = new();
    private readonly StartupManager _startupManager = new();
    private readonly TweaksService _tweaksService = new();
    private readonly RamOptimizer _ramOptimizer;
    private readonly RepairService _repairService = new();

    private readonly ObservableCollection<CleanItem> _cleanItems = new();
    private readonly ObservableCollection<StartupEntry> _startupEntries = new();
    private readonly ObservableCollection<TweakDefinition> _tweaks = new();
    private readonly ObservableCollection<ProcessMemoryItem> _processes = new();

    private readonly DispatcherTimer _telemetryTimer;
    private CancellationTokenSource? _repairCts;
    private double _displayedScore;

    public MainWindow()
    {
        InitializeComponent();

        _ramOptimizer = new RamOptimizer(_telemetry);

        CleanerList.ItemsSource = _cleanItems;
        StartupList.ItemsSource = _startupEntries;
        TweaksList.ItemsSource = _tweaks;
        ProcessList.ItemsSource = _processes;

        foreach (var item in _cleaner.BuildDefaultCategories())
            _cleanItems.Add(item);

        foreach (var tweak in _tweaksService.CreateDefinitions())
            _tweaks.Add(tweak);

        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _telemetryTimer.Tick += (_, _) => UpdateTelemetryUi();

        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _telemetryTimer.Stop();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateTelemetryUi();
        _telemetryTimer.Start();

        await Task.WhenAll(
            LoadStartupAsync(),
            _tweaksService.RefreshStatesAsync(_tweaks),
            LoadProcessesAsync(),
            ScanJunkInternalAsync());

        UpdateTelemetryUi();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void NavTab_Checked(object sender, RoutedEventArgs e)
    {
        if (TabOverview == null || sender is not RadioButton rb) return;
        string tag = rb.Tag?.ToString() ?? "0";

        TabOverview.Visibility = tag == "0" ? Visibility.Visible : Visibility.Collapsed;
        TabCleaner.Visibility = tag == "1" ? Visibility.Visible : Visibility.Collapsed;
        TabStartup.Visibility = tag == "2" ? Visibility.Visible : Visibility.Collapsed;
        TabTweaks.Visibility = tag == "3" ? Visibility.Visible : Visibility.Collapsed;
        TabRam.Visibility = tag == "4" ? Visibility.Visible : Visibility.Collapsed;
        TabRepair.Visibility = tag == "5" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "4")
            _ = LoadProcessesAsync();
    }

    private void UpdateTelemetryUi()
    {
        long junkBytes = _cleanItems.Sum(x => x.SizeBytes);
        int enabledStartup = _startupEntries.Count(x => x.IsEnabled);
        int activeTweaks = _tweaks.Count(x => x.IsEnabled && x.Id != "flush_dns");

        var report = _telemetry.CollectHealthReport(junkBytes, enabledStartup, activeTweaks, _tweaks.Count);

        TxtKpiRam.Text = $"{report.RamUsedGb:F1} / {report.RamTotalGb:F1} GB";
        TxtKpiRamPct.Text = $"{report.RamUsagePercent:F0}% utilized";
        TxtRamBanner.Text = $"Memory in use: {report.RamUsedGb:F1} GB of {report.RamTotalGb:F1} GB ({report.RamUsagePercent:F0}%)";

        TxtKpiDisk.Text = $"{report.DiskFreeGb:F1} GB";
        TxtKpiDiskSub.Text = $"{report.DiskUsedPercent:F0}% of {report.DiskTotalGb:F0} GB used";

        TxtKpiProcs.Text = report.ActiveProcesses.ToString("N0");
        TxtKpiStartupSub.Text = $"{enabledStartup} active startup apps";

        TxtKpiUptime.Text = report.UptimeFormatted;
        TxtKpiJunkSub.Text = $"{CleanItem.FormatBytes(junkBytes)} junk detected";

        TxtFreshnessLabel.Text = report.FreshnessLabel;
        var brush = (Brush)new BrushConverter().ConvertFromString(report.FreshnessColorHex)!;
        TxtFreshnessLabel.Foreground = brush;
        ScoreArcPath.Stroke = brush;

        AnimateFreshnessGauge(report.FreshnessScore);
    }

    private async void AnimateFreshnessGauge(int targetScore)
    {
        double start = _displayedScore;
        const int steps = 16;
        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps;
            double eased = 1 - Math.Pow(1 - t, 3);
            double current = start + (targetScore - start) * eased;
            _displayedScore = current;

            TxtFreshnessScore.Text = Math.Round(current).ToString("0");
            DrawScoreArc(current);
            await Task.Delay(16);
        }
    }

    private void DrawScoreArc(double score)
    {
        double clamped = Math.Clamp(score, 0.5, 99.9);
        double angle = (clamped / 100.0) * 360.0;
        double radius = 78.0;
        double center = 88.0;

        double startRad = -Math.PI / 2.0;
        double endRad = startRad + angle * (Math.PI / 180.0);

        Point startPt = new(center + radius * Math.Cos(startRad), center + radius * Math.Sin(startRad));
        Point endPt = new(center + radius * Math.Cos(endRad), center + radius * Math.Sin(endRad));

        var figure = new PathFigure { StartPoint = startPt, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = endPt,
            Size = new Size(radius, radius),
            IsLargeArc = angle > 180.0,
            SweepDirection = SweepDirection.Clockwise
        });

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        ScoreArcPath.Data = geo;
    }

    private async void OneClickBoost_Click(object sender, RoutedEventArgs e)
    {
        BtnOneClickBoost.IsEnabled = false;
        BtnRescanOverview.IsEnabled = false;
        PbBoost.Value = 5;

        try
        {
            TxtBoostStatus.Text = "Step 1/4: Scanning & cleaning safe junk caches...";
            await ScanJunkInternalAsync();
            PbBoost.Value = 25;

            var cleanRes = await _cleaner.CleanSelectedAsync(_cleanItems);
            PbBoost.Value = 55;

            TxtBoostStatus.Text = "Step 2/4: Trimming idle RAM working sets...";
            var ramRes = await _ramOptimizer.TrimRamAsync();
            PbBoost.Value = 75;

            TxtBoostStatus.Text = "Step 3/4: Applying recommended reversible speed tweaks & DNS flush...";
            int appliedTweaks = 0;
            foreach (var tweak in _tweaks.Where(t => t.RecommendedForBoost))
            {
                if (!tweak.IsEnabled || tweak.Id == "flush_dns")
                {
                    if (await _tweaksService.ApplyTweakAsync(tweak, true))
                        appliedTweaks++;
                }
            }
            PbBoost.Value = 95;

            TxtBoostStatus.Text = "Step 4/4: Refreshing live freshness score...";
            await LoadProcessesAsync();
            UpdateTelemetryUi();
            PbBoost.Value = 100;

            string ramText = ramRes.MemoryFreedMb >= 1024
                ? $"{ramRes.MemoryFreedMb / 1024.0:F2} GB RAM"
                : $"{ramRes.MemoryFreedMb:F0} MB RAM";

            string summary = $"Freed {CleanItem.FormatBytes(cleanRes.BytesFreed)} • Freed {ramText} • Applied {appliedTweaks} Tweaks!";
            TxtBoostStatus.Text = "1-Click Fresh Boost completed successfully.";
            TxtBoostSummaryTitle.Text = "Fresh Boost Complete";
            TxtBoostSummaryBody.Text = summary;
            UpdateCleanerTotalText();
        }
        finally
        {
            BtnOneClickBoost.IsEnabled = true;
            BtnRescanOverview.IsEnabled = true;
        }
    }

    private async void RescanOverview_Click(object sender, RoutedEventArgs e)
    {
        BtnRescanOverview.IsEnabled = false;
        TxtBoostStatus.Text = "Rescanning system junk, startup apps & tweaks...";
        await Task.WhenAll(ScanJunkInternalAsync(), LoadStartupAsync(), _tweaksService.RefreshStatesAsync(_tweaks));
        UpdateTelemetryUi();
        TxtBoostStatus.Text = "System scan up to date.";
        BtnRescanOverview.IsEnabled = true;
    }

    private async void ScanJunk_Click(object sender, RoutedEventArgs e)
    {
        BtnScanJunk.IsEnabled = false;
        BtnCleanJunk.IsEnabled = false;
        await ScanJunkInternalAsync();
        UpdateTelemetryUi();
        BtnScanJunk.IsEnabled = true;
        BtnCleanJunk.IsEnabled = true;
    }

    private async Task ScanJunkInternalAsync()
    {
        var prog = new Progress<(int Percent, string Status)>(p => PbCleaner.Value = p.Percent);
        await _cleaner.ScanAllAsync(_cleanItems, prog);
        UpdateCleanerTotalText();
    }

    private async void CleanJunk_Click(object sender, RoutedEventArgs e)
    {
        BtnScanJunk.IsEnabled = false;
        BtnCleanJunk.IsEnabled = false;

        var prog = new Progress<(int Percent, string Status)>(p => PbCleaner.Value = p.Percent);
        var res = await _cleaner.CleanSelectedAsync(_cleanItems, prog);
        UpdateCleanerTotalText();
        UpdateTelemetryUi();

        MessageBox.Show(
            $"Cleaned {CleanItem.FormatBytes(res.BytesFreed)} across {res.FilesDeleted:N0} files.\n" +
            (res.FilesSkipped > 0 ? $"({res.FilesSkipped:N0} currently in-use files were safely skipped.)" : "Zero files skipped."),
            "Deep Cleaner Complete",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        BtnScanJunk.IsEnabled = true;
        BtnCleanJunk.IsEnabled = true;
    }

    private void UpdateCleanerTotalText()
    {
        long total = _cleanItems.Where(x => x.IsSelected).Sum(x => x.SizeBytes);
        TxtCleanerTotal.Text = $"{CleanItem.FormatBytes(total)} reclaimable";
    }

    private async Task LoadStartupAsync()
    {
        var entries = await _startupManager.LoadStartupEntriesAsync();
        _startupEntries.Clear();
        foreach (var entry in entries)
            _startupEntries.Add(entry);
        TxtStartupCount.Text = $"{_startupEntries.Count(x => x.IsEnabled)} active / {_startupEntries.Count} total";
    }

    private async void RefreshStartup_Click(object sender, RoutedEventArgs e)
    {
        await LoadStartupAsync();
        UpdateTelemetryUi();
    }

    private async void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not StartupEntry entry) return;
        bool desired = !entry.IsEnabled;
        cb.IsEnabled = false;

        bool ok = await _startupManager.SetStartupEnabledAsync(entry, desired);
        if (!ok)
            MessageBox.Show($"Could not modify startup entry '{entry.Name}'.", "Startup Guard", MessageBoxButton.OK, MessageBoxImage.Warning);

        TxtStartupCount.Text = $"{_startupEntries.Count(x => x.IsEnabled)} active / {_startupEntries.Count} total";
        UpdateTelemetryUi();
        cb.IsEnabled = true;
    }

    private async void TweakToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not TweakDefinition tweak) return;
        bool desired = tweak.Id == "flush_dns" || !tweak.IsEnabled;
        cb.IsEnabled = false;

        await _tweaksService.ApplyTweakAsync(tweak, desired);
        UpdateTelemetryUi();
        cb.IsEnabled = true;
    }

    private async void UndoAll_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Restore all tweaked Windows Registry values, Telemetry service, and Power Plan from undo-backup.json?",
            "Undo All Tweaks",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        BtnGlobalUndo.IsEnabled = false;
        var (count, msg) = await _tweaksService.UndoAllAsync(_tweaks);
        UpdateTelemetryUi();
        BtnGlobalUndo.IsEnabled = true;

        MessageBox.Show(msg, "Restore Defaults", MessageBoxButton.OK,
            count > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async Task LoadProcessesAsync()
    {
        var list = await _ramOptimizer.GetTopMemoryProcessesAsync(12);
        _processes.Clear();
        foreach (var item in list)
            _processes.Add(item);
    }

    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e)
    {
        await LoadProcessesAsync();
        UpdateTelemetryUi();
    }

    private async void TrimRam_Click(object sender, RoutedEventArgs e)
    {
        BtnTrimRam.IsEnabled = false;
        TxtRamTrimResult.Text = "Trimming idle working sets across accessible processes...";

        var res = await _ramOptimizer.TrimRamAsync();
        TxtRamTrimResult.Text = $"⚡ Smart RAM Trim complete: Freed {res.MemoryFreedMb:F0} MB across {res.ProcessesTrimmed} processes ({res.RamBeforeGb:F2} GB → {res.RamAfterGb:F2} GB).";

        await LoadProcessesAsync();
        UpdateTelemetryUi();
        BtnTrimRam.IsEnabled = true;
    }

    private async void EndProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ProcessMemoryItem item) return;
        if (item.IsProtected)
        {
            MessageBox.Show($"{item.Name} is protected to keep Windows stable.", "Protected Process", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (ok, msg) = _ramOptimizer.EndProcessSafe(item);
        TxtRamTrimResult.Text = msg;
        if (ok)
        {
            await LoadProcessesAsync();
            UpdateTelemetryUi();
        }
    }

    private async void QuickRestorePoint_Click(object sender, RoutedEventArgs e)
    {
        NavRepair.IsChecked = true;
        await RunRepairActionAsync(ct => _repairService.CreateRestorePointAsync(AppendTerminalLine, ct));
    }

    private async void RepairRestorePoint_Click(object sender, RoutedEventArgs e)
        => await RunRepairActionAsync(ct => _repairService.CreateRestorePointAsync(AppendTerminalLine, ct));

    private async void RepairSsdTrim_Click(object sender, RoutedEventArgs e)
        => await RunRepairActionAsync(ct => _repairService.OptimizeSsdTrimAsync(AppendTerminalLine, ct));

    private async void RepairSfc_Click(object sender, RoutedEventArgs e)
        => await RunRepairActionAsync(ct => _repairService.RunSfcScanAsync(AppendTerminalLine, ct));

    private async void RepairDism_Click(object sender, RoutedEventArgs e)
        => await RunRepairActionAsync(ct => _repairService.RunDismRestoreHealthAsync(AppendTerminalLine, ct));

    private async void RepairIcons_Click(object sender, RoutedEventArgs e)
        => await RunRepairActionAsync(ct => _repairService.RebuildIconAndExplorerCacheAsync(AppendTerminalLine, ct));

    private async Task RunRepairActionAsync(Func<CancellationToken, Task<int>> action)
    {
        if (_repairCts != null)
        {
            AppendTerminalLine("[WinFresh] Another repair task is currently running.");
            return;
        }

        _repairCts = new CancellationTokenSource();
        SetRepairButtonsEnabled(false);
        BtnCancelRepair.IsEnabled = true;

        try
        {
            await action(_repairCts.Token);
        }
        finally
        {
            _repairCts.Dispose();
            _repairCts = null;
            SetRepairButtonsEnabled(true);
            BtnCancelRepair.IsEnabled = false;
        }
    }

    private void CancelRepair_Click(object sender, RoutedEventArgs e) => _repairCts?.Cancel();

    private void ClearLog_Click(object sender, RoutedEventArgs e) => TxtRepairTerminal.Clear();

    private void SetRepairButtonsEnabled(bool enabled)
    {
        BtnRepairRestore.IsEnabled = enabled;
        BtnRepairTrim.IsEnabled = enabled;
        BtnRepairSfc.IsEnabled = enabled;
        BtnRepairDism.IsEnabled = enabled;
        BtnRepairIcons.IsEnabled = enabled;
        BtnQuickRestorePoint.IsEnabled = enabled;
    }

    private void AppendTerminalLine(string line)
    {
        Dispatcher.Invoke(() =>
        {
            TxtRepairTerminal.AppendText(Environment.NewLine + line);
            TxtRepairTerminal.ScrollToEnd();
        });
    }
}
