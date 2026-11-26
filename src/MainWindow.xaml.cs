using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using xDriverUpdater.Models;
using xDriverUpdater.Services;

namespace xDriverUpdater;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly ConfigStore _configStore = new();
    private readonly HardwareDriverInspector _inspector = new();
    private readonly WindowsUpdateDriverService _wuaService = new();
    private readonly MicrosoftCatalogService _catalogService = new();
    private readonly DirectManufacturerService _directService = new();
    private readonly RepositoryManagerService _repoService = new();
    private readonly DriverStoreAndInstallService _storeService = new();
    private readonly ScheduledTaskService _taskService = new();
    private readonly SilentDriverUpdateService _silentUpdateService = new();
    private readonly XSecurityAlertService _alertService = new();
    private readonly DriverHistoryService _historyService = new();
    private readonly TelemetryScanner _telemetryScanner = new();
    private readonly TelemetryController _telemetryController = new();
    private readonly NetworkMonitor _networkMonitor = new();
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private AppSettings _settings = new();
    private SystemProfile _profile = new();
    private List<DriverDevice> _allDevices = new();
    private List<DriverStorePackage> _storePackages = new();
    private List<DriverUpdateCandidate> _allUpdates = new();
    private List<DeviceUpdateGroup> _updateGroups = new();
    private List<TelemetryItem> _telemetryItems = new();
    private List<LiveConnection> _liveConnections = new();
    private bool _monitorPaused;
    private bool _monitorBusy;
    private bool _loadingSettings;
    private bool _isBusy;

    public MainWindow()
    {
        _settings = _configStore.Load();
        InitializeComponent();
        RestoreWindowPlacement();
        _monitorTimer.Tick += async (_, _) => await RefreshLiveMonitorAsync();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch { }
    }

    private void RestoreWindowPlacement()
    {
        var work = SystemParameters.WorkArea;
        var maxW = Math.Max(MinWidth, work.Width - 24);
        var maxH = Math.Max(MinHeight, work.Height - 24);

        if (_settings.WindowWidth is >= 760 and <= 3840)
            Width = Math.Min(_settings.WindowWidth.Value, maxW);
        else
            Width = Math.Min(920, maxW);

        if (_settings.WindowHeight is >= 520 and <= 2160)
            Height = Math.Min(_settings.WindowHeight.Value, maxH);
        else
            Height = Math.Min(640, maxH);

        if (_settings.WindowLeft.HasValue && _settings.WindowTop.HasValue)
        {
            var left = _settings.WindowLeft.Value;
            var top = _settings.WindowTop.Value;
            var vLeft = SystemParameters.VirtualScreenLeft;
            var vTop = SystemParameters.VirtualScreenTop;
            var vRight = vLeft + SystemParameters.VirtualScreenWidth;
            var vBottom = vTop + SystemParameters.VirtualScreenHeight;

            if (left + 100 > vLeft && left < vRight - 100 && top >= vTop && top < vBottom - 80)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _monitorTimer.Stop();
        try
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            _configStore.Save(_settings);
        }
        catch { }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySettingsToUi();
        ReposGrid.ItemsSource = _settings.Repositories;
        RepoPresetCombo.ItemsSource = RepositoryManagerService.GetPublicRepositoryPresets().Select(p => p.Name).ToList();
        if (RepoPresetCombo.Items.Count > 0) RepoPresetCombo.SelectedIndex = 0;
        await Task.WhenAll(RefreshHardwareAndStoreAsync(), RefreshScheduledTaskStatusAsync(), RefreshTelemetryAsync());
    }

    private void ApplySettingsToUi()
    {
        _loadingSettings = true;
        ChkUseWua.IsChecked = _settings.IncludeWindowsUpdateChannel;
        ChkUseDirect.IsChecked = _settings.IncludeDirectManufacturerChecks;
        ChkUseRepos.IsChecked = _settings.IncludeCustomRepositories;
        ChkPreferLaptopOem.IsChecked = _settings.PreferOemDriversOnLaptops;
        ChkRestorePoint.IsChecked = _settings.CreateRestorePointBeforeInstall;
        ChkAutoBackup.IsChecked = _settings.BackupDriverBeforeInstall;
        ChkLocalToastOnFailure.IsChecked = _settings.EnableLocalToastOnFailure;
        SchedTimeBox.Text = _settings.ScheduledTaskTime;
        XSecurityUrlBox.Text = _settings.XSecurityEndpointUrl;
        SchedFreqCombo.SelectedIndex = string.Equals(_settings.ScheduledTaskFrequency, "Weekly", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _loadingSettings = false;
    }

    private void SettingCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.IncludeWindowsUpdateChannel = ChkUseWua.IsChecked == true;
        _settings.IncludeDirectManufacturerChecks = ChkUseDirect.IsChecked == true;
        _settings.IncludeCustomRepositories = ChkUseRepos.IsChecked == true;
        _settings.PreferOemDriversOnLaptops = ChkPreferLaptopOem.IsChecked == true;
        _settings.CreateRestorePointBeforeInstall = ChkRestorePoint.IsChecked == true;
        _settings.BackupDriverBeforeInstall = ChkAutoBackup.IsChecked == true;
        _settings.EnableLocalToastOnFailure = ChkLocalToastOnFailure.IsChecked == true;
        _configStore.Save(_settings);
    }

    private void SetBusy(bool busy, string status, bool indeterminate = true)
    {
        _isBusy = busy;
        RescanHwBtn.IsEnabled = !busy;
        ScanUpdatesBtn.IsEnabled = !busy;
        ScanWuaOnlyBtn.IsEnabled = !busy;
        ScanDirectAndReposBtn.IsEnabled = !busy;
        InstallSelectedUpdatesBtn.IsEnabled = !busy;
        BackupAllDriversBtn.IsEnabled = !busy;
        ExportOfflinePackBtn.IsEnabled = !busy;
        RestoreBackupBtn.IsEnabled = !busy;
        InstallLocalDriverBtn.IsEnabled = !busy;
        DeleteStorePkgBtn.IsEnabled = !busy;
        CleanAllStaleBtn.IsEnabled = !busy && _storePackages.Any(p => p.IsSuperseded && !p.IsInUse);
        RefreshStoreBtn.IsEnabled = !busy;
        RegisterTaskBtn.IsEnabled = !busy;
        RunSilentNowBtn.IsEnabled = !busy;
        TestAlertBtn.IsEnabled = !busy;
        RemoveTaskBtn.IsEnabled = !busy;
        StatusFooterText.Text = status;
        MainProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        MainProgressBar.IsIndeterminate = indeterminate;
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogTextBox.ScrollToEnd();
        });
    }

    private async Task RefreshHardwareAndStoreAsync()
    {
        SetBusy(true, "Inspecting hardware, PCI/USB Subsystem OEM IDs, and Windows Driver Store...");
        AppendLog("Starting full system hardware & driver inventory...");
        try
        {
            var (profile, devices) = await _inspector.InspectSystemAsync(_settings.PreferOemDriversOnLaptops);
            _profile = profile;
            _allDevices = devices;
            _storePackages = await _storeService.EnumerateDriverStoreAsync(_allDevices);

            // Re-attach existing updates if any match
            UpdateOverviewAndAuditUi();
            ApplyDeviceFilters();
            DriverStoreGrid.ItemsSource = _storePackages;

            AppendLog($"Inspection complete: {_allDevices.Count} devices, {_storePackages.Count} Driver Store OEM packages. Platform: {_profile.PlatformSummary}");
            SetBusy(false, $"Ready — {_allDevices.Count} hardware devices inspected ({_profile.FormFactorDisplay}).");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR inspecting system: {ex.Message}");
            SetBusy(false, $"Inspection error: {ex.Message}");
        }
    }

    private void UpdateOverviewAndAuditUi()
    {
        FormFactorPillText.Text = _profile.IsLaptop ? $"LAPTOP • {_profile.NormalizedOem.ToUpperInvariant()} OEM" : $"DESKTOP • {_profile.NormalizedOem.ToUpperInvariant()}";
        HeaderSubtitle.Text = $"{_profile.OsCaption} (Build {_profile.OsBuild}, {_profile.OsArch})  •  {_profile.PlatformSummary}";

        CardPlatformTitle.Text = _profile.PlatformSummary;
        CardPlatformDetail.Text = $"CPU: {_profile.CpuName}\nBIOS: {_profile.BiosManufacturer} {_profile.BiosVersion} ({_profile.BiosReleaseDate})";
        OpenOemPortalBtn.Content = $"Open {_profile.NormalizedOem} Support Portal";

        int problems = _allDevices.Count(d => d.HasProblem);
        int inbox = _allDevices.Count(d => d.HealthStatus == DriverHealthStatus.InboxGeneric);
        int staleStore = _storePackages.Count(p => p.IsSuperseded && !p.IsInUse);
        int devicesWithNewer = _updateGroups.Count(g => g.HasNewerVersion);
        CardHealthTitle.Text = problems == 0
            ? $"{_allDevices.Count} Devices Healthy • {devicesWithNewer} Update(s) Available"
            : $"{problems} Problem Device(s) • {_allDevices.Count} Total Inspected";
        CardHealthDetail.Text = $"Inbox Generic Drivers: {inbox}  •  OEM Driver Store Packages: {_storePackages.Count} ({staleStore} stale/superseded)\nClick 'Check Updates (All Sources)' to query Windows Update, MS Catalog, NVIDIA, and Repositories.";

        var newerOnly = _allUpdates.Where(u => u.IsNewerThanInstalled).ToList();
        var findings = _inspector.RunSmartAudit(_profile, _allDevices, _storePackages, newerOnly);
        AuditList.ItemsSource = findings;
        AuditSummaryText.Text = $"{findings.Count} audit check(s)";

        UpdatesTabItem.Header = _updateGroups.Count > 0
            ? $"Driver Updates ({devicesWithNewer} new)"
            : "Driver Updates";

        CleanAllStaleBtn.Content = staleStore > 0
            ? $"Clean All Stale Packages ({staleStore})"
            : "Clean All Stale Packages";
        CleanAllStaleBtn.IsEnabled = !_isBusy && staleStore > 0;
    }

    private async Task RunUpdateScanAsync(bool includeWua, bool includeDirect, bool includeRepos)
    {
        if (_isBusy) return;
        SetBusy(true, "Scanning selected driver update sources...");
        _allUpdates.Clear();
        _updateGroups.Clear();
        foreach (var d in _allDevices) d.AvailableUpdates.Clear();

        try
        {
            if (includeWua)
            {
                StatusFooterText.Text = "Querying Windows Update (Device Manager WHQL channel)...";
                var (wuaList, wuaMsg) = await _wuaService.ScanDriverUpdatesAsync(_allDevices, AppendLog);
                _allUpdates.AddRange(wuaList);
                AppendLog(wuaMsg);

                StatusFooterText.Text = "Querying Microsoft Update Catalog for versioned WHQL CAB drivers...";
                var catList = await _catalogService.QueryCatalogForPrimaryDevicesAsync(_allDevices, AppendLog);
                _allUpdates.AddRange(catList);
            }

            if (includeDirect)
            {
                StatusFooterText.Text = "Checking Direct Manufacturer feeds (NVIDIA multi-version API)...";
                var directList = await _directService.CheckDirectManufacturerUpdatesAsync(
                    _profile, _allDevices, _settings.PreferOemDriversOnLaptops, AppendLog);
                _allUpdates.AddRange(directList);
            }

            if (includeRepos)
            {
                StatusFooterText.Text = "Syncing & matching enabled Custom Repositories...";
                var repoList = await _repoService.ScanRepositoriesAsync(
                    _profile, _allDevices, _settings.Repositories, _settings.PreferOemDriversOnLaptops, AppendLog);
                _allUpdates.AddRange(repoList);
                ReposGrid.Items.Refresh();
            }

            // Attach back to devices and update health status for real newer versions
            foreach (var u in _allUpdates)
            {
                var dev = _allDevices.FirstOrDefault(d => d.DeviceId == u.DeviceId);
                if (dev != null)
                {
                    dev.AvailableUpdates.Add(u);
                    if (u.IsNewerThanInstalled && !dev.HasProblem)
                        dev.HealthStatus = DriverHealthStatus.UpdateAvailable;
                }
            }

            ApplyUpdateSourceFilter();
            ApplyDeviceFilters();
            UpdateOverviewAndAuditUi();

            int newCount = _updateGroups.Count(g => g.HasNewerVersion);
            SetBusy(false, $"Scan complete: {newCount} device(s) have newer drivers ({_allUpdates.Count} total versions available to choose from).");
            if (_updateGroups.Count > 0)
                MainTabs.SelectedIndex = 1;
        }
        catch (Exception ex)
        {
            AppendLog($"Scan failed: {ex.Message}");
            SetBusy(false, $"Scan error: {ex.Message}");
        }
    }

    private async void RescanHwBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        await RefreshHardwareAndStoreAsync();
    }

    private async void ScanUpdatesBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunUpdateScanAsync(
            _settings.IncludeWindowsUpdateChannel,
            _settings.IncludeDirectManufacturerChecks,
            _settings.IncludeCustomRepositories);
    }

    private async void ScanWuaOnlyBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunUpdateScanAsync(includeWua: true, includeDirect: false, includeRepos: false);
    }

    private async void ScanDirectAndReposBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunUpdateScanAsync(includeWua: false, includeDirect: true, includeRepos: true);
    }

    private void OpenOemPortalBtn_Click(object sender, RoutedEventArgs e)
    {
        DriverStoreAndInstallService.OpenUrl(_profile.OemSupportUrl);
    }

    // ==================== UPDATES TAB ====================

    private void UpdateSourceFilter_SelectionChanged(object sender, RoutedEventArgs e)
    {
        ApplyUpdateSourceFilter();
    }

    private void ApplyUpdateSourceFilter()
    {
        if (UpdatesGrid == null || UpdateSourceFilter == null) return;
        var sel = (UpdateSourceFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Sources";
        bool includeOlder = ChkIncludeOlderVersions?.IsChecked == true;

        IEnumerable<DriverUpdateCandidate> q = _allUpdates;
        if (sel.StartsWith("Windows Update"))
            q = q.Where(u => u.SourceKind is UpdateSourceKind.WindowsUpdate or UpdateSourceKind.MicrosoftCatalog);
        else if (sel.StartsWith("Direct"))
            q = q.Where(u => u.SourceKind == UpdateSourceKind.DirectManufacturer);
        else if (sel.StartsWith("Custom"))
            q = q.Where(u => u.SourceKind == UpdateSourceKind.CustomRepository);

        if (!includeOlder)
            q = q.Where(u => u.IsNewerThanInstalled);

        // Group into 1 row per device, with all available versions sorted newest-first in the dropdown!
        _updateGroups = q
            .GroupBy(u => string.IsNullOrWhiteSpace(u.DeviceId) ? u.DeviceName : u.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(grp =>
            {
                var orderedVersions = grp
                    .OrderByDescending(v => v.IsNewerThanInstalled)
                    .ThenByDescending(v => DirectManufacturerService.ParseComparableVersion(v.Version))
                    .ThenByDescending(v => v.ReleaseDate ?? DateTime.MinValue)
                    .ThenByDescending(v => v.MatchScore)
                    .ToList();

                var first = orderedVersions[0];
                var dev = _allDevices.FirstOrDefault(d => d.DeviceId == first.DeviceId);
                bool isPinned = !string.IsNullOrWhiteSpace(first.DeviceId) && _settings.PinnedDevices.ContainsKey(first.DeviceId);
                return new DeviceUpdateGroup
                {
                    DeviceId = first.DeviceId,
                    DeviceName = isPinned ? $"{first.DeviceName} [PINNED]" : first.DeviceName,
                    Category = dev?.Category ?? "",
                    InstalledVersion = first.InstalledVersion,
                    Versions = orderedVersions,
                    SelectedVersion = first,
                    IsSelected = first.IsNewerThanInstalled && !isPinned
                };
            })
            .OrderByDescending(g => g.HasNewerVersion)
            .ThenBy(g => g.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        UpdatesGrid.ItemsSource = _updateGroups;
    }

    private void UpdatesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSelectedUpdateDetails();
    }

    private void VersionPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSelectedUpdateDetails();
    }

    private void RefreshSelectedUpdateDetails()
    {
        if (UpdatesGrid?.SelectedItem is not DeviceUpdateGroup g || g.SelectedVersion is not DriverUpdateCandidate u)
        {
            if (OpenUpdateUrlBtn != null) OpenUpdateUrlBtn.IsEnabled = false;
            if (PinUpdateDeviceBtn != null) PinUpdateDeviceBtn.IsEnabled = false;
            return;
        }

        bool isPinned = !string.IsNullOrWhiteSpace(g.DeviceId) && _settings.PinnedDevices.ContainsKey(g.DeviceId);
        var actionTag = u.IsNewerThanInstalled ? "Upgrade" : "Pin / Rollback";
        SelectedUpdateTitle.Text = $"{g.DeviceName}  —  {g.InstalledVersion} → v{u.Version} ({actionTag} • {g.Versions.Count} version(s) available)";
        SelectedUpdateDetail.Text = $"Package: {u.Title}  •  Source: {u.SourceLabel}  •  Channel: {u.Channel.ToUpperInvariant()}  •  Date: {u.ReleaseDateText}\n{u.ReleaseNotes}";
        OpenUpdateUrlBtn.IsEnabled = !string.IsNullOrWhiteSpace(u.DownloadUrl);
        PinUpdateDeviceBtn.IsEnabled = !string.IsNullOrWhiteSpace(g.DeviceId);
        PinUpdateDeviceBtn.Content = isPinned ? "Unpin Device" : "Pin Current Version";
    }

    private void PinUpdateDeviceBtn_Click(object sender, RoutedEventArgs e)
    {
        if (UpdatesGrid.SelectedItem is not DeviceUpdateGroup g || string.IsNullOrWhiteSpace(g.DeviceId)) return;
        TogglePinDevice(g.DeviceId, g.DeviceName.Replace(" [PINNED]", ""), g.InstalledVersion);
        ApplyUpdateSourceFilter();
        RefreshSelectedUpdateDetails();
    }

    private void TogglePinDevice(string deviceId, string deviceName, string currentVersion)
    {
        if (_settings.PinnedDevices.ContainsKey(deviceId))
        {
            _settings.PinnedDevices.Remove(deviceId);
            _configStore.Save(_settings);
            AppendLog($"[Pin] Unpinned {deviceName} — updates will now apply normally.");
            StatusFooterText.Text = $"Unpinned {deviceName}.";
        }
        else
        {
            _settings.PinnedDevices[deviceId] = currentVersion;
            _configStore.Save(_settings);
            AppendLog($"[Pin] Pinned {deviceName} at v{currentVersion} — skipped by scheduled silent updates.");
            StatusFooterText.Text = $"Pinned {deviceName} at v{currentVersion}.";
        }
    }

    private void OpenUpdateUrlBtn_Click(object sender, RoutedEventArgs e)
    {
        if (UpdatesGrid.SelectedItem is DeviceUpdateGroup g && g.SelectedVersion != null && !string.IsNullOrWhiteSpace(g.SelectedVersion.DownloadUrl))
            DriverStoreAndInstallService.OpenUrl(g.SelectedVersion.DownloadUrl);
    }

    private async void InstallSelectedUpdatesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var selected = _updateGroups
            .Where(g => g.IsSelected && g.SelectedVersion != null)
            .Select(g => g.SelectedVersion!)
            .ToList();

        if (selected.Count == 0)
        {
            DarkDialog.Show("Check at least one device row to install its selected Target Version.", "xDriverUpdater", DarkDialogKind.Info, this);
            return;
        }

        var summaryLines = string.Join("\n", selected.Take(6).Select(s => $"  • {s.DeviceName}: {s.InstalledVersion} → v{s.Version} ({s.SourceLabel})"));
        if (selected.Count > 6) summaryLines += $"\n  ... and {selected.Count - 6} more";

        var confirmed = DarkDialog.Confirm(
            $"Install {selected.Count} selected driver version(s)?\n\n{summaryLines}\n\n" +
            $"• System Restore Point: {(_settings.CreateRestorePointBeforeInstall ? "Enabled" : "Off")}\n" +
            $"• Auto-Backup Existing INF: {(_settings.BackupDriverBeforeInstall ? "Enabled" : "Off")}",
            "Confirm Driver Installation", DarkDialogKind.Question, this);
        if (!confirmed) return;

        SetBusy(true, $"Installing {selected.Count} driver update(s)...", indeterminate: false);
        bool anyReboot = false;
        int succeeded = 0;

        try
        {
            if (_settings.CreateRestorePointBeforeInstall && selected.Any(s => !s.InstallerType.Equals("page", StringComparison.OrdinalIgnoreCase)))
            {
                StatusFooterText.Text = "Creating Windows System Restore Point...";
                MainProgressBar.IsIndeterminate = true;
                await _storeService.CreateSystemRestorePointAsync("xDriverUpdater Pre-Install", AppendLog);
            }

            // Batch WUA updates cleanly in one WUA session if multiple are selected
            var wuaSelected = selected.Where(s => s.SourceKind == UpdateSourceKind.WindowsUpdate).ToList();
            if (wuaSelected.Count > 0)
            {
                StatusFooterText.Text = $"Downloading & installing {wuaSelected.Count} Windows Update WHQL driver(s)...";
                MainProgressBar.IsIndeterminate = true;
                if (_settings.BackupDriverBeforeInstall)
                {
                    foreach (var w in wuaSelected)
                    {
                        var dev = _allDevices.FirstOrDefault(d => d.DeviceId == w.DeviceId);
                        if (dev != null && !string.IsNullOrWhiteSpace(dev.InfName))
                            await _storeService.ExportSingleDriverBackupAsync(dev.InfName, _settings.BackupDirectory, AppendLog);
                    }
                }
                var (ok, reboot, msg) = await _wuaService.DownloadAndInstallWuaUpdatesAsync(wuaSelected, AppendLog);
                if (ok) succeeded += wuaSelected.Count;
                if (reboot) anyReboot = true;
                AppendLog(msg);
            }

            // Install Direct / Custom Repo packages one by one with download progress
            var nonWua = selected.Where(s => s.SourceKind != UpdateSourceKind.WindowsUpdate).ToList();
            var prog = new Progress<double>(pct =>
            {
                MainProgressBar.IsIndeterminate = false;
                MainProgressBar.Value = pct;
            });

            foreach (var cand in nonWua)
            {
                StatusFooterText.Text = $"Processing {cand.Title}...";
                var dev = _allDevices.FirstOrDefault(d => d.DeviceId == cand.DeviceId);
                var (ok, reboot, msg) = await _storeService.InstallCandidateAsync(cand, dev, _settings, _wuaService, AppendLog, prog);
                if (ok) succeeded++;
                if (reboot) anyReboot = true;
                AppendLog($"[{cand.Title}] {msg}");
                _historyService.Record(new DriverHistoryEntry
                {
                    Action = cand.IsNewerThanInstalled ? "Install" : "Rollback",
                    DeviceName = cand.DeviceName,
                    DeviceId = cand.DeviceId,
                    FromVersion = cand.InstalledVersion,
                    ToVersion = cand.Version,
                    Source = cand.SourceLabel,
                    Success = ok,
                    Message = msg
                });
            }

            SetBusy(false, $"Finished: {succeeded}/{selected.Count} completed." + (anyReboot ? " Reboot recommended." : ""));
            DarkDialog.Show(
                $"Driver operation finished ({succeeded}/{selected.Count} succeeded)." +
                (anyReboot ? "\n\nWindows indicated a reboot is required to complete driver activation." : ""),
                "xDriverUpdater", succeeded == selected.Count ? DarkDialogKind.Success : DarkDialogKind.Warning, this);

            await RefreshHardwareAndStoreAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"Install error: {ex.Message}");
            SetBusy(false, $"Install error: {ex.Message}");
        }
    }

    // ==================== ALL DEVICES TAB ====================

    private void DeviceFilter_Changed(object sender, EventArgs e)
    {
        ApplyDeviceFilters();
    }

    private void ApplyDeviceFilters()
    {
        if (DevicesGrid == null) return;
        var text = DeviceSearchBox?.Text?.Trim() ?? "";
        var cat = (DeviceCategoryFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Categories";
        var stat = (DeviceStatusFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Statuses";

        IEnumerable<DriverDevice> q = _allDevices;

        if (!string.IsNullOrWhiteSpace(text))
        {
            q = q.Where(d =>
                d.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                d.DriverProvider.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                d.SiliconVendor.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                d.HardwareId.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                d.InfName.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        if (cat != "All Categories")
            q = q.Where(d => d.Category.Equals(cat, StringComparison.OrdinalIgnoreCase));

        q = stat switch
        {
            "Problems Only" => q.Where(d => d.HasProblem),
            "Updates Available" => q.Where(d => d.AvailableUpdates.Count > 0),
            "Inbox Generic" => q.Where(d => d.HealthStatus == DriverHealthStatus.InboxGeneric),
            "OEM Customized" => q.Where(d => d.IsLaptopOemCustomized),
            "Aged (>2 yrs)" => q.Where(d => d.HealthStatus == DriverHealthStatus.Outdated),
            _ => q
        };

        DevicesGrid.ItemsSource = q.ToList();
    }

    private void DevicesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not DriverDevice d)
        {
            DevMsCatalogBtn.IsEnabled = false;
            DevOemBtn.IsEnabled = false;
            DevDirectBtn.IsEnabled = false;
            DevBackupBtn.IsEnabled = false;
            DevPinBtn.IsEnabled = false;
            DevCopyHwidBtn.IsEnabled = false;
            return;
        }

        bool isPinned = !string.IsNullOrWhiteSpace(d.DeviceId) && _settings.PinnedDevices.ContainsKey(d.DeviceId);
        InspectorDeviceTitle.Text = $"{d.DisplayName}  (v{d.DriverVersion}, {d.DriverDateText}){(isPinned ? "  [PINNED]" : "")}";
        var subsysInfo = !string.IsNullOrEmpty(d.SubsystemVendorId)
            ? $"SUBSYS SVID: {d.SubsystemVendorId} ({(string.IsNullOrEmpty(d.SubsystemOemName) ? "Unknown OEM" : d.SubsystemOemName)})"
            : $"Bus: {d.BusType}";
        InspectorDeviceMeta.Text = $"Silicon: {d.SiliconVendor}  •  {subsysInfo}  •  Signed: {(d.IsSigned ? "Yes" : "No")}";

        var primaryHwid = d.HardwareIds.FirstOrDefault() ?? d.HardwareId;
        InspectorRationale.Text = $"{d.SourceRationale}\nHardware ID: {primaryHwid}  |  INF: {d.InfName}  |  Signer: {d.Signer}";

        DevMsCatalogBtn.IsEnabled = true;
        DevOemBtn.IsEnabled = !string.IsNullOrWhiteSpace(d.OemVendorUrl);
        DevOemBtn.Content = $"Open {(!string.IsNullOrEmpty(d.SubsystemOemName) ? d.SubsystemOemName : _profile.NormalizedOem)} OEM Portal";
        DevDirectBtn.IsEnabled = !string.IsNullOrWhiteSpace(d.DirectVendorUrl);
        DevDirectBtn.Content = $"Open {d.SiliconVendor} Direct Site";
        DevBackupBtn.IsEnabled = d.InfName.StartsWith("oem", StringComparison.OrdinalIgnoreCase);
        DevPinBtn.IsEnabled = !string.IsNullOrWhiteSpace(d.DeviceId);
        DevPinBtn.Content = isPinned ? "Unpin Device" : "Pin Current Version";
        DevCopyHwidBtn.IsEnabled = !string.IsNullOrWhiteSpace(primaryHwid);
    }

    private void DevMsCatalogBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is DriverDevice d)
            DriverStoreAndInstallService.OpenUrl(d.MicrosoftCatalogUrl);
    }

    private void DevOemBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is DriverDevice d)
            DriverStoreAndInstallService.OpenUrl(d.OemVendorUrl);
    }

    private void DevDirectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is DriverDevice d)
            DriverStoreAndInstallService.OpenUrl(d.DirectVendorUrl);
    }

    private async void DevBackupBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not DriverDevice d || _isBusy) return;
        SetBusy(true, $"Backing up {d.InfName}...");
        var (ok, msg) = await _storeService.ExportSingleDriverBackupAsync(d.InfName, _settings.BackupDirectory, AppendLog);
        SetBusy(false, msg);
        DarkDialog.Show(msg, "Driver Backup", ok ? DarkDialogKind.Success : DarkDialogKind.Warning, this);
    }

    private void DevPinBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not DriverDevice d || string.IsNullOrWhiteSpace(d.DeviceId)) return;
        TogglePinDevice(d.DeviceId, d.DisplayName, d.DriverVersion);
        DevicesGrid_SelectionChanged(DevicesGrid, null!);
    }

    private void DevCopyHwidBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is DriverDevice d)
        {
            var id = d.HardwareIds.FirstOrDefault() ?? d.HardwareId;
            if (!string.IsNullOrWhiteSpace(id))
            {
                Clipboard.SetText(id);
                StatusFooterText.Text = $"Copied Hardware ID to clipboard: {id}";
            }
        }
    }

    // ==================== REPOSITORIES TAB ====================

    private void BrowseRepoBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select Repository JSON Manifest (or cancel to pick a folder)",
            Filter = "JSON Manifest (*.json)|*.json|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            NewRepoUrlBox.Text = dlg.FileName;
            if (string.IsNullOrWhiteSpace(NewRepoNameBox.Text))
                NewRepoNameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
            return;
        }

        var folderDlg = new OpenFolderDialog
        {
            Title = "Select Local Driver Repository Folder (containing .inf or manifest.json)"
        };
        if (folderDlg.ShowDialog() == true)
        {
            NewRepoUrlBox.Text = folderDlg.FolderName;
            if (string.IsNullOrWhiteSpace(NewRepoNameBox.Text))
                NewRepoNameBox.Text = Path.GetFileName(folderDlg.FolderName);
        }
    }

    private void AddRepoBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = NewRepoNameBox.Text.Trim();
        var url = NewRepoUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            DarkDialog.Show("Enter a repository HTTPS URL, local .json manifest path, or local driver folder path.", "Add Repository", DarkDialogKind.Info, this);
            return;
        }
        if (string.IsNullOrWhiteSpace(name))
            name = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(url).Host : Path.GetFileName(url);

        _settings.Repositories.Add(new RepositorySourceConfig
        {
            Name = name,
            UrlOrPath = url,
            Enabled = true,
            IsBuiltIn = false,
            LastStatus = "Added (not synced)"
        });
        _configStore.Save(_settings);
        ReposGrid.ItemsSource = null;
        ReposGrid.ItemsSource = _settings.Repositories;
        NewRepoNameBox.Clear();
        NewRepoUrlBox.Clear();
        AppendLog($"Added custom repository: '{name}' ({url})");
    }

    private void AddPresetRepoBtn_Click(object sender, RoutedEventArgs e)
    {
        var presets = RepositoryManagerService.GetPublicRepositoryPresets();
        var idx = RepoPresetCombo.SelectedIndex;
        if (idx < 0 || idx >= presets.Count) return;

        var preset = presets[idx];
        if (_settings.Repositories.Any(r => r.UrlOrPath.Equals(preset.UrlOrPath, StringComparison.OrdinalIgnoreCase)))
        {
            DarkDialog.Show($"'{preset.Name}' is already in your repository list.", "Repositories", DarkDialogKind.Info, this);
            return;
        }

        _settings.Repositories.Add(new RepositorySourceConfig
        {
            Name = preset.Name,
            UrlOrPath = preset.UrlOrPath,
            Description = preset.Description,
            Enabled = true,
            IsBuiltIn = false,
            LastStatus = "Added (click Save & Sync)"
        });
        _configStore.Save(_settings);
        ReposGrid.ItemsSource = null;
        ReposGrid.ItemsSource = _settings.Repositories;
        AppendLog($"Added preset repository: '{preset.Name}' ({preset.UrlOrPath})");
    }

    private void RemoveRepoBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ReposGrid.SelectedItem is not RepositorySourceConfig repo) return;
        if (repo.IsBuiltIn)
        {
            DarkDialog.Show("The built-in core catalog cannot be deleted (you can uncheck it to disable it).", "Repositories", DarkDialogKind.Info, this);
            return;
        }
        _settings.Repositories.Remove(repo);
        _configStore.Save(_settings);
        ReposGrid.ItemsSource = null;
        ReposGrid.ItemsSource = _settings.Repositories;
        AppendLog($"Removed custom repository: '{repo.Name}'");
    }

    private void ExportSampleRepoBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save Sample Repository Manifest",
            FileName = "xDriverUpdater-repo-sample.json",
            Filter = "JSON File (*.json)|*.json"
        };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, RepositoryManagerService.GenerateSampleManifestJson());
            AppendLog($"Exported sample repository manifest to {dlg.FileName}");
            DarkDialog.Show($"Sample repository manifest saved to:\n{dlg.FileName}\n\nHost this JSON on GitHub, Cloudflare R2, xServer, or a local folder and add it above!", "Sample Manifest Exported", DarkDialogKind.Success, this);
        }
    }

    private async void SaveAndSyncReposBtn_Click(object sender, RoutedEventArgs e)
    {
        _configStore.Save(_settings);
        await RunUpdateScanAsync(includeWua: false, includeDirect: false, includeRepos: true);
    }

    // ==================== DRIVER STORE & BACKUP TAB ====================

    private async void BackupAllDriversBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true, "Exporting all third-party Windows drivers via pnputil...");
        var (ok, outDir, msg) = await _storeService.ExportAllDriversBackupAsync(_settings.BackupDirectory, AppendLog);
        SetBusy(false, msg);
        DarkDialog.Show(msg, "Full Driver Backup", ok ? DarkDialogKind.Success : DarkDialogKind.Warning, this);
    }

    private async void ExportOfflinePackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var folderDlg = new OpenFolderDialog
        {
            Title = "Choose Destination Folder for Portable Offline Driver Pack (e.g. USB drive or backup folder)",
            InitialDirectory = Directory.Exists(_settings.BackupDirectory) ? _settings.BackupDirectory : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (folderDlg.ShowDialog() != true) return;

        SetBusy(true, "Building Portable Offline Driver Pack (exporting drivers + restore script)...");
        var (ok, packDir, msg) = await _storeService.ExportPortableOfflineDriverPackAsync(folderDlg.FolderName, _profile, _allDevices, AppendLog);
        SetBusy(false, ok ? $"Portable Offline Driver Pack created: {packDir}" : "Offline pack export failed.");
        DarkDialog.Show(msg, "Portable Offline Driver Pack", ok ? DarkDialogKind.Success : DarkDialogKind.Warning, this);
    }

    private async void RestoreBackupBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        Directory.CreateDirectory(_settings.BackupDirectory);
        var folderDlg = new OpenFolderDialog
        {
            Title = "Select a Backed-Up Driver Folder to Restore / Rollback",
            InitialDirectory = _settings.BackupDirectory
        };
        if (folderDlg.ShowDialog() != true) return;

        if (!DarkDialog.Confirm(
            $"Restore / rollback drivers from:\n{folderDlg.FolderName}\n\nThis runs pnputil /add-driver /subdirs /install on the selected backup folder.",
            "Confirm Driver Backup Restore", DarkDialogKind.Question, this))
            return;

        SetBusy(true, $"Restoring driver backup from {Path.GetFileName(folderDlg.FolderName)}...");
        var (ok, reboot, msg) = await _storeService.InstallLocalPackageAsync(folderDlg.FolderName, AppendLog);
        _historyService.Record(new DriverHistoryEntry
        {
            Action = "Rollback",
            DeviceName = Path.GetFileName(folderDlg.FolderName),
            Source = "Local Backup Restore",
            Success = ok,
            Message = msg,
            BackupPath = folderDlg.FolderName
        });
        await RefreshHardwareAndStoreAsync();
        SetBusy(false, ok ? "Driver backup restored." : "Driver backup restore finished with warnings.");
        DarkDialog.Show(
            (ok ? "Driver backup restored successfully." : "Restore reported an issue:") +
            (reboot ? "\n\nReboot required." : "") +
            $"\n\n{msg}",
            "Restore / Rollback Backup", ok ? DarkDialogKind.Success : DarkDialogKind.Warning, this);
    }

    private void ViewHistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        var entries = _historyService.LoadHistory();
        if (entries.Count == 0)
        {
            DarkDialog.Show("No driver installs, scheduled updates, or rollbacks have been recorded yet.", "Driver Install History", DarkDialogKind.Info, this);
            return;
        }

        var lines = entries.Take(15).Select(h =>
            $"• [{h.TimeText}] {h.Action} — {h.DeviceName} ({h.FromVersion} → {h.ToVersion}, {h.Source}): {(h.Success ? "OK" : "FAILED")}");
        var summary = string.Join("\n", lines);
        if (entries.Count > 15)
            summary += $"\n\n... and {entries.Count - 15} older record(s) in history.json";

        DarkDialog.Show(summary, $"Driver Install History ({entries.Count} total)", DarkDialogKind.Info, this);
    }

    private async void InstallLocalDriverBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var dlg = new OpenFileDialog
        {
            Title = "Select Driver Package (.inf, .cab, or .zip)",
            Filter = "Driver Packages (*.inf;*.cab;*.zip)|*.inf;*.cab;*.zip|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        SetBusy(true, $"Installing local driver package {Path.GetFileName(dlg.FileName)}...");
        var (ok, reboot, msg) = await _storeService.InstallLocalPackageAsync(dlg.FileName, AppendLog);
        SetBusy(false, ok ? "Local driver package installed." : "Local driver install finished with warnings.");
        DarkDialog.Show(
            (ok ? "Driver package installed successfully." : "Driver install reported an issue:") +
            (reboot ? "\n\nReboot required." : "") +
            $"\n\n{msg}",
            "Local Driver Install", ok ? DarkDialogKind.Success : DarkDialogKind.Warning, this);
        await RefreshHardwareAndStoreAsync();
    }

    private async void DeleteStorePkgBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || DriverStoreGrid.SelectedItem is not DriverStorePackage pkg) return;
        if (pkg.IsInUse)
        {
            DarkDialog.Show($"{pkg.PublishedName} ({pkg.OriginalName}) is currently ACTIVE on a device. Remove only STALE or CACHED packages.", "Active Driver Protected", DarkDialogKind.Warning, this);
            return;
        }

        var confirmed = DarkDialog.Confirm(
            $"Delete driver package {pkg.PublishedName} ({pkg.OriginalName}, v{pkg.DriverVersion}) from the Windows Driver Store?",
            "Confirm Driver Store Prune", DarkDialogKind.Question, this);
        if (!confirmed) return;

        SetBusy(true, $"Removing {pkg.PublishedName} from Driver Store...");
        var (ok, msg) = await _storeService.DeleteDriverStorePackageAsync(pkg.PublishedName);
        AppendLog($"[DriverStore] Delete {pkg.PublishedName}: {msg}");
        await RefreshHardwareAndStoreAsync();
        SetBusy(false, ok ? $"Removed {pkg.PublishedName}." : $"Failed to remove {pkg.PublishedName}.");
    }

    private async void CleanAllStaleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var stalePackages = _storePackages
            .Where(p => p.IsSuperseded && !p.IsInUse && p.PublishedName.StartsWith("oem", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (stalePackages.Count == 0)
        {
            DarkDialog.Show("No stale (superseded, inactive) OEM driver packages were found in the Windows Driver Store.", "Driver Store Clean", DarkDialogKind.Info, this);
            return;
        }

        var previewLines = string.Join("\n", stalePackages.Take(8).Select(p =>
            $"  • {p.PublishedName} — {p.OriginalName} (v{p.DriverVersion}, {p.ProviderName})"));
        if (stalePackages.Count > 8)
            previewLines += $"\n  ... and {stalePackages.Count - 8} more";

        var confirmed = DarkDialog.Confirm(
            $"Remove all {stalePackages.Count} stale (superseded, inactive) OEM driver package(s) from the Windows Driver Store?\n\n" +
            $"{previewLines}\n\n" +
            "Active drivers currently in use by devices will not be affected.",
            "Confirm Clean All Stale Packages", DarkDialogKind.Question, this);
        if (!confirmed) return;

        SetBusy(true, $"Cleaning up {stalePackages.Count} stale driver package(s)...", indeterminate: false);
        MainProgressBar.Value = 0;
        AppendLog($"[DriverStore] Starting bulk cleanup of {stalePackages.Count} stale package(s)...");

        try
        {
            var progress = new Progress<(int Completed, int Total, DriverStorePackage Current)>(p =>
            {
                MainProgressBar.IsIndeterminate = false;
                MainProgressBar.Value = p.Total > 0 ? (double)p.Completed / p.Total * 100.0 : 0;
                if (p.Completed < p.Total)
                    StatusFooterText.Text = $"Removing stale package {p.Completed + 1}/{p.Total}: {p.Current.PublishedName} ({p.Current.OriginalName})...";
            });

            var (removed, failed) = await _storeService.DeleteStalePackagesAsync(stalePackages, AppendLog, progress);
            AppendLog($"[DriverStore] Bulk stale cleanup complete: {removed}/{stalePackages.Count} removed, {failed.Count} failed.");

            await RefreshHardwareAndStoreAsync();

            if (failed.Count == 0)
            {
                StatusFooterText.Text = $"Cleaned up {removed} stale driver package(s).";
                DarkDialog.Show(
                    $"Successfully removed {removed} stale driver package(s) from the Windows Driver Store.",
                    "Driver Store Cleanup Complete", DarkDialogKind.Success, this);
            }
            else
            {
                StatusFooterText.Text = $"Cleanup finished: {removed}/{stalePackages.Count} removed ({failed.Count} failed).";
                var failLines = string.Join("\n", failed.Take(5).Select(f => $"  • {f.Package.PublishedName} ({f.Package.OriginalName}): {f.Error}"));
                if (failed.Count > 5)
                    failLines += $"\n  ... and {failed.Count - 5} more (see Execution & Audit Log)";

                DarkDialog.Show(
                    $"Removed {removed} of {stalePackages.Count} stale driver package(s).\n\n" +
                    $"{failed.Count} package(s) could not be removed:\n{failLines}",
                    "Driver Store Cleanup Completed with Warnings", DarkDialogKind.Warning, this);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[DriverStore] Bulk cleanup error: {ex.Message}");
            SetBusy(false, $"Cleanup error: {ex.Message}");
            DarkDialog.Show($"Stale package cleanup encountered an error:\n\n{ex.Message}", "Driver Store Cleanup Error", DarkDialogKind.Warning, this);
        }
    }

    private void OpenBackupsFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.BackupDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.BackupDirectory}\"") { UseShellExecute = true });
    }

    private async void RefreshStoreBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        await RefreshHardwareAndStoreAsync();
    }

    // ==================== SCHEDULED SILENT UPDATES & xSECURITY ALERTS ====================

    private async Task RefreshScheduledTaskStatusAsync()
    {
        var info = await _taskService.GetStatusAsync();
        Dispatcher.Invoke(() =>
        {
            ScheduledTaskStatusText.Text = info.Exists
                ? $"Registered ({info.State})  •  Next: {info.NextRun}  •  Last: {info.LastRun} ({info.LastResult})"
                : "Not registered in Windows Task Scheduler";
        });
    }

    private void SaveScheduledUiSettings()
    {
        _settings.ScheduledTaskFrequency = (SchedFreqCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Daily";
        _settings.ScheduledTaskTime = string.IsNullOrWhiteSpace(SchedTimeBox.Text) ? "03:00" : SchedTimeBox.Text.Trim();
        _settings.XSecurityEndpointUrl = string.IsNullOrWhiteSpace(XSecurityUrlBox.Text)
            ? "http://192.168.88.50:4789/api/events/ingest"
            : XSecurityUrlBox.Text.Trim();
        _settings.EnableLocalToastOnFailure = ChkLocalToastOnFailure.IsChecked == true;
        _configStore.Save(_settings);
    }

    private async void RegisterTaskBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SaveScheduledUiSettings();
        SetBusy(true, "Registering Windows Scheduled Task for silent driver updates...");
        var (ok, msg) = await _taskService.RegisterOrUpdateTaskAsync(_settings, AppendLog);
        if (ok)
        {
            _settings.ScheduledTaskEnabled = true;
            _configStore.Save(_settings);
        }
        await RefreshScheduledTaskStatusAsync();
        SetBusy(false, msg);
        DarkDialog.Show(msg, "Scheduled Silent Updates", ok ? DarkDialogKind.Success : DarkDialogKind.Warning, this);
    }

    private async void RunSilentNowBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SaveScheduledUiSettings();
        SetBusy(true, "Running silent driver update check, download, and install...");
        var result = await _silentUpdateService.RunSilentUpdateAsync(_settings, AppendLog);
        await RefreshHardwareAndStoreAsync();
        await RefreshScheduledTaskStatusAsync();
        SetBusy(false, result.Summary);
        DarkDialog.Show(
            $"{result.Summary}\n\nLog file:\n{result.LogFilePath}",
            "Silent Driver Update Run",
            result.Success ? DarkDialogKind.Success : DarkDialogKind.Warning,
            this);
    }

    private async void TestAlertBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SaveScheduledUiSettings();
        SetBusy(true, "Sending test alert to xServer xSecurity & connected devices...");
        var logPath = Path.Combine(_settings.LogDirectory, "scheduled-update-latest.log");
        var (ok, msg) = await _alertService.SendTestAlertAsync(_settings, logPath, AppendLog);
        SetBusy(false, msg);
        DarkDialog.Show(msg, "xSecurity Alert Test", ok ? DarkDialogKind.Success : DarkDialogKind.Warning, this);
    }

    private void OpenLogsFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.LogDirectory}\"") { UseShellExecute = true });
    }

    private async void RemoveTaskBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true, "Removing scheduled silent driver update task...");
        var (ok, msg) = await _taskService.RemoveTaskAsync(AppendLog);
        if (ok)
        {
            _settings.ScheduledTaskEnabled = false;
            _configStore.Save(_settings);
        }
        await RefreshScheduledTaskStatusAsync();
        SetBusy(false, msg);
        DarkDialog.Show(msg, "Scheduled Silent Updates", ok ? DarkDialogKind.Info : DarkDialogKind.Warning, this);
    }

    // ==================== TELEMETRY, DEBLOAT & LIVE OUTBOUND MONITOR ====================

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs) return;
        bool onMonitor = MainTabs.SelectedItem == LiveMonitorTabItem;
        if (onMonitor && !_monitorPaused)
        {
            _monitorTimer.Start();
            _ = RefreshLiveMonitorAsync();
        }
        else
        {
            _monitorTimer.Stop();
        }
    }

    private async Task RefreshTelemetryAsync()
    {
        try
        {
            var items = await _telemetryScanner.ScanAllAsync();
            _telemetryItems = items;
            Dispatcher.Invoke(() =>
            {
                int activeCount = _telemetryItems.Count(t => t.IsActive);
                int safeActive = _telemetryItems.Count(t => t.IsActive && t.Risk == RiskLevel.Safe);
                TelemetryTabItem.Header = activeCount > 0
                    ? $"Telemetry & Debloat ({activeCount})"
                    : "Telemetry & Debloat";
                ApplyTelemetryFilters();
                AppendLog($"[Telemetry] Scan complete: {_telemetryItems.Count} controls ({activeCount} active, {safeActive} safe to disable/remove).");
            });
        }
        catch (Exception ex)
        {
            AppendLog($"[Telemetry] Scan error: {ex.Message}");
        }
    }

    private void TelemetryFilter_Changed(object sender, EventArgs e)
    {
        ApplyTelemetryFilters();
    }

    private void ApplyTelemetryFilters()
    {
        if (TelemetryGrid == null) return;
        var q = _telemetryItems.AsEnumerable();
        var search = TelemetrySearchBox?.Text?.Trim() ?? "";
        var cat = (TelemetryCategoryFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Categories";
        var risk = (TelemetryRiskFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Risks";
        var stat = (TelemetryStatusFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Statuses";

        if (!string.IsNullOrWhiteSpace(search))
        {
            q = q.Where(t =>
                t.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.Vendor.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.Description.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (cat != "All Categories")
            q = q.Where(t => t.CategoryLabel.Equals(cat, StringComparison.OrdinalIgnoreCase));

        if (risk != "All Risks")
            q = q.Where(t => t.RiskLabel.Equals(risk, StringComparison.OrdinalIgnoreCase));

        if (stat.StartsWith("Active"))
            q = q.Where(t => t.IsActive);
        else if (stat.StartsWith("Hardened"))
            q = q.Where(t => !t.IsActive);

        TelemetryGrid.ItemsSource = q.ToList();
    }

    private void TelSelectSafeBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _telemetryItems)
            item.IsSelected = item.IsActive && item.Risk == RiskLevel.Safe;
    }

    private void TelSelectAllActiveBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _telemetryItems)
            item.IsSelected = item.IsActive;
    }

    private void TelClearSelBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _telemetryItems)
            item.IsSelected = false;
    }

    private async void TelDisableSelectedBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = _telemetryItems.Where(t => t.IsSelected && t.IsActive).ToList();
        if (selected.Count == 0)
        {
            DarkDialog.Show("Select one or more active telemetry or bloat items first (tip: click 'Select Safe Active').", "Telemetry & Debloat", DarkDialogKind.Info, this);
            return;
        }

        if (!DarkDialog.Confirm($"Disable / remove {selected.Count} selected item(s)?\n\nServices, tasks, registry policies, and startup items can be restored anytime; removed Appx packages can be reinstalled from Microsoft Store.", "Confirm Harden / Debloat", DarkDialogKind.Question, this))
            return;

        int okCount = 0;
        await Task.Run(() =>
        {
            foreach (var item in selected)
            {
                var (ok, msg) = _telemetryController.DisableItem(item);
                if (ok) okCount++;
                AppendLog($"[Telemetry] {msg}");
                item.IsSelected = false;
            }
        });

        await RefreshTelemetryAsync();
        StatusFooterText.Text = $"Hardened / removed {okCount}/{selected.Count} item(s).";
    }

    private async void TelRestoreSelectedBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = _telemetryItems.Where(t => t.IsSelected && !t.IsActive && t.Category != TelemetryCategory.AppxBloat).ToList();
        if (selected.Count == 0)
        {
            DarkDialog.Show("Select one or more hardened services, tasks, registry policies, or startup entries to restore.", "Telemetry & Debloat", DarkDialogKind.Info, this);
            return;
        }

        int okCount = 0;
        await Task.Run(() =>
        {
            foreach (var item in selected)
            {
                var (ok, msg) = _telemetryController.RestoreItem(item);
                if (ok) okCount++;
                AppendLog($"[Telemetry] {msg}");
                item.IsSelected = false;
            }
        });

        await RefreshTelemetryAsync();
        StatusFooterText.Text = $"Restored {okCount}/{selected.Count} item(s).";
    }

    private async void TelRowActionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not TelemetryItem item) return;
        if (item.Category == TelemetryCategory.AppxBloat && !item.IsActive) return;

        if (item.Category == TelemetryCategory.AppxBloat &&
            !DarkDialog.Confirm($"Remove Appx package '{item.DisplayName}' ({item.AppxPackageName})?", "Remove Appx Package", DarkDialogKind.Question, this))
            return;

        var (ok, msg) = await Task.Run(() => item.IsActive
            ? _telemetryController.DisableItem(item)
            : _telemetryController.RestoreItem(item));
        AppendLog($"[Telemetry] {msg}");
        StatusFooterText.Text = msg;
        await RefreshTelemetryAsync();
    }

    private async void TelRescanBtn_Click(object sender, RoutedEventArgs e)
    {
        StatusFooterText.Text = "Rescanning Windows telemetry, services, tasks, policies & Appx bloat...";
        await RefreshTelemetryAsync();
        StatusFooterText.Text = "Telemetry & Debloat scan complete.";
    }

    private async Task RefreshLiveMonitorAsync()
    {
        if (_monitorBusy) return;
        _monitorBusy = true;
        try
        {
            var conns = await Task.Run(() =>
            {
                var blocked = _telemetryController.GetBlockedExecutablePaths();
                return _networkMonitor.CaptureOutboundConnections(blocked);
            });
            _liveConnections = conns;
            Dispatcher.Invoke(ApplyMonitorFilters);
        }
        catch { }
        finally
        {
            _monitorBusy = false;
        }
    }

    private void MonitorFilter_Changed(object sender, EventArgs e)
    {
        ApplyMonitorFilters();
    }

    private void ApplyMonitorFilters()
    {
        if (LiveConnectionsGrid == null) return;
        var q = _liveConnections.AsEnumerable();
        var search = MonitorSearchBox?.Text?.Trim() ?? "";
        if (ChkMonitorFlaggedOnly?.IsChecked == true)
            q = q.Where(c => c.IsSuspectedTelemetry);

        if (!string.IsNullOrWhiteSpace(search))
        {
            q = q.Where(c =>
                c.ProcessName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.RemoteAddress.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.RemoteHost.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.TelemetryReason.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var list = q.ToList();
        LiveConnectionsGrid.ItemsSource = list;
        int flagged = _liveConnections.Count(c => c.IsSuspectedTelemetry);
        int blocked = _liveConnections.Count(c => c.IsFirewallBlocked);
        MonitorStatsText.Text = $"{_liveConnections.Count} outbound  •  {flagged} flagged  •  {blocked} blocked";
    }

    private void MonitorPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        _monitorPaused = !_monitorPaused;
        MonitorPauseBtn.Content = _monitorPaused ? "Resume" : "Pause";
        if (_monitorPaused)
            _monitorTimer.Stop();
        else if (MainTabs.SelectedItem == LiveMonitorTabItem)
            _monitorTimer.Start();
    }

    private async void MonitorRefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        await RefreshLiveMonitorAsync();
    }

    private async void MonitorFirewallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not LiveConnection conn) return;
        bool block = !conn.IsFirewallBlocked;
        var (ok, msg) = await Task.Run(() => _telemetryController.SetFirewallOutboundBlock(conn.ProcessPath, conn.ProcessName, block));
        AppendLog($"[Firewall] {msg}");
        StatusFooterText.Text = msg;
        if (ok) conn.IsFirewallBlocked = block;
        await RefreshLiveMonitorAsync();
    }

    private async void MonitorKillBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not LiveConnection conn) return;
        if (!DarkDialog.Confirm($"Terminate process '{conn.ProcessName}' (PID {conn.Pid})?", "Kill Process", DarkDialogKind.Question, this))
            return;

        try
        {
            using var proc = Process.GetProcessById(conn.Pid);
            proc.Kill(entireProcessTree: false);
            AppendLog($"[Monitor] Terminated process {conn.ProcessName} (PID {conn.Pid}).");
            StatusFooterText.Text = $"Terminated {conn.ProcessName} (PID {conn.Pid}).";
        }
        catch (Exception ex)
        {
            AppendLog($"[Monitor] Could not terminate {conn.ProcessName}: {ex.Message}");
            DarkDialog.Show($"Failed to terminate {conn.ProcessName}: {ex.Message}", "Kill Process", DarkDialogKind.Warning, this);
        }
        await RefreshLiveMonitorAsync();
    }
}
