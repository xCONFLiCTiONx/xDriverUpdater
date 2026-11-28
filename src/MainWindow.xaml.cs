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
using xScanner.Core.Hardening;
using xScanner.Core.ScanEngine;
using xScanner.Core.ScanScheduler;
using xScanner.Database;
using xScanner.Database.Models;
using xScanner.Engines.ClamAV;
using xScanner.Quarantine;

namespace xDriverUpdater;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

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
    private readonly PrivacyGuardMonitorService _pgMonitor = new();
    private readonly NetworkDiagnosticsService _netDiag = new();
    private readonly DispatcherTimer _watchdogTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _driveTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private readonly ScanDatabase _scanDb = new();
    private readonly ClamAvManager _clamManager;
    private readonly ScanOrchestrator _scanOrchestrator;
    private readonly QuarantineManager _quarantineManager;
    private readonly HardeningManager _hardeningManager = new();
    private CancellationTokenSource? _malwareScanCts;
    private bool _isMalwareScanning;
    private List<HardeningCheckResult> _hardeningChecks = new();

    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayIconGreen;
    private System.Drawing.Icon? _trayIconOrange;
    private System.Drawing.Icon? _trayIconRed;
    private bool _isExplicitExit;
    private bool _trayNotifiedOnce;
    private bool _startMinimized;
    private bool _initialCheckDone;
    private bool _isResolvingIssue;
    private long _activeMalwareThreats;
    private string _primaryResolveAction = "";
    private bool? _lastOnlineState;
    private DateTime _lastPgAlertUtc = DateTime.MinValue;
    private PrivacyGuardStatus _lastPgStatus = new();
    private List<PhysicalDriveInfo> _physicalDrives = new();

    private static readonly System.Windows.Media.Imaging.BitmapImage BrandGreenPng = new(new Uri("pack://application:,,,/xSecurityCenter;component/Assets/xsecurity-green.png"));
    private static readonly System.Windows.Media.Imaging.BitmapImage BrandOrangePng = new(new Uri("pack://application:,,,/xSecurityCenter;component/Assets/xsecurity-orange.png"));
    private static readonly System.Windows.Media.Imaging.BitmapImage BrandRedPng = new(new Uri("pack://application:,,,/xSecurityCenter;component/Assets/xsecurity-red.png"));
    private static readonly System.Windows.Media.Imaging.BitmapImage WindowGreenIco = new(new Uri("pack://application:,,,/xSecurityCenter;component/Assets/xsecurity-green.ico"));
    private static readonly System.Windows.Media.Imaging.BitmapImage WindowOrangeIco = new(new Uri("pack://application:,,,/xSecurityCenter;component/Assets/xsecurity-orange.ico"));
    private static readonly System.Windows.Media.Imaging.BitmapImage WindowRedIco = new(new Uri("pack://application:,,,/xSecurityCenter;component/Assets/xsecurity-red.ico"));

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

    private string NetworkRollbackPath => Path.Combine(_configStore.ConfigDir, "network-rollback.json");

    public MainWindow(bool startMinimized = false)
    {
        _startMinimized = startMinimized;
        _suppressPowerUiEvents = true;
        _settings = _configStore.Load();
        _powerInspector = new PowerHardwareInspector(_powerCfg);
        _powerEngine = new SmartPlanEngine(_powerCfg);
        _clamManager = new ClamAvManager(_scanDb);
        _scanOrchestrator = new ScanOrchestrator(_scanDb, _clamManager);
        _quarantineManager = new QuarantineManager(_scanDb);
        InitializeComponent();
        ContextEntriesGrid.ItemsSource = _contextEntries;
        RestoreWindowPlacement();
        InitializeTrayIcon();
        _monitorTimer.Tick += async (_, _) => await RefreshLiveMonitorAsync();
        _watchdogTimer.Tick += async (_, _) => await RunBackgroundWatchdogTickAsync();
        _driveTimer.Tick += async (_, _) => await RefreshDrivesAsync(fromBackground: true);
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    public void BringToForeground()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            int captionColor = 0x00181515; // #151518 (COLORREF 0x00BBGGRR)
            int textColor = 0x00F5F4F4;    // #F4F4F5
            int borderColor = 0x00332C2C;  // #2C2C33
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
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
        SaveWindowPlacement();
        if (!_isExplicitExit && _settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            _monitorTimer.Stop();
            ShowInTaskbar = false;
            Hide();
            if (!_trayNotifiedOnce)
            {
                _trayNotifiedOnce = true;
                ShowTrayBalloon("xSecurityCenter Active", "Staying running in the system tray to monitor xPrivacyGuard, network health, and drives.", System.Windows.Forms.ToolTipIcon.Info);
            }
            return;
        }

        _monitorTimer.Stop();
        _watchdogTimer.Stop();
        _driveTimer.Stop();
        try { _malwareScanCts?.Cancel(); _clamManager.KillActiveProcesses(); } catch { }
        DisposeTrayIcon();
    }

    private void SaveWindowPlacement()
    {
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

        _watchdogTimer.Start();
        _driveTimer.Start();
        RefreshScannerMetaUi();

        await Task.WhenAll(
            RefreshHardwareAndStoreAsync(),
            RefreshScheduledTaskStatusAsync(),
            RefreshTelemetryAsync(),
            RefreshPowerPlanAsync(initialLoad: true),
            RefreshContextEntriesAsync(),
            RefreshDrivesAsync(fromBackground: false),
            RunBackgroundWatchdogTickAsync(initial: true));

        _initialCheckDone = true;
        UpdateDrawerOverallHealth();
    }

    private void ApplySettingsToUi()
    {
        _loadingSettings = true;
        ChkStartWithWindows.IsChecked = SecurityStartupManager.IsStartupEnabled();
        ChkMinimizeToTray.IsChecked = _settings.MinimizeToTrayOnClose;
        ChkMonitorPrivacyGuard.IsChecked = _settings.MonitorPrivacyGuard;
        ChkAutoNetDiag.IsChecked = _settings.AutoRunNetworkDiagnosticsOnOffline;
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

        StaticEthIpBox.Text = _settings.StaticEthIp;
        StaticWifiIpBox.Text = _settings.StaticWifiIp;
        StaticSubnetBox.Text = _settings.StaticSubnetMask;
        StaticGatewayBox.Text = _settings.StaticGateway;
        StaticPrimaryDnsBox.Text = _settings.StaticPrimaryDns;
        StaticSecondaryDnsBox.Text = _settings.StaticSecondaryDns;
        UpdateStaticRollbackUi();
        _loadingSettings = false;
    }

    private void SettingCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.MinimizeToTrayOnClose = ChkMinimizeToTray.IsChecked == true;
        _settings.MonitorPrivacyGuard = ChkMonitorPrivacyGuard.IsChecked == true;
        _settings.AutoRunNetworkDiagnosticsOnOffline = ChkAutoNetDiag.IsChecked == true;
        _settings.IncludeWindowsUpdateChannel = ChkUseWua.IsChecked == true;
        _settings.IncludeDirectManufacturerChecks = ChkUseDirect.IsChecked == true;
        _settings.IncludeCustomRepositories = ChkUseRepos.IsChecked == true;
        _settings.PreferOemDriversOnLaptops = ChkPreferLaptopOem.IsChecked == true;
        _settings.CreateRestorePointBeforeInstall = ChkRestorePoint.IsChecked == true;
        _settings.BackupDriverBeforeInstall = ChkAutoBackup.IsChecked == true;
        _settings.EnableLocalToastOnFailure = ChkLocalToastOnFailure.IsChecked == true;
        _configStore.Save(_settings);
        UpdateDrawerOverallHealth();
    }

    private void SecurityStartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        bool want = ChkStartWithWindows.IsChecked == true;
        var (ok, msg) = SecurityStartupManager.SetStartup(want);
        AppendLog($"[Startup] {msg}");
        StatusFooterText.Text = msg;
        if (!ok)
        {
            _loadingSettings = true;
            ChkStartWithWindows.IsChecked = SecurityStartupManager.IsStartupEnabled();
            _loadingSettings = false;
            DarkDialog.Show($"Could not update the Windows Task Scheduler logon entry:\n\n{msg}", "Startup Task", DarkDialogKind.Warning, this);
        }
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
        UpdateDrawerOverallHealth();
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
                NavDrivers.IsChecked = true;
            DriversSubTabs.SelectedIndex = 0;
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
        _settings.XSecurityEndpointUrl = XSecurityUrlBox.Text?.Trim() ?? "";
        _settings.EnableLocalToastOnFailure = ChkLocalToastOnFailure.IsChecked == true;
        _configStore.Save(_settings);
        UpdateDrawerOverallHealth();
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

    // ==================== NAVIGATION DRAWER ====================

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton rb || rb.Tag is not string tag) return;
        SwitchView(tag);
    }

    private void SwitchView(string tag)
    {
        DashboardView.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        ScannerView.Visibility = tag == "Scanner" ? Visibility.Visible : Visibility.Collapsed;
        NetworkView.Visibility = tag == "Network" ? Visibility.Visible : Visibility.Collapsed;
        DrivesView.Visibility = tag == "Drives" ? Visibility.Visible : Visibility.Collapsed;
        DriversSubTabs.Visibility = tag == "Drivers" ? Visibility.Visible : Visibility.Collapsed;
        TelemetryView.Visibility = tag == "Telemetry" ? Visibility.Visible : Visibility.Collapsed;
        LiveMonitorView.Visibility = tag == "Monitor" ? Visibility.Visible : Visibility.Collapsed;
        PowerView.Visibility = tag == "Power" ? Visibility.Visible : Visibility.Collapsed;
        ContextView.Visibility = tag == "Context" ? Visibility.Visible : Visibility.Collapsed;

        PageTitleText.Text = tag switch
        {
            "Scanner" => "Malware Scanner & Hardening",
            "Network" => "Network Diagnostics & Static IP",
            "Drives" => "Drive Sentinel",
            "Drivers" => "Drivers, Devices & Store",
            "Telemetry" => "Telemetry & Debloat",
            "Monitor" => "Live Outbound Monitor",
            "Power" => "Smart Power Plan",
            "Context" => "Context Menus, Startup & Win+X",
            _ => "Security Center"
        };

        if (tag == "Monitor" && !_monitorPaused)
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
                NavTelemetryLabel.Text = activeCount > 0
                    ? $"Telemetry ({activeCount})"
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
        else if (LiveMonitorView.Visibility == Visibility.Visible)
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
        var normalBg = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#222227")!;
        var activeBg = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#2B1B0E")!;
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
            DarkDialog.Show($"Windows denied access to this location. Ensure xSecurityCenter is running as administrator.\n\n{ex.Message}", "Access Denied", DarkDialogKind.Warning, this);
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

    // ==================== SYSTEM TRAY & BACKGROUND WATCHDOG ====================

    private void InitializeTrayIcon()
    {
        try
        {
            var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
            _trayIconGreen = LoadTrayIcon(Path.Combine(assetsDir, "xsecurity-green.ico"), Path.Combine(AppContext.BaseDirectory, "icon.ico"));
            _trayIconOrange = LoadTrayIcon(Path.Combine(assetsDir, "xsecurity-orange.ico"), Path.Combine(AppContext.BaseDirectory, "icon.ico"));
            _trayIconRed = LoadTrayIcon(Path.Combine(assetsDir, "xsecurity-red.ico"), Path.Combine(AppContext.BaseDirectory, "icon.ico"));

            var menu = new System.Windows.Forms.ContextMenuStrip
            {
                BackColor = System.Drawing.Color.FromArgb(0x1A, 0x1A, 0x1E),
                ForeColor = System.Drawing.Color.FromArgb(0xF4, 0xF4, 0xF5),
                ShowImageMargin = false,
                Font = new System.Drawing.Font("Segoe UI", 9.5f),
                Renderer = new DarkTrayMenuRenderer()
            };
            menu.Items.Add("Open xSecurityCenter", null, (_, _) => Dispatcher.Invoke(BringToForeground));
            menu.Items.Add("Run Offline Network Diagnostics", null, (_, _) => Dispatcher.Invoke(() =>
            {
                BringToForeground();
                NavNetwork.IsChecked = true;
                _ = RunNetworkDiagnosticsAsync();
            }));
            menu.Items.Add("Scan Drives Now", null, (_, _) => Dispatcher.Invoke(() => _ = RefreshDrivesAsync(false)));
            menu.Items.Add("Launch xPrivacyGuard", null, (_, _) => Dispatcher.Invoke(() => DashLaunchPgBtn_Click(this, new RoutedEventArgs())));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit xSecurityCenter", null, (_, _) => Dispatcher.Invoke(() =>
            {
                _isExplicitExit = true;
                Close();
                System.Windows.Application.Current.Shutdown();
            }));

            foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
            {
                item.BackColor = System.Drawing.Color.FromArgb(0x1A, 0x1A, 0x1E);
                item.ForeColor = System.Drawing.Color.FromArgb(0xF4, 0xF4, 0xF5);
            }

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = _trayIconOrange ?? _trayIconGreen ?? System.Drawing.SystemIcons.Shield,
                Text = "xSecurityCenter — Starting up...",
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(BringToForeground);
        }
        catch { }
    }

    private static System.Drawing.Icon LoadTrayIcon(string primaryPath, string fallbackPath)
    {
        if (File.Exists(primaryPath)) return new System.Drawing.Icon(primaryPath);
        if (File.Exists(fallbackPath)) return new System.Drawing.Icon(fallbackPath);
        return System.Drawing.SystemIcons.Shield;
    }

    private void DisposeTrayIcon()
    {
        try
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            _trayIconGreen?.Dispose();
            _trayIconOrange?.Dispose();
            _trayIconRed?.Dispose();
        }
        catch { }
    }

    private void ShowTrayBalloon(string title, string message, System.Windows.Forms.ToolTipIcon icon)
    {
        try
        {
            _trayIcon?.ShowBalloonTip(5000, title, message, icon);
        }
        catch { }
    }

    private async Task RunBackgroundWatchdogTickAsync(bool initial = false)
    {
        try
        {
            var pg = await Task.Run(() => _pgMonitor.CheckStatus(_settings.PrivacyGuardExePath));
            _lastPgStatus = pg;
            bool isOnline = await _netDiag.QuickInternetCheckAsync();
            var adapters = await Task.Run(() => _netDiag.GetAdapters(physicalOnly: false));

            Dispatcher.Invoke(() =>
            {
                UpdatePrivacyGuardUi(pg);
                UpdateNetworkSummaryUi(isOnline, adapters);
                if (NetAdaptersGrid.ItemsSource == null || initial)
                    NetAdaptersGrid.ItemsSource = adapters;
                UpdateDrawerOverallHealth();
            });

            if (_settings.MonitorPrivacyGuard && !pg.IsProcessRunning &&
                (DateTime.UtcNow - _lastPgAlertUtc).TotalMinutes >= 15)
            {
                _lastPgAlertUtc = DateTime.UtcNow;
                AppendLog("[Watchdog] WARNING: xPrivacyGuard is NOT running!");
                ShowTrayBalloon("xPrivacyGuard Stopped", "xPrivacyGuard is not running. Open xSecurityCenter to relaunch it.", System.Windows.Forms.ToolTipIcon.Warning);
            }

            if (_lastOnlineState == true && !isOnline && _settings.AutoRunNetworkDiagnosticsOnOffline)
            {
                AppendLog("[Watchdog] Network went OFFLINE — auto-running offline diagnostics...");
                var report = await _netDiag.RunOfflineDiagnosticsAsync(pg);
                Dispatcher.Invoke(() => RenderNetworkDiagnosticReport(report));
                ShowTrayBalloon("Network Offline — Diagnostics Ready", report.Headline, System.Windows.Forms.ToolTipIcon.Warning);
            }
            else if (initial)
            {
                var report = await _netDiag.RunOfflineDiagnosticsAsync(pg);
                Dispatcher.Invoke(() => RenderNetworkDiagnosticReport(report));
            }

            _lastOnlineState = isOnline;
        }
        catch { }
    }

    private void UpdatePrivacyGuardUi(PrivacyGuardStatus pg)
    {
        var bc = new System.Windows.Media.BrushConverter();
        DashPgBadge.Background = (System.Windows.Media.Brush)bc.ConvertFromString(pg.BadgeBg)!;
        DashPgBadgeText.Text = pg.BadgeText;
        DashPgBadgeText.Foreground = (System.Windows.Media.Brush)bc.ConvertFromString(pg.BadgeFg)!;
        DashPgTitle.Text = pg.Headline;
        DashPgDetail.Text = pg.IsProcessRunning
            ? (pg.IsTunnelUp
                ? $"PID {pg.ProcessId} • Tunnel IP {pg.TunnelIp}"
                : $"PID {pg.ProcessId} • Process running, tunnel currently idle")
            : $"Not detected. Expected: {pg.ExecutablePath}";
        DrawerPgStatusText.Text = pg.IsProcessRunning
            ? (pg.IsTunnelUp ? "xPrivacyGuard: Protected" : "xPrivacyGuard: Idle")
            : "xPrivacyGuard: STOPPED";
        DrawerPgStatusText.Foreground = (System.Windows.Media.Brush)bc.ConvertFromString(pg.BadgeFg)!;
    }

    private void UpdateNetworkSummaryUi(bool isOnline, List<NetworkAdapterSnapshot> adapters)
    {
        var bc = new System.Windows.Media.BrushConverter();
        var active = adapters.FirstOrDefault(a => a.AdapterKind is "Ethernet" or "Wi-Fi" && a.Status.Equals("Up", StringComparison.OrdinalIgnoreCase));
        DashNetBadge.Background = (System.Windows.Media.Brush)bc.ConvertFromString(isOnline ? "#0E2918" : "#331417")!;
        DashNetBadgeText.Text = isOnline ? "ONLINE" : "OFFLINE";
        DashNetBadgeText.Foreground = (System.Windows.Media.Brush)bc.ConvertFromString(isOnline ? "#22C55E" : "#EF4444")!;
        DashNetTitle.Text = isOnline ? "Internet Connected" : "Network Offline";
        DashNetDetail.Text = active != null
            ? $"{active.Name}: {active.IpAddress} ({active.ModeLabel}) • GW {active.Gateway}"
            : "No active Ethernet or Wi-Fi link detected.";
        DrawerNetStatusText.Text = isOnline ? "Network: Online" : "Network: OFFLINE";
        DrawerNetStatusText.Foreground = (System.Windows.Media.Brush)bc.ConvertFromString(isOnline ? "#22C55E" : "#EF4444")!;
    }

    private void UpdateDrawerOverallHealth()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateDrawerOverallHealth);
            return;
        }

        var bc = new System.Windows.Media.BrushConverter();
        var issues = new List<string>();
        _primaryResolveAction = "";

        bool pgStopped = _settings.MonitorPrivacyGuard && !_lastPgStatus.IsProcessRunning;
        bool pgDisconnected = _settings.MonitorPrivacyGuard && _lastPgStatus.IsProcessRunning && !_lastPgStatus.IsTunnelUp;
        bool netOffline = _lastOnlineState == false;
        var failingDrives = _physicalDrives.Where(d => !d.IsVirtualDisk && d.Prediction.Severity >= FailureSeverity.Warning30Days).ToList();
        var hotDrives = _physicalDrives.Where(d => !d.IsVirtualDisk && d.TemperatureC.HasValue && d.TemperatureC.Value >= _settings.DriveTempWarningThresholdC).ToList();

        if (pgStopped)
        {
            issues.Add("xPrivacyGuard is NOT running.");
            if (string.IsNullOrEmpty(_primaryResolveAction)) _primaryResolveAction = "launch_pg";
        }
        else if (pgDisconnected)
        {
            issues.Add("xPrivacyGuard is running, but its VPN tunnel is disconnected.");
            if (string.IsNullOrEmpty(_primaryResolveAction)) _primaryResolveAction = "launch_pg";
        }

        if (netOffline)
        {
            issues.Add("Internet / network appears OFFLINE.");
            if (string.IsNullOrEmpty(_primaryResolveAction)) _primaryResolveAction = "net_diag";
        }

        if (failingDrives.Count > 0)
        {
            issues.Add($"{failingDrives.Count} drive(s) predicted failing ({string.Join(", ", failingDrives.Select(d => d.Model))}).");
            if (string.IsNullOrEmpty(_primaryResolveAction)) _primaryResolveAction = "open_drives";
        }
        else if (hotDrives.Count > 0)
        {
            issues.Add($"Drive temperature alert: {hotDrives[0].Model} at {hotDrives[0].TemperatureC}°C.");
            if (string.IsNullOrEmpty(_primaryResolveAction)) _primaryResolveAction = "open_drives";
        }

        if (_activeMalwareThreats > 0)
        {
            issues.Add($"{_activeMalwareThreats} malware threat(s) detected in recent scan.");
            if (string.IsNullOrEmpty(_primaryResolveAction)) _primaryResolveAction = "open_quarantine";
        }

        bool isWorking = !_initialCheckDone || _isBusy || _isMalwareScanning || _isResolvingIssue;

        if (isWorking)
        {
            // ORANGE: Starting up / Scanning / Resolving
            BrandIconImage.Source = BrandOrangePng;
            BrandIconBorder.BorderBrush = (System.Windows.Media.Brush)bc.ConvertFromString("#8CF38020")!;
            Icon = WindowOrangeIco;
            if (_trayIcon != null && _trayIconOrange != null)
            {
                _trayIcon.Icon = _trayIconOrange;
                _trayIcon.Text = !_initialCheckDone
                    ? "xSecurityCenter — Starting up..."
                    : "xSecurityCenter — Working / Resolving...";
            }
            DrawerStatusDot.Fill = (System.Windows.Media.Brush)bc.ConvertFromString("#F38020")!;
            DrawerStatusText.Text = !_initialCheckDone ? "Starting..." : "Resolving...";

            IssuesBanner.Visibility = Visibility.Visible;
            IssuesBanner.Background = (System.Windows.Media.Brush)bc.ConvertFromString("#2B1B0E")!;
            IssuesBanner.BorderBrush = (System.Windows.Media.Brush)bc.ConvertFromString("#F38020")!;
            IssuesBannerTitle.Text = !_initialCheckDone ? "STARTING UP — CHECKING SECURITY POSTURE" : "ACTIVE OPERATION IN PROGRESS";
            IssuesBannerTitle.Foreground = (System.Windows.Media.Brush)bc.ConvertFromString("#FAAD3F")!;
            IssuesBannerDetail.Text = issues.Count > 0
                ? string.Join("  •  ", issues)
                : "Running security checks and background telemetry...";
            BtnResolvePrimary.Visibility = Visibility.Collapsed;
        }
        else if (issues.Count > 0)
        {
            // RED: Unresolved issue(s)
            BrandIconImage.Source = BrandRedPng;
            BrandIconBorder.BorderBrush = (System.Windows.Media.Brush)bc.ConvertFromString("#8CEF4444")!;
            Icon = WindowRedIco;
            if (_trayIcon != null && _trayIconRed != null)
            {
                _trayIcon.Icon = _trayIconRed;
                var tip = $"xSecurityCenter — Issue: {issues[0]}";
                _trayIcon.Text = tip.Length > 63 ? tip[..63] : tip;
            }
            DrawerStatusDot.Fill = (System.Windows.Media.Brush)bc.ConvertFromString("#EF4444")!;
            DrawerStatusText.Text = "Issue Detected";

            IssuesBanner.Visibility = Visibility.Visible;
            IssuesBanner.Background = (System.Windows.Media.Brush)bc.ConvertFromString("#331417")!;
            IssuesBanner.BorderBrush = (System.Windows.Media.Brush)bc.ConvertFromString("#EF4444")!;
            IssuesBannerTitle.Text = $"{issues.Count} UNRESOLVED SECURITY {(issues.Count == 1 ? "ISSUE" : "ISSUES")} — ACTION NEEDED";
            IssuesBannerTitle.Foreground = (System.Windows.Media.Brush)bc.ConvertFromString("#F87171")!;
            IssuesBannerDetail.Text = string.Join("\n", issues.Select(i => "• " + i));

            BtnResolvePrimary.Visibility = Visibility.Visible;
            BtnResolvePrimary.Content = _primaryResolveAction switch
            {
                "launch_pg" => pgStopped ? "Launch xPrivacyGuard" : "Open xPrivacyGuard",
                "net_diag" => "Run Offline Diagnostics",
                "open_drives" => "Open Drive Sentinel",
                "open_quarantine" => "Open Quarantine",
                _ => "Resolve"
            };
        }
        else
        {
            // GREEN: All Good!
            BrandIconImage.Source = BrandGreenPng;
            BrandIconBorder.BorderBrush = (System.Windows.Media.Brush)bc.ConvertFromString("#8C22C55E")!;
            Icon = WindowGreenIco;
            if (_trayIcon != null && _trayIconGreen != null)
            {
                _trayIcon.Icon = _trayIconGreen;
                _trayIcon.Text = "xSecurityCenter — All Systems Protected (Green)";
            }
            DrawerStatusDot.Fill = (System.Windows.Media.Brush)bc.ConvertFromString("#22C55E")!;
            DrawerStatusText.Text = "All Green";

            IssuesBanner.Visibility = Visibility.Collapsed;
        }
    }

    private async void BtnResolvePrimary_Click(object sender, RoutedEventArgs e)
    {
        _isResolvingIssue = true;
        UpdateDrawerOverallHealth();
        try
        {
            switch (_primaryResolveAction)
            {
                case "launch_pg":
                    var (ok, msg) = _pgMonitor.LaunchPrivacyGuard(_settings.PrivacyGuardExePath);
                    AppendLog($"[Resolve] {msg}");
                    if (!ok) DarkDialog.Show(msg, "Launch xPrivacyGuard", DarkDialogKind.Warning, this);
                    await Task.Delay(1800);
                    await RunBackgroundWatchdogTickAsync();
                    break;
                case "net_diag":
                    NavNetwork.IsChecked = true;
                    await RunNetworkDiagnosticsAsync();
                    break;
                case "open_drives":
                    NavDrives.IsChecked = true;
                    await RefreshDrivesAsync(fromBackground: false);
                    break;
                case "open_quarantine":
                    NavScanner.IsChecked = true;
                    ScannerView.SelectedIndex = 2;
                    _activeMalwareThreats = 0;
                    RefreshScannerMetaUi();
                    break;
            }
        }
        finally
        {
            _isResolvingIssue = false;
            UpdateDrawerOverallHealth();
        }
    }

    private async void BtnRecheckAll_Click(object sender, RoutedEventArgs e)
    {
        _isResolvingIssue = true;
        UpdateDrawerOverallHealth();
        try
        {
            await Task.WhenAll(RunBackgroundWatchdogTickAsync(), RefreshDrivesAsync(fromBackground: false));
        }
        finally
        {
            _isResolvingIssue = false;
            UpdateDrawerOverallHealth();
        }
    }

    private async void DashLaunchPgBtn_Click(object sender, RoutedEventArgs e)
    {
        var (ok, msg) = _pgMonitor.LaunchPrivacyGuard(_settings.PrivacyGuardExePath);
        AppendLog($"[PrivacyGuard] {msg}");
        StatusFooterText.Text = msg;
        if (!ok)
        {
            DarkDialog.Show(msg, "Launch xPrivacyGuard", DarkDialogKind.Warning, this);
            return;
        }
        await Task.Delay(1500);
        await RunBackgroundWatchdogTickAsync();
    }

    private async void DashCheckPgBtn_Click(object sender, RoutedEventArgs e) => await RunBackgroundWatchdogTickAsync();

    // ==================== OFFLINE NETWORK DIAGNOSTICS & STATIC IP ====================

    private void DashOpenNetworkBtn_Click(object sender, RoutedEventArgs e) => NavNetwork.IsChecked = true;

    private async void DashRunNetDiagBtn_Click(object sender, RoutedEventArgs e)
    {
        NavNetwork.IsChecked = true;
        await RunNetworkDiagnosticsAsync();
    }

    private async void RunNetDiagBtn_Click(object sender, RoutedEventArgs e) => await RunNetworkDiagnosticsAsync();

    private async Task RunNetworkDiagnosticsAsync()
    {
        RunNetDiagBtn.IsEnabled = false;
        StatusFooterText.Text = "Running offline network diagnostics (adapters, xPrivacyGuard, gateway, DNS, WAN)...";
        try
        {
            var pg = await Task.Run(() => _pgMonitor.CheckStatus(_settings.PrivacyGuardExePath));
            var report = await _netDiag.RunOfflineDiagnosticsAsync(pg);
            RenderNetworkDiagnosticReport(report);
            UpdatePrivacyGuardUi(pg);
            UpdateNetworkSummaryUi(report.IsOnline, report.Adapters);
            AppendLog($"[NetworkDiag] {report.Headline}");
            StatusFooterText.Text = report.Headline;
        }
        catch (Exception ex)
        {
            AppendLog($"[NetworkDiag] Error: {ex.Message}");
            StatusFooterText.Text = $"Diagnostics error: {ex.Message}";
        }
        finally
        {
            RunNetDiagBtn.IsEnabled = true;
        }
    }

    private void RenderNetworkDiagnosticReport(NetworkDiagnosticReport report)
    {
        NetDiagLastRunText.Text = $"Last run: {report.RunAt:HH:mm:ss}";
        NetDiagHeadlineText.Text = report.Headline;
        NetDiagRootCauseText.Text = report.RootCause;
        NetDiagStepsText.Text = report.RecoverySteps;
        NetDiagChecksGrid.ItemsSource = report.Checks;
        NetAdaptersGrid.ItemsSource = report.Adapters;
        UpdateStaticRollbackUi();
    }

    private void UpdateStaticRollbackUi()
    {
        bool hasRollback = _netDiag.HasActiveRollback(NetworkRollbackPath);
        var bc = new System.Windows.Media.BrushConverter();
        StaticRollbackBadge.Background = (System.Windows.Media.Brush)bc.ConvertFromString(hasRollback ? "#2B1B0E" : "#222227")!;
        StaticRollbackBadgeText.Text = hasRollback ? "STATIC TROUBLESHOOTING ACTIVE" : "Normal Mode";
        StaticRollbackBadgeText.Foreground = (System.Windows.Media.Brush)bc.ConvertFromString(hasRollback ? "#FAAD3F" : "#A1A1AA")!;
    }

    private void AutoFillSubnetBtn_Click(object sender, RoutedEventArgs e)
    {
        var adapters = _netDiag.GetAdapters(physicalOnly: true);
        var active = adapters.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Gateway))
                  ?? adapters.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.IpAddress) && !a.IpAddress.StartsWith("169.254."));
        if (active == null)
        {
            DarkDialog.Show("No active IPv4 subnet detected. Enter your router's subnet manually (e.g., 192.168.1.x).", "Auto-Fill Subnet", DarkDialogKind.Info, this);
            return;
        }

        var basis = !string.IsNullOrWhiteSpace(active.Gateway) ? active.Gateway : active.IpAddress;
        var parts = basis.Split('.');
        if (parts.Length == 4)
        {
            var prefix = $"{parts[0]}.{parts[1]}.{parts[2]}";
            StaticEthIpBox.Text = $"{prefix}.250";
            StaticWifiIpBox.Text = $"{prefix}.251";
            StaticSubnetBox.Text = string.IsNullOrWhiteSpace(active.SubnetMask) ? "255.255.255.0" : active.SubnetMask;
            StaticGatewayBox.Text = !string.IsNullOrWhiteSpace(active.Gateway) ? active.Gateway : $"{prefix}.1";
            StaticPrimaryDnsBox.Text = "1.1.1.1";
            StaticSecondaryDnsBox.Text = "8.8.8.8";
            StatusFooterText.Text = $"Auto-filled troubleshooting static IPs for {prefix}.0/24 subnet.";
        }
    }

    private async void ApplyStaticBothBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.StaticEthIp = StaticEthIpBox.Text.Trim();
        _settings.StaticWifiIp = StaticWifiIpBox.Text.Trim();
        _settings.StaticSubnetMask = StaticSubnetBox.Text.Trim();
        _settings.StaticGateway = StaticGatewayBox.Text.Trim();
        _settings.StaticPrimaryDns = StaticPrimaryDnsBox.Text.Trim();
        _settings.StaticSecondaryDns = StaticSecondaryDnsBox.Text.Trim();
        _configStore.Save(_settings);

        if (!DarkDialog.Confirm(
            $"Apply static IPv4 settings to BOTH Ethernet ({_settings.StaticEthIp}) and Wi-Fi ({_settings.StaticWifiIp})?\n\n" +
            $"Gateway: {_settings.StaticGateway} | DNS: {_settings.StaticPrimaryDns}, {_settings.StaticSecondaryDns}\n\n" +
            "Your current IP/DHCP configuration for both adapters will be saved so you can revert with one click.",
            "Apply Static IP to Both Adapters", DarkDialogKind.Question, this))
            return;

        ApplyStaticBothBtn.IsEnabled = false;
        StatusFooterText.Text = "Snapshotting current adapter settings and applying Static IP to both adapters...";
        try
        {
            var ethProfile = new StaticIpProfile
            {
                IpAddress = _settings.StaticEthIp,
                SubnetMask = _settings.StaticSubnetMask,
                Gateway = _settings.StaticGateway,
                PrimaryDns = _settings.StaticPrimaryDns,
                SecondaryDns = _settings.StaticSecondaryDns
            };
            var wifiProfile = new StaticIpProfile
            {
                IpAddress = _settings.StaticWifiIp,
                SubnetMask = _settings.StaticSubnetMask,
                Gateway = _settings.StaticGateway,
                PrimaryDns = _settings.StaticPrimaryDns,
                SecondaryDns = _settings.StaticSecondaryDns
            };

            var (ok, msg) = await _netDiag.ApplyStaticIpToBothAdaptersAsync(ethProfile, wifiProfile, NetworkRollbackPath);
            AppendLog($"[StaticIP] {msg}");
            UpdateStaticRollbackUi();
            await RunNetworkDiagnosticsAsync();
            DarkDialog.Show(msg, "Static IP Troubleshooter", ok ? DarkDialogKind.Success : DarkDialogKind.Warning, this);
        }
        finally
        {
            ApplyStaticBothBtn.IsEnabled = true;
        }
    }

    private async void RevertStaticBothBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!DarkDialog.Confirm(
            "Revert both Ethernet and Wi-Fi adapters back to their previous saved configuration (or DHCP)?",
            "Revert Both Adapters", DarkDialogKind.Question, this))
            return;

        RevertStaticBothBtn.IsEnabled = false;
        StatusFooterText.Text = "Reverting both adapters to previous configuration...";
        try
        {
            var (ok, msg) = await _netDiag.RevertBothAdaptersAsync(NetworkRollbackPath);
            AppendLog($"[StaticIP] {msg}");
            UpdateStaticRollbackUi();
            await RunNetworkDiagnosticsAsync();
            DarkDialog.Show(msg, "Adapters Reverted", ok ? DarkDialogKind.Success : DarkDialogKind.Warning, this);
        }
        finally
        {
            RevertStaticBothBtn.IsEnabled = true;
        }
    }

    private async void NetFlushDnsBtn_Click(object sender, RoutedEventArgs e)
    {
        var msg = await _netDiag.FlushDnsAsync();
        AppendLog($"[Network] Flush DNS: {msg}");
        StatusFooterText.Text = "DNS cache flushed.";
        DarkDialog.Show(msg, "Flush DNS Cache", DarkDialogKind.Success, this);
    }

    private async void NetRenewDhcpBtn_Click(object sender, RoutedEventArgs e)
    {
        StatusFooterText.Text = "Releasing and renewing DHCP lease...";
        var msg = await _netDiag.RenewDhcpAsync();
        AppendLog($"[Network] DHCP Renew: {msg}");
        await RunNetworkDiagnosticsAsync();
        DarkDialog.Show(msg, "Renew DHCP Lease", DarkDialogKind.Info, this);
    }

    private async void NetRestartAdaptersBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!DarkDialog.Confirm("Disable and re-enable physical Ethernet and Wi-Fi adapters? Active connections will drop briefly.", "Restart Network Adapters", DarkDialogKind.Question, this))
            return;
        StatusFooterText.Text = "Restarting physical network adapters...";
        var msg = await _netDiag.RestartPhysicalAdaptersAsync();
        AppendLog($"[Network] Adapter Restart: {msg}");
        await Task.Delay(2500);
        await RunNetworkDiagnosticsAsync();
    }

    private async void NetResetStackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!DarkDialog.Confirm("Reset Windows Winsock catalog and TCP/IP stack? A reboot is recommended afterward.", "Reset Winsock / TCP-IP", DarkDialogKind.Warning, this))
            return;
        var msg = await _netDiag.ResetNetworkStackAsync();
        AppendLog($"[Network] Stack Reset: {msg}");
        DarkDialog.Show(msg, "Winsock / TCP-IP Reset", DarkDialogKind.Success, this);
    }

    // ==================== DRIVE SENTINEL ====================

    private void DashOpenDrivesBtn_Click(object sender, RoutedEventArgs e) => NavDrives.IsChecked = true;

    private async void DriveScanBtn_Click(object sender, RoutedEventArgs e) => await RefreshDrivesAsync(fromBackground: false);

    private async Task RefreshDrivesAsync(bool fromBackground)
    {
        if (DriveScanBtn != null) DriveScanBtn.IsEnabled = false;
        try
        {
            var drives = await Task.Run(() => DriveTelemetryService.GetAllPhysicalDrives());
            _physicalDrives = drives;

            int total = drives.Count(d => !d.IsVirtualDisk);
            int risk = drives.Count(d => !d.IsVirtualDisk && d.Prediction.Severity >= FailureSeverity.AttentionNeeded);
            int failing = drives.Count(d => !d.IsVirtualDisk && d.Prediction.Severity >= FailureSeverity.Warning30Days);
            int healthy = Math.Max(0, total - risk);
            var maxTemp = drives.Where(d => d.TemperatureC.HasValue).Select(d => d.TemperatureC!.Value).DefaultIfEmpty(0).Max();

            Dispatcher.Invoke(() =>
            {
                DrivesGrid.ItemsSource = drives;
                DriveMetricTotal.Text = total.ToString();
                DriveMetricHealthy.Text = healthy.ToString();
                DriveMetricRisk.Text = risk.ToString();
                DriveMetricMaxTemp.Text = maxTemp > 0 ? $"{maxTemp}°C" : "—";

                var bc = new System.Windows.Media.BrushConverter();
                string badgeBg = failing > 0 ? "#331417" : risk > 0 ? "#2B1B0E" : "#0E2918";
                string badgeFg = failing > 0 ? "#EF4444" : risk > 0 ? "#FAAD3F" : "#22C55E";
                string badgeTxt = failing > 0 ? "FAILING" : risk > 0 ? "ATTENTION" : "HEALTHY";

                DashDriveBadge.Background = (System.Windows.Media.Brush)bc.ConvertFromString(badgeBg)!;
                DashDriveBadgeText.Text = badgeTxt;
                DashDriveBadgeText.Foreground = (System.Windows.Media.Brush)bc.ConvertFromString(badgeFg)!;
                DashDriveTitle.Text = failing > 0
                    ? $"{failing} Drive(s) Predicted Failing!"
                    : risk > 0 ? $"{risk} Drive(s) Need Attention" : $"{healthy}/{total} Physical Drives Healthy";
                DashDriveDetail.Text = maxTemp > 0
                    ? $"Peak Temp: {maxTemp}°C • NVMe/SATA SMART monitoring active"
                    : $"{total} physical drive(s) scanned";

                DrawerDriveStatusText.Text = failing > 0
                    ? $"Drives: {failing} FAILING!"
                    : maxTemp > 0 ? $"Drives: {healthy}/{total} OK ({maxTemp}°C)" : $"Drives: {healthy}/{total} OK";
                DrawerDriveStatusText.Foreground = (System.Windows.Media.Brush)bc.ConvertFromString(badgeFg)!;

                if (drives.Count > 0 && DrivesGrid.SelectedItem == null)
                    DrivesGrid.SelectedIndex = 0;

                UpdateDrawerOverallHealth();
                AppendLog($"[DriveSentinel] Scanned {total} physical drive(s): {healthy} healthy, {risk} attention/risk, peak temp {(maxTemp > 0 ? $"{maxTemp}°C" : "N/A")}.");
            });

            if (fromBackground && (failing > 0 || maxTemp >= _settings.DriveTempWarningThresholdC))
            {
                ShowTrayBalloon("Drive Sentinel Alert",
                    failing > 0
                        ? $"{failing} drive(s) showing failure warning indicators! Open Drive Sentinel immediately."
                        : $"Drive temperature reached {maxTemp}°C (threshold {_settings.DriveTempWarningThresholdC}°C).",
                    System.Windows.Forms.ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[DriveSentinel] Scan error: {ex.Message}");
        }
        finally
        {
            if (DriveScanBtn != null) DriveScanBtn.IsEnabled = true;
        }
    }

    private void DrivesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DrivesGrid.SelectedItem is not PhysicalDriveInfo d) return;
        SelectedDriveTitle.Text = $"Disk {d.DiskIndex}: {d.Model} ({d.MediaType}, {d.SizeLabel}) — {d.Prediction.Title}";
        SelectedDriveDetail.Text =
            $"{d.Prediction.Summary}  |  Action: {d.Prediction.RecommendedAction}\n" +
            $"Serial: {d.SerialNumber}  •  Power-On: {d.PowerOnHoursLabel}  •  TBW: {d.TotalBytesWrittenLabel}  •  Spare: {d.AvailableSpareLabel}";
        DriveSmartGrid.ItemsSource = d.SmartAttributes;
        DriveEventsGrid.ItemsSource = d.StorageEvents;
    }

    // ==================== MALWARE SCANNER, HARDENING & QUARANTINE (Merged from xScanner) ====================

    private void LogScanner(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ScannerTerminalBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            ScannerTerminalBox.ScrollToEnd();
        });
    }

    private void RefreshScannerMetaUi()
    {
        try
        {
            ScanClamStatusText.Text = _clamManager.IsInstalled()
                ? _clamManager.GetDefinitionVersion()
                : "Not installed (click Install)";
            QuarantineGrid.ItemsSource = _scanDb.GetQuarantineRecords();
            ExclusionsListBox.ItemsSource = _scanDb.GetExclusions();
            ScanHistoryGrid.ItemsSource = _scanDb.GetScanRecords();
            bool scheduled = SchedulerManager.IsTaskRegistered();
            ScanScheduleStatusText.Text = scheduled
                ? $"Active Windows Scheduled Task: '{SchedulerManager.DefaultTaskName}'"
                : "No scheduled malware scan configured.";
        }
        catch { }
    }

    private void BtnClearScanTerminal_Click(object sender, RoutedEventArgs e) => ScannerTerminalBox.Clear();

    private async void BtnBasicScan_Click(object sender, RoutedEventArgs e) => await RunMalwareScanAsync(fullScan: false);

    private async void BtnFullScan_Click(object sender, RoutedEventArgs e) => await RunMalwareScanAsync(fullScan: true);

    private async Task RunMalwareScanAsync(bool fullScan)
    {
        if (_isMalwareScanning) return;
        _isMalwareScanning = true;
        _malwareScanCts = new CancellationTokenSource();
        var token = _malwareScanCts.Token;

        BtnBasicScan.IsEnabled = false;
        BtnFullScan.IsEnabled = false;
        BtnStopScan.IsEnabled = true;
        ScanStatusText.Text = "SCANNING";
        ScanStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBright");
        ScanProgressBar.IsIndeterminate = true;
        UpdateDrawerOverallHealth();
        LogScanner($"[INFO] Starting {(fullScan ? "Full System" : "Basic")} Scan...");

        try
        {
            await Task.Run(async () =>
            {
                if (_clamManager.IsInstalled())
                {
                    LogScanner("[INFO] Updating ClamAV definitions...");
                    var upProg = new Progress<string>(m => LogScanner($"[FRESHCLAM] {m}"));
                    await _clamManager.UpdateDefinitionsAsync(upProg, token);
                }

                Action<ScanProgressEventArgs> onProgress = p =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!string.IsNullOrEmpty(p.LogMessage))
                            LogScanner(p.LogMessage);
                        ScanCurrentFileText.Text = string.IsNullOrEmpty(p.CurrentFile) ? "Scanning..." : p.CurrentFile;
                        ScanFilesCountText.Text = p.FilesScanned.ToString("N0");
                        ScanThreatsCountText.Text = p.ThreatsDetected.ToString("N0");
                        ScanProgressBar.IsIndeterminate = p.IsIndeterminate;
                        if (!p.IsIndeterminate && p.TotalFiles > 0)
                        {
                            ScanProgressBar.Maximum = p.TotalFiles;
                            ScanProgressBar.Value = Math.Min(p.FilesExamined, p.TotalFiles);
                        }
                        if (p.IsCompleted)
                        {
                            _activeMalwareThreats = p.ThreatsDetected;
                            ScanProgressBar.IsIndeterminate = false;
                            ScanProgressBar.Value = ScanProgressBar.Maximum > 0 ? ScanProgressBar.Maximum : 100;
                            ScanStatusText.Text = p.ThreatsDetected > 0 ? "THREATS" : "CLEAN";
                            ScanStatusText.Foreground = (System.Windows.Media.Brush)FindResource(p.ThreatsDetected > 0 ? "Danger" : "Success");
                            ScanCurrentFileText.Text = $"Scan complete — Examined: {p.FilesExamined:N0}, Scanned: {p.FilesScanned:N0}, Threats: {p.ThreatsDetected}.";
                            ShowTrayBalloon(
                                p.ThreatsDetected > 0 ? "Malware Threat Alert" : "Malware Scan Clean",
                                ScanCurrentFileText.Text,
                                p.ThreatsDetected > 0 ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
                        }
                    });
                };

                var exclusions = _scanDb.GetExclusions();
                if (fullScan)
                {
                    await _scanOrchestrator.RunFullScanAsync(exclusions, onProgress, token);
                }
                else
                {
                    await _scanOrchestrator.RunBasicScanAsync(exclusions, onProgress, token);
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            LogScanner("[INFO] Scan cancelled by user.");
            ScanStatusText.Text = "STOPPED";
            ScanCurrentFileText.Text = "Scan stopped by user.";
        }
        catch (Exception ex)
        {
            LogScanner($"[ERROR] Scan failed: {ex.Message}");
            ScanStatusText.Text = "ERROR";
            DarkDialog.Show($"Scan failed: {ex.Message}", "Malware Scanner", DarkDialogKind.Error, this);
        }
        finally
        {
            _isMalwareScanning = false;
            BtnBasicScan.IsEnabled = true;
            BtnFullScan.IsEnabled = true;
            BtnStopScan.IsEnabled = false;
            ScanProgressBar.IsIndeterminate = false;
            RefreshScannerMetaUi();
            UpdateDrawerOverallHealth();
        }
    }

    private void BtnStopScan_Click(object sender, RoutedEventArgs e)
    {
        if (!_isMalwareScanning) return;
        BtnStopScan.IsEnabled = false;
        _malwareScanCts?.Cancel();
        _clamManager.KillActiveProcesses();
        LogScanner("[INFO] Stopping scan and terminating ClamAV child processes...");
    }

    private async void BtnInstallClam_Click(object sender, RoutedEventArgs e)
    {
        BtnInstallClam.IsEnabled = false;
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressBar.Maximum = 100;
        try
        {
            if (!_clamManager.IsInstalled())
            {
                LogScanner("[INFO] Downloading & verifying official ClamAV engine...");
                var statusProg = new Progress<string>(m => { ScanCurrentFileText.Text = m; LogScanner($"[CLAMAV] {m}"); });
                var pctProg = new Progress<double>(v => ScanProgressBar.Value = v);
                bool ok = await _clamManager.DownloadAndInstallAsync(statusProg, pctProg);
                DarkDialog.Show(ok ? "ClamAV engine installed and ready." : "ClamAV installation failed — check terminal output.", "ClamAV Setup", ok ? DarkDialogKind.Success : DarkDialogKind.Warning, this);
            }
            else
            {
                LogScanner("[INFO] Updating ClamAV definitions...");
                var upProg = new Progress<string>(m => LogScanner($"[FRESHCLAM] {m}"));
                bool ok = await _clamManager.UpdateDefinitionsAsync(upProg);
                DarkDialog.Show(ok ? "ClamAV definitions updated." : "Definition update finished with warnings — check terminal.", "ClamAV Update", ok ? DarkDialogKind.Success : DarkDialogKind.Info, this);
            }
            RefreshScannerMetaUi();
        }
        finally
        {
            BtnInstallClam.IsEnabled = true;
        }
    }

    private async void BtnRunHardeningAudit_Click(object sender, RoutedEventArgs e)
    {
        BtnRunHardeningAudit.IsEnabled = false;
        HardeningScoreText.Text = "Running 12-point security & hardening audit...";
        try
        {
            var report = await _hardeningManager.RunSecurityAuditAsync();
            RenderHardeningReport(report);
        }
        finally
        {
            BtnRunHardeningAudit.IsEnabled = true;
        }
    }

    private async void BtnApplyHardening_Click(object sender, RoutedEventArgs e)
    {
        var selected = _hardeningChecks.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            DarkDialog.Show("Run a 12-Point Audit and select one or more checks first.", "Security Hardening", DarkDialogKind.Info, this);
            return;
        }
        if (!DarkDialog.Confirm($"Apply hardening remediation to {selected.Count} selected check(s)?", "Confirm Hardening", DarkDialogKind.Question, this))
            return;

        BtnApplyHardening.IsEnabled = false;
        try
        {
            var report = await _hardeningManager.ApplyHardeningAndVerifyAsync(null, selected);
            RenderHardeningReport(report);
            DarkDialog.Show($"Hardening applied. Security Score: {report.ScorePercentage}% ({report.PassedChecks}/{report.TotalChecks} passed).", "Hardening Complete", DarkDialogKind.Success, this);
        }
        finally
        {
            BtnApplyHardening.IsEnabled = true;
        }
    }

    private async void BtnRevertHardening_Click(object sender, RoutedEventArgs e)
    {
        var selected = _hardeningChecks.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;
        if (!DarkDialog.Confirm($"Revert hardening on {selected.Count} selected check(s)?", "Revert Hardening", DarkDialogKind.Warning, this))
            return;

        BtnRevertHardening.IsEnabled = false;
        try
        {
            var report = await _hardeningManager.RevertAllHardeningAsync(selected);
            RenderHardeningReport(report);
            DarkDialog.Show($"Reverted selected checks. Current Score: {report.ScorePercentage}%.", "Hardening Reverted", DarkDialogKind.Info, this);
        }
        finally
        {
            BtnRevertHardening.IsEnabled = true;
        }
    }

    private void RenderHardeningReport(HardeningAuditReport report)
    {
        _hardeningChecks = report.CheckResults;
        HardeningChecksGrid.ItemsSource = _hardeningChecks;
        HardeningPortsGrid.ItemsSource = report.ListeningPorts;
        HardeningScoreText.Text = $"Security Score: {report.ScorePercentage}%  •  {report.PassedChecks}/{report.TotalChecks} Hardened  •  {report.ListeningPorts.Count} Listening Ports";
        HardeningDohText.Text = $"DoH: {report.DohStatus}  •  DNS: {(report.ActiveDnsServers.Count > 0 ? string.Join(", ", report.ActiveDnsServers) : "Auto")}";
        AppendLog($"[Hardening] Audit complete: {report.ScorePercentage}% ({report.PassedChecks}/{report.TotalChecks}).");
    }

    private void BtnQuarantineRefresh_Click(object sender, RoutedEventArgs e) => RefreshScannerMetaUi();

    private void BtnQuarantineRestore_Click(object sender, RoutedEventArgs e)
    {
        if (QuarantineGrid.SelectedItem is not QuarantineRecord rec) return;
        if (!DarkDialog.Confirm($"Restore quarantined file to:\n{rec.OriginalPath}\n(and add it to exclusions)?", "Restore File", DarkDialogKind.Question, this))
            return;
        bool ok = _quarantineManager.RestoreFile(rec.Id, rec.OriginalPath, rec.QuarantinePath);
        RefreshScannerMetaUi();
        DarkDialog.Show(ok ? "File restored and added to exclusions." : "Could not restore file (source missing or access denied).", "Quarantine", ok ? DarkDialogKind.Success : DarkDialogKind.Warning, this);
    }

    private void BtnQuarantineDelete_Click(object sender, RoutedEventArgs e)
    {
        if (QuarantineGrid.SelectedItem is not QuarantineRecord rec) return;
        if (!DarkDialog.Confirm($"Permanently delete quarantined file?\n{rec.OriginalPath}", "Delete Permanently", DarkDialogKind.Warning, this))
            return;
        bool ok = _quarantineManager.DeletePermanently(rec.Id, rec.QuarantinePath);
        RefreshScannerMetaUi();
        DarkDialog.Show(ok ? "Quarantined file deleted permanently." : "Delete failed.", "Quarantine", ok ? DarkDialogKind.Success : DarkDialogKind.Warning, this);
    }

    private void BtnAddExclusion_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Select folder to exclude from malware scans" };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            _scanDb.AddExclusion(dlg.SelectedPath);
            RefreshScannerMetaUi();
        }
    }

    private void BtnRemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (ExclusionsListBox.SelectedItem is not string path) return;
        _scanDb.RemoveExclusion(path);
        RefreshScannerMetaUi();
    }

    private void BtnRegisterScanSched_Click(object sender, RoutedEventArgs e)
    {
        var time = string.IsNullOrWhiteSpace(ScanSchedTimeBox.Text) ? "01:00" : ScanSchedTimeBox.Text.Trim();
        bool ok = SchedulerManager.ConfigureWeeklySchedule(SchedulerManager.DefaultTaskName, time, wakePc: true);
        RefreshScannerMetaUi();
        DarkDialog.Show(ok ? $"Weekly malware scan scheduled for {time}." : "Failed to register scheduled scan task.", "Scheduled Scan", ok ? DarkDialogKind.Success : DarkDialogKind.Warning, this);
    }

    private void BtnRemoveScanSched_Click(object sender, RoutedEventArgs e)
    {
        bool ok = SchedulerManager.RemoveSchedule(SchedulerManager.DefaultTaskName);
        RefreshScannerMetaUi();
        DarkDialog.Show(ok ? "Scheduled malware scan removed." : "No scheduled task found or removal failed.", "Scheduled Scan", ok ? DarkDialogKind.Info : DarkDialogKind.Warning, this);
    }

    private sealed class DarkTrayMenuRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
    {
        public DarkTrayMenuRenderer() : base(new DarkTrayColorTable()) { }

        protected override void OnRenderMenuItemBackground(System.Windows.Forms.ToolStripItemRenderEventArgs e)
        {
            var rect = new System.Drawing.Rectangle(4, 2, e.Item.Width - 8, e.Item.Height - 4);
            var bg = e.Item.Selected
                ? System.Drawing.Color.FromArgb(0x2B, 0x1B, 0x0E)
                : System.Drawing.Color.FromArgb(0x1A, 0x1A, 0x1E);
            using var brush = new System.Drawing.SolidBrush(bg);
            e.Graphics.FillRectangle(brush, rect);
            e.Item.ForeColor = e.Item.Selected
                ? System.Drawing.Color.FromArgb(0xFA, 0xAD, 0x3F)
                : System.Drawing.Color.FromArgb(0xF4, 0xF4, 0xF5);
        }

        protected override void OnRenderSeparator(System.Windows.Forms.ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0x2C, 0x2C, 0x33));
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
    }

    private sealed class DarkTrayColorTable : System.Windows.Forms.ProfessionalColorTable
    {
        private static readonly System.Drawing.Color Bg = System.Drawing.Color.FromArgb(0x1A, 0x1A, 0x1E);
        private static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(0x2C, 0x2C, 0x33);
        public override System.Drawing.Color ToolStripDropDownBackground => Bg;
        public override System.Drawing.Color MenuBorder => Border;
        public override System.Drawing.Color ImageMarginGradientBegin => Bg;
        public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
        public override System.Drawing.Color ImageMarginGradientEnd => Bg;
    }
}
