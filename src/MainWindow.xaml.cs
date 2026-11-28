using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly PowerCfgService _powerCfg = new();
    private readonly PowerHardwareInspector _powerInspector;
    private readonly SmartPlanEngine _powerEngine;
    private readonly ContextMenuScanner _contextScanner = new();
    private readonly ContextMenuManager _contextManager = new();
    private readonly ObservableCollection<MenuEntry> _contextEntries = [];

    private AppSettings _settings = new();
    private SystemProfile _profile = new();
    private List<DriverDevice> _allDevices = new();
    private List<DriverStorePackage> _storePackages = new();
    private List<DriverUpdateCandidate> _allUpdates = new();
    private List<DeviceUpdateGroup> _updateGroups = new();
    private List<TelemetryItem> _telemetryItems = new();
    private List<LiveConnection> _liveConnections = new();
    private PowerHardwareProfile? _powerHw;
    private Dictionary<Guid, uint> _powerActiveValues = new();
    private PowerPlanConfig _powerConfig = new();
    private List<MenuEntry> _allContextEntries = [];
    private bool _suppressPowerUiEvents;
    private bool _monitorPaused;
    private bool _monitorBusy;
    private bool _loadingSettings;
    private bool _isBusy;

    public MainWindow()
    {
        _suppressPowerUiEvents = true;
        _settings = _configStore.Load();
        _powerInspector = new PowerHardwareInspector(_powerCfg);
        _powerEngine = new SmartPlanEngine(_powerCfg);
        InitializeComponent();
        ContextEntriesGrid.ItemsSource = _contextEntries;
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
            Width = Math.Min(1180, maxW);

        if (_settings.WindowHeight is >= 520 and <= 2160)
            Height = Math.Min(_settings.WindowHeight.Value, maxH);
        else
            Height = Math.Min(720, maxH);

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
        await Task.WhenAll(
            RefreshHardwareAndStoreAsync(),
            RefreshScheduledTaskStatusAsync(),
            RefreshTelemetryAsync(),
            RefreshPowerPlanAsync(initialLoad: true),
            RefreshContextEntriesAsync());
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

    // ==================== POWER PLAN (MERGED FROM xPowerPlan) ====================

    private async Task RefreshPowerPlanAsync(bool initialLoad = false)
    {
        SetPowerBusy(true, "Inspecting power hardware and active Windows power plans...");
        try
        {
            _powerHw = await _powerInspector.InspectAsync();
            _powerActiveValues = _powerHw.ActiveScheme != null
                ? await Task.Run(() => _powerCfg.QueryAllAcValues(_powerHw.ActiveScheme.Guid))
                : new Dictionary<Guid, uint>();

            RenderPowerHardwareCards(_powerHw);
            RenderPowerAudit(_powerHw, _powerActiveValues);
            PowerPlansGrid.ItemsSource = _powerHw.ExistingSchemes;

            if (initialLoad)
            {
                _powerConfig = _settings.PowerPlanLastConfig?.Clone()
                    ?? _powerEngine.CreatePreset(PresetKind.SmartDevAi, _powerHw);
                BindPowerConfigToUi(_powerConfig);
            }

            RefreshPowerDiff();
            AppendLog($"[PowerPlan] Active plan: {_powerHw.ActiveScheme?.Name ?? "Unknown"} ({_powerHw.ExistingSchemes.Count} plans found).");
            SetPowerBusy(false, $"Power plan inspected • {_powerHw.ExistingSchemes.Count} Windows power plans found.");
        }
        catch (Exception ex)
        {
            AppendLog($"[PowerPlan] Inspection error: {ex.Message}");
            SetPowerBusy(false, $"Power plan inspection error: {ex.Message}");
        }
    }

    private void RenderPowerHardwareCards(PowerHardwareProfile hw)
    {
        PowerActivePlanBadgeText.Text = hw.ActiveScheme?.Name ?? "Unknown";

        PowerHwCpuText.Text = hw.CpuName;
        var cpuNotes = $"{hw.PhysicalCores} Cores / {hw.LogicalProcessors} Threads  •  Base {hw.MaxClockMhz} MHz";
        if (hw.IsAmdCpu) cpuNotes += "  •  AMD CPPC2 aware";
        PowerHwCpuDetailText.Text = cpuNotes;

        PowerHwGpuText.Text = hw.Gpus.Count > 0 ? string.Join(" + ", hw.Gpus.Select(g => g.Name)) : "No GPU detected";
        var gpuDetail = hw.Gpus.FirstOrDefault() is { } g0 ? $"Driver {g0.DriverVersion}" : "";
        if (hw.HasDisabledAmdIgpu)
            gpuDetail += "  •  AMD Radeon iGPU disabled in BIOS (dead AMD graphics power settings skipped)";
        PowerHwGpuDetailText.Text = gpuDetail;

        PowerHwStorageText.Text = hw.Disks.Count > 0
            ? string.Join("\n", hw.Disks.Select(d => "• " + d.Summary))
            : "No physical disks enumerated";

        PowerHwPlatformText.Text = $"{hw.Motherboard}  •  {hw.TotalRamGb:F0} GB RAM  •  {(hw.HasBattery ? "Laptop (Battery)" : "Desktop (AC Only)")}";
        var sleepBits = new List<string>();
        if (hw.SupportsS3Standby) sleepBits.Add("S3 Standby");
        if (hw.SupportsS0LowPowerIdle) sleepBits.Add("S0 Modern Standby");
        if (hw.SupportsHibernate) sleepBits.Add("Hibernate");
        if (hw.HypervisorBlocksHybridSleep) sleepBits.Add("Hybrid Sleep off (Hypervisor)");
        PowerHwSleepText.Text = "Sleep States: " + string.Join(", ", sleepBits);
    }

    private void RenderPowerAudit(PowerHardwareProfile hw, Dictionary<Guid, uint> activeValues)
    {
        var findings = _powerEngine.AuditActivePlan(hw, activeValues);
        PowerAuditList.ItemsSource = findings;

        var tuneCount = findings.Count(f => f.Severity == PowerFindingSeverity.Warning);
        var okCount = findings.Count(f => f.Severity == PowerFindingSeverity.Good);
        PowerAuditSummaryText.Text = tuneCount > 0
            ? $"{tuneCount} tuning {(tuneCount == 1 ? "opportunity" : "opportunities")} found • {okCount} optimal"
            : $"Looking great! {okCount} checks optimal";
    }

    private void BindPowerConfigToUi(PowerPlanConfig cfg)
    {
        _suppressPowerUiEvents = true;
        try
        {
            PowerPlanNameBox.Text = cfg.PlanName;
            PowerReusePlanCheck.IsChecked = cfg.ReuseSingleManagedPlan;
            PowerBackupPrevCheck.IsChecked = cfg.AutoBackupPreviousPlan;
            PowerUnhideAdvancedCheck.IsChecked = cfg.UnhideUsefulAdvancedSettings;

            PowerDisplayOffBox.Text = cfg.DisplayOffMinutes.ToString();
            PowerSleepAfterBox.Text = cfg.SleepAfterMinutes.ToString();
            PowerHibernateAfterBox.Text = cfg.HibernateAfterMinutes.ToString();
            SelectPowerComboByTag(PowerWakeTimersCombo, cfg.WakeTimersMode);
            PowerRespectMediaCheck.IsChecked = cfg.RespectMediaKeepAwake;
            PowerRequirePassCheck.IsChecked = cfg.RequirePasswordOnWakeup;
            PowerAllowHybridCheck.IsChecked = cfg.AllowHybridSleep;

            SelectPowerComboByTag(PowerBoostModeCombo, cfg.BoostMode);
            PowerEppSlider.Value = cfg.EnergyPerfPreference;
            PowerEppValueText.Text = $"{cfg.EnergyPerfPreference}%";
            PowerMinCpuSlider.Value = cfg.MinProcessorState;
            PowerMinCpuValueText.Text = $"{cfg.MinProcessorState}%";
            PowerMaxCpuSlider.Value = cfg.MaxProcessorState;
            PowerMaxCpuValueText.Text = $"{cfg.MaxProcessorState}%";
            PowerCoreParkingSlider.Value = cfg.CoreParkingMinCores;
            PowerCoreParkingValueText.Text = $"{cfg.CoreParkingMinCores}%";
            SelectPowerComboByTag(PowerCoolingCombo, cfg.CoolingPolicy);

            SelectPowerComboByTag(PowerPcieCombo, cfg.PcieLinkStatePowerMgmt);
            SelectPowerComboByTag(PowerAhciCombo, cfg.AhciLinkPowerMgmt);
            PowerNvmeIdleBox.Text = cfg.PrimaryNvmeIdleMs.ToString();
            PowerNvmeLatBox.Text = cfg.PrimaryNvmeLatTolMs.ToString();
            PowerDiskOffBox.Text = cfg.DiskTurnOffMinutes.ToString();
            SelectPowerComboByTag(PowerUsb3Combo, cfg.Usb3LinkPowerMgmt);
            PowerUsbSuspendCheck.IsChecked = cfg.UsbSelectiveSuspend;

            HighlightPowerPresetButton(cfg.Preset);
        }
        finally
        {
            _suppressPowerUiEvents = false;
        }
    }

    private void ReadPowerUiIntoConfig()
    {
        _powerConfig.PlanName = string.IsNullOrWhiteSpace(PowerPlanNameBox.Text) ? "xPowerPlan — Custom" : PowerPlanNameBox.Text.Trim();
        _powerConfig.ReuseSingleManagedPlan = PowerReusePlanCheck.IsChecked == true;
        _powerConfig.AutoBackupPreviousPlan = PowerBackupPrevCheck.IsChecked == true;
        _powerConfig.UnhideUsefulAdvancedSettings = PowerUnhideAdvancedCheck.IsChecked == true;

        _powerConfig.DisplayOffMinutes = ParseNonNegativeInt(PowerDisplayOffBox.Text, 10);
        _powerConfig.SleepAfterMinutes = ParseNonNegativeInt(PowerSleepAfterBox.Text, 15);
        _powerConfig.HibernateAfterMinutes = ParseNonNegativeInt(PowerHibernateAfterBox.Text, 0);
        _powerConfig.WakeTimersMode = GetPowerComboTagInt(PowerWakeTimersCombo, 2);
        _powerConfig.RespectMediaKeepAwake = PowerRespectMediaCheck.IsChecked == true;
        _powerConfig.RequirePasswordOnWakeup = PowerRequirePassCheck.IsChecked == true;
        _powerConfig.AllowHybridSleep = PowerAllowHybridCheck.IsChecked == true;

        _powerConfig.BoostMode = GetPowerComboTagInt(PowerBoostModeCombo, 4);
        _powerConfig.EnergyPerfPreference = (int)Math.Round(PowerEppSlider.Value);
        _powerConfig.MinProcessorState = (int)Math.Round(PowerMinCpuSlider.Value);
        _powerConfig.MaxProcessorState = Math.Max(_powerConfig.MinProcessorState, (int)Math.Round(PowerMaxCpuSlider.Value));
        _powerConfig.CoreParkingMinCores = (int)Math.Round(PowerCoreParkingSlider.Value);
        _powerConfig.CoolingPolicy = GetPowerComboTagInt(PowerCoolingCombo, 1);

        _powerConfig.PcieLinkStatePowerMgmt = GetPowerComboTagInt(PowerPcieCombo, 0);
        _powerConfig.AhciLinkPowerMgmt = GetPowerComboTagInt(PowerAhciCombo, 0);
        _powerConfig.PrimaryNvmeIdleMs = ParseNonNegativeInt(PowerNvmeIdleBox.Text, 200);
        _powerConfig.PrimaryNvmeLatTolMs = ParseNonNegativeInt(PowerNvmeLatBox.Text, 15);
        _powerConfig.DiskTurnOffMinutes = ParseNonNegativeInt(PowerDiskOffBox.Text, 0);
        _powerConfig.Usb3LinkPowerMgmt = GetPowerComboTagInt(PowerUsb3Combo, 0);
        _powerConfig.UsbSelectiveSuspend = PowerUsbSuspendCheck.IsChecked == true;

        PowerEppValueText.Text = $"{_powerConfig.EnergyPerfPreference}%";
        PowerMinCpuValueText.Text = $"{_powerConfig.MinProcessorState}%";
        PowerMaxCpuValueText.Text = $"{_powerConfig.MaxProcessorState}%";
        PowerCoreParkingValueText.Text = $"{_powerConfig.CoreParkingMinCores}%";
    }

    private void RefreshPowerDiff()
    {
        var rows = _powerEngine.ComputeDiff(_powerConfig, _powerActiveValues);
        PowerDiffGrid.ItemsSource = rows;
        var changes = rows.Count(r => r.IsChanged);
        PowerDiffSummaryText.Text = $"{changes} of {rows.Count} settings differ from your current active plan ({_powerHw?.ActiveScheme?.Name ?? "Unknown"}).";
    }

    private void PowerOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPowerUiEvents || !IsLoaded) return;
        ReadPowerUiIntoConfig();
        RefreshPowerDiff();
    }

    private void ApplyPowerPreset(PresetKind kind)
    {
        if (_powerHw == null) return;
        _powerConfig = _powerEngine.CreatePreset(kind, _powerHw);
        _powerConfig.ReuseSingleManagedPlan = PowerReusePlanCheck.IsChecked == true;
        _powerConfig.AutoBackupPreviousPlan = PowerBackupPrevCheck.IsChecked == true;
        _powerConfig.UnhideUsefulAdvancedSettings = PowerUnhideAdvancedCheck.IsChecked == true;
        BindPowerConfigToUi(_powerConfig);
        RefreshPowerDiff();
        StatusFooterText.Text = $"Loaded '{_powerConfig.PlanName}' preset tailored to your hardware.";
    }

    private void HighlightPowerPresetButton(PresetKind kind)
    {
        var normalBg = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#252A36")!;
        var activeBg = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#1E3A6D")!;
        PresetDevBtn.Background = kind == PresetKind.SmartDevAi ? activeBg : normalBg;
        PresetPerfBtn.Background = kind == PresetKind.MaxPerformance ? activeBg : normalBg;
        PresetQuietBtn.Background = kind == PresetKind.CoolAndQuiet ? activeBg : normalBg;
        PresetCustomBtn.Background = kind == PresetKind.Custom ? activeBg : normalBg;
    }

    private void PresetDevBtn_Click(object sender, RoutedEventArgs e) => ApplyPowerPreset(PresetKind.SmartDevAi);
    private void PresetPerfBtn_Click(object sender, RoutedEventArgs e) => ApplyPowerPreset(PresetKind.MaxPerformance);
    private void PresetQuietBtn_Click(object sender, RoutedEventArgs e) => ApplyPowerPreset(PresetKind.CoolAndQuiet);
    private void PresetCustomBtn_Click(object sender, RoutedEventArgs e)
    {
        _powerConfig.Preset = PresetKind.Custom;
        HighlightPowerPresetButton(PresetKind.Custom);
        StatusFooterText.Text = "Custom power mode — adjust any option below.";
    }

    private async void PowerRefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshPowerPlanAsync();

    private async void PowerApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_powerHw == null) return;
        ReadPowerUiIntoConfig();

        PowerSubTabs.SelectedIndex = 2; // Diff & Enforce
        SetPowerBusy(true, $"Generating and enforcing '{_powerConfig.PlanName}'...");
        PowerLogBox.Text = "Applying settings via Windows powercfg...\n";

        var backupDir = Path.Combine(_settings.BackupDirectory, "PowerPlans");
        var result = await _powerEngine.GenerateAndApplyAsync(_powerConfig, _powerHw, _settings.PowerPlanManagedSchemeGuid, backupDir);
        PowerLogBox.Text = string.Join(Environment.NewLine, result.Logs);
        AppendLog($"[PowerPlan] {result.Summary}");

        if (result.Success)
        {
            _settings.PowerPlanManagedSchemeGuid = result.AppliedSchemeGuid;
            _settings.PowerPlanLastConfig = _powerConfig.Clone();
            _settings.PowerPlanLastAppliedUtc = DateTime.UtcNow;
            _configStore.Save(_settings);

            await RefreshPowerPlanAsync(initialLoad: false);
            StatusFooterText.Text = result.Summary;
            DarkDialog.Show(result.Summary, "Power Plan Enforced", DarkDialogKind.Success, this);
        }
        else
        {
            SetPowerBusy(false, result.Summary);
            DarkDialog.Show(result.Summary, "Power Plan Error", DarkDialogKind.Error, this);
        }
    }

    private void PowerSubTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == PowerSubTabs && PowerSubTabs.SelectedIndex == 2 && _powerHw != null)
        {
            ReadPowerUiIntoConfig();
            RefreshPowerDiff();
        }
    }

    private async void PowerSetActivePlanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PowerPlansGrid.SelectedItem is not PowerSchemeInfo plan) return;
        var (ok, outMsg) = _powerCfg.SetActiveScheme(plan.Guid);
        if (!ok)
        {
            DarkDialog.Show($"Failed to activate '{plan.Name}':\n{outMsg}", "Power Plan", DarkDialogKind.Error, this);
            return;
        }
        await RefreshPowerPlanAsync();
        StatusFooterText.Text = $"Switched active power plan to '{plan.Name}'.";
    }

    private void PowerExportPlanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PowerPlansGrid.SelectedItem is not PowerSchemeInfo plan) return;
        var dlg = new SaveFileDialog
        {
            Title = "Export Power Plan Backup",
            Filter = "Windows Power Plan (*.pow)|*.pow",
            FileName = $"{string.Concat(plan.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'))}.pow"
        };
        if (dlg.ShowDialog() == true)
        {
            var (ok, outMsg) = _powerCfg.ExportScheme(plan.Guid, dlg.FileName);
            StatusFooterText.Text = ok ? $"Exported '{plan.Name}' to {dlg.FileName}" : $"Export failed: {outMsg}";
            AppendLog($"[PowerPlan] {StatusFooterText.Text}");
        }
    }

    private async void PowerDeletePlanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PowerPlansGrid.SelectedItem is not PowerSchemeInfo plan) return;
        if (plan.IsBuiltIn)
        {
            DarkDialog.Show("Built-in Windows power plans cannot be deleted.", "Power Plan", DarkDialogKind.Warning, this);
            return;
        }
        if (plan.IsActive)
        {
            DarkDialog.Show("Switch to another power plan before deleting the currently active plan.", "Power Plan", DarkDialogKind.Warning, this);
            return;
        }
        if (!DarkDialog.Confirm($"Delete power plan '{plan.Name}'?\n\nGUID: {plan.Guid}", "Confirm Delete", DarkDialogKind.Question, this))
            return;

        var (ok, outMsg) = _powerCfg.DeleteScheme(plan.Guid);
        if (!ok)
        {
            DarkDialog.Show($"Failed to delete plan:\n{outMsg}", "Power Plan", DarkDialogKind.Error, this);
            return;
        }
        await RefreshPowerPlanAsync();
        StatusFooterText.Text = $"Deleted power plan '{plan.Name}'.";
    }

    private void SetPowerBusy(bool busy, string message)
    {
        PowerRefreshBtn.IsEnabled = !busy;
        PowerQuickApplyBtn.IsEnabled = !busy;
        if (PowerEnforceBtn != null) PowerEnforceBtn.IsEnabled = !busy;
        if (!_isBusy) StatusFooterText.Text = message;
    }

    private static void SelectPowerComboByTag(ComboBox combo, int tagVal)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (int.TryParse(item.Tag?.ToString(), out var t) && t == tagVal)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static int GetPowerComboTagInt(ComboBox combo, int fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var t))
            return t;
        return fallback;
    }

    private static int ParseNonNegativeInt(string? text, int fallback)
        => int.TryParse(text?.Trim(), out var v) && v >= 0 ? v : fallback;

    // ==================== CONTEXT MENUS, STARTUP, INSTALLED APPS & WIN+X (xContextGuard) ====================

    private MenuEntry? SelectedContextEntry => ContextEntriesGrid?.SelectedItem as MenuEntry;

    private async Task RefreshContextEntriesAsync()
    {
        if (ContextRefreshBtn != null) ContextRefreshBtn.IsEnabled = false;
        try
        {
            var scanned = await Task.Run(() => _contextScanner.Scan());
            _allContextEntries = scanned;
            Dispatcher.Invoke(() =>
            {
                ApplyContextFilters();
                ContextStatusText.Text = $"Found {_allContextEntries.Count} entries. Select one to manage it.";
                AppendLog($"[ContextGuard] Scan complete: {_allContextEntries.Count} entries across context menus, startup, Win+X, and installed apps.");
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                ContextStatusText.Text = "Scan failed.";
                AppendLog($"[ContextGuard] Scan error: {ex.Message}");
            });
        }
        finally
        {
            if (ContextRefreshBtn != null) ContextRefreshBtn.IsEnabled = true;
        }
    }

    private void ApplyContextFilters()
    {
        if (ContextEntriesGrid == null) return;
        var query = ContextSearchBox?.Text?.Trim() ?? "";
        var selectedLocation = (ContextLocationFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All locations";
        var filtered = _allContextEntries.Where(e =>
            string.IsNullOrWhiteSpace(query) ||
            e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            e.Command.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            e.RegistryPath.Contains(query, StringComparison.OrdinalIgnoreCase));

        filtered = selectedLocation switch
        {
            "Installed applications" => filtered
                .Where(e => e.Kind == EntryKind.InstalledApp)
                .OrderByDescending(e => e.IsMissingFiles)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            "Installed apps (Missing files)" => filtered
                .Where(e => e.Kind == EntryKind.InstalledApp && e.IsMissingFiles)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            "Startup items (All)" => filtered.Where(e => e.IsStartup),
            "Startup (Registry)" => filtered.Where(e => e.Kind == EntryKind.StartupRegistry),
            "Startup (Folders)" => filtered.Where(e => e.Kind == EntryKind.StartupFolder),
            "Win+X menu" => filtered.Where(e => e.Kind == EntryKind.WinXShortcut),
            "File context menu" => filtered.Where(e => e.Location.StartsWith("*", StringComparison.Ordinal)),
            "Folder context menu" => filtered.Where(e => e.Location is "Directory\\shell" or "Folder\\shell"),
            "Folder background" => filtered.Where(e => e.Location.Contains("Background", StringComparison.OrdinalIgnoreCase)),
            "Drive context menu" => filtered.Where(e => e.Location.StartsWith("Drive", StringComparison.OrdinalIgnoreCase)),
            "Desktop background" => filtered.Where(e => e.Location.StartsWith("DesktopBackground", StringComparison.OrdinalIgnoreCase)),
            _ => filtered
        };

        _contextEntries.Clear();
        foreach (var entry in filtered) _contextEntries.Add(entry);
        ContextStatusText.Text = $"Showing {_contextEntries.Count} of {_allContextEntries.Count} entries.";
    }

    private void ContextSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) ApplyContextFilters();
    }

    private void ContextLocationFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyContextFilters();
    }

    private async void ContextRefresh_Click(object sender, RoutedEventArgs e) => await RefreshContextEntriesAsync();

    private void ContextRestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (!DarkDialog.Confirm(
            "Restart Windows Explorer now to reload the taskbar and Win+X menu?",
            "Restart Explorer", DarkDialogKind.Question, this))
            return;

        RunContextOperation(() =>
        {
            _contextManager.RestartExplorer();
            ContextStatusText.Text = "Windows Explorer restarted.";
            AppendLog("[ContextGuard] Restarted Windows Explorer.");
        });
    }

    private void ContextEdit_Click(object sender, RoutedEventArgs e)
    {
        var entry = SelectedContextEntry;
        if (entry is null)
        {
            DarkDialog.Show("Select an entry first to edit it.", "Nothing Selected", DarkDialogKind.Info, this);
            return;
        }

        if (entry.Kind == EntryKind.ContextMenuHandler)
        {
            DarkDialog.Show(
                "Shell extension handlers (COM CLSIDs) cannot be edited directly.\nUse Enable or Disable to toggle them, or Delete to remove them.",
                "Cannot Edit Shell Extension", DarkDialogKind.Info, this);
            return;
        }

        var dialog = new EditEntryDialog(entry) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        RunContextOperation(() =>
        {
            if (entry.Kind == EntryKind.WinXShortcut)
            {
                _contextManager.SaveWinXShortcut(
                    originalPath: entry.RegistryPath,
                    displayName: dialog.EntryName,
                    groupName: dialog.SelectedGroup,
                    targetPath: dialog.TargetPath,
                    arguments: dialog.Arguments,
                    runAsAdmin: dialog.RunAsAdmin);

                _ = RefreshContextEntriesAsync();
                AppendLog($"[ContextGuard] Updated Win+X entry '{dialog.EntryName}'.");
                PromptContextRestartExplorer($"Updated Win+X entry \"{dialog.EntryName}\".");
            }
            else if (entry.IsStartup)
            {
                var backup = _contextManager.SaveStartupEntry(
                    entry,
                    dialog.EntryName,
                    dialog.TargetPath,
                    dialog.Arguments,
                    dialog.RunAsAdmin);
                _ = RefreshContextEntriesAsync();
                ContextStatusText.Text = $"Updated startup entry \"{dialog.EntryName}\". Backup: {Path.GetFileName(backup)}";
                AppendLog($"[ContextGuard] {ContextStatusText.Text}");
            }
            else if (entry.Kind == EntryKind.InstalledApp)
            {
                var backup = _contextManager.SaveInstalledApp(entry, dialog.EntryName, dialog.TargetPath);
                _ = RefreshContextEntriesAsync();
                ContextStatusText.Text = $"Updated installed app entry \"{dialog.EntryName}\". Backup: {Path.GetFileName(backup)}";
                AppendLog($"[ContextGuard] {ContextStatusText.Text}");
            }
            else if (entry.Kind == EntryKind.StaticVerb)
            {
                var backup = _contextManager.SaveStaticVerb(entry, dialog.EntryName, dialog.TargetPath);
                _ = RefreshContextEntriesAsync();
                ContextStatusText.Text = $"Updated command. Backup: {Path.GetFileName(backup)}";
                AppendLog($"[ContextGuard] {ContextStatusText.Text}");
            }
        });
    }

    private void ContextEntriesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedContextEntry != null)
            ContextEdit_Click(sender, e);
    }

    private void ContextEnable_Click(object sender, RoutedEventArgs e) => ChangeSelectedContextEntry(enabled: true);
    private void ContextDisable_Click(object sender, RoutedEventArgs e) => ChangeSelectedContextEntry(enabled: false);

    private void ChangeSelectedContextEntry(bool enabled)
    {
        var entry = SelectedContextEntry;
        if (entry is null)
        {
            DarkDialog.Show("Select an entry first.", "Nothing Selected", DarkDialogKind.Info, this);
            return;
        }

        var action = enabled ? "enable" : "disable";
        bool alreadyInTargetState = entry.Kind == EntryKind.InstalledApp
            ? entry.IsHiddenApp == !enabled
            : entry.IsDisabled == !enabled;

        if (alreadyInTargetState)
        {
            DarkDialog.Show($"\"{entry.Name}\" is already {(enabled ? "enabled" : "disabled")}.", "No Change Needed", DarkDialogKind.Info, this);
            return;
        }

        if (!DarkDialog.Confirm(
            $"Back up and {action} this entry?\n\n{entry.Name}\n{entry.RegistryPath}",
            $"Confirm {action}", DarkDialogKind.Question, this))
            return;

        RunContextOperation(() =>
        {
            var backup = _contextManager.SetEnabled(entry, enabled);
            _ = RefreshContextEntriesAsync();
            var msg = $"Entry {(enabled ? "enabled" : "disabled")}. Backup: {Path.GetFileName(backup)}";
            AppendLog($"[ContextGuard] {entry.Name}: {msg}");
            if (entry.Kind == EntryKind.WinXShortcut)
                PromptContextRestartExplorer(msg);
            else
                ContextStatusText.Text = msg;
        });
    }

    private void ContextDelete_Click(object sender, RoutedEventArgs e)
    {
        var entry = SelectedContextEntry;
        if (entry is null)
        {
            DarkDialog.Show("Select an entry first.", "Nothing Selected", DarkDialogKind.Info, this);
            return;
        }

        var targetDescription = entry.Kind switch
        {
            EntryKind.WinXShortcut => "this Win+X shortcut file",
            EntryKind.StartupRegistry => "this startup registry entry",
            EntryKind.StartupFolder => "this startup folder item",
            EntryKind.InstalledApp => "this installed application (Uninstall) registry entry",
            _ => "this registry registration"
        };

        if (!DarkDialog.Confirm(
            $"Permanently delete {targetDescription}? A backup will be created first.\n\n{entry.Name}\n{entry.RegistryPath}\n\nThis can affect Explorer or the associated application.",
            "Confirm Permanent Deletion", DarkDialogKind.Warning, this))
            return;

        RunContextOperation(() =>
        {
            var backup = _contextManager.Delete(entry);
            _ = RefreshContextEntriesAsync();
            var msg = $"Deleted entry \"{entry.Name}\". Backup: {Path.GetFileName(backup)}";
            AppendLog($"[ContextGuard] {msg}");
            if (entry.Kind == EntryKind.WinXShortcut)
                PromptContextRestartExplorer(msg);
            else
                ContextStatusText.Text = msg;
        });
    }

    private void PromptContextRestartExplorer(string message)
    {
        ContextStatusText.Text = message;
        if (DarkDialog.Confirm(
            $"{message}\n\nWindows Explorer caches Win+X menu items. Would you like to restart Explorer now to apply the changes immediately?",
            "Restart Windows Explorer?", DarkDialogKind.Question, this))
        {
            _contextManager.RestartExplorer();
            ContextStatusText.Text = message + " Windows Explorer restarted.";
            AppendLog("[ContextGuard] Restarted Windows Explorer.");
        }
    }

    private void ContextBackup_Click(object sender, RoutedEventArgs e)
    {
        var entry = SelectedContextEntry;
        if (entry is null)
        {
            DarkDialog.Show("Select an entry first.", "Nothing Selected", DarkDialogKind.Info, this);
            return;
        }

        RunContextOperation(() =>
        {
            var backup = _contextManager.CreateBackup(entry);
            ContextStatusText.Text = $"Backup created: {backup}";
            AppendLog($"[ContextGuard] Manual backup created: {backup}");
            DarkDialog.Show($"Backup created successfully:\n{backup}", "Backup Complete", DarkDialogKind.Success, this);
        });
    }

    private void ContextRestore_Click(object sender, RoutedEventArgs e)
    {
        var initialDir = Directory.Exists(_contextManager.BackupDirectory)
            ? _contextManager.BackupDirectory
            : (Directory.Exists(_contextManager.LegacyBackupDirectory)
                ? _contextManager.LegacyBackupDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        var dialog = new OpenFileDialog
        {
            Title = "Choose a ContextGuard Backup",
            Filter = "ContextGuard backups (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = initialDir
        };
        if (dialog.ShowDialog(this) != true) return;

        if (!DarkDialog.Confirm(
            $"Restore the selected backup?\n\n{dialog.FileName}",
            "Confirm Restore", DarkDialogKind.Warning, this))
            return;

        RunContextOperation(() =>
        {
            _contextManager.Restore(dialog.FileName);
            _ = RefreshContextEntriesAsync();
            var msg = $"Restored backup: {Path.GetFileName(dialog.FileName)}";
            AppendLog($"[ContextGuard] {msg}");
            PromptContextRestartExplorer(msg);
        });
    }

    private void RunContextOperation(Action action)
    {
        try
        {
            action();
        }
        catch (UnauthorizedAccessException ex)
        {
            DarkDialog.Show($"Windows denied access to this location. Ensure xSystemForge is running as administrator.\n\n{ex.Message}", "Access Denied", DarkDialogKind.Warning, this);
        }
        catch (System.Security.SecurityException ex)
        {
            DarkDialog.Show(ex.Message, "Security Error", DarkDialogKind.Error, this);
        }
        catch (Exception ex)
        {
            DarkDialog.Show(ex.Message, "Operation Failed", DarkDialogKind.Error, this);
        }
    }
}
