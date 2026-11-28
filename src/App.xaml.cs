using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using xDriverUpdater.Services;

namespace xDriverUpdater;

public partial class App : System.Windows.Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const string SingleInstanceMutexName = "Local\\xSecurityCenter_SingleInstance";
    private const string ShowWindowEventName = "Local\\xSecurityCenter_ShowWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWindowEvent;

    protected override async void OnStartup(StartupEventArgs e)
    {
        bool startSilent = e.Args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        bool isCli = e.Args.Length > 0 && !startSilent;

        DispatcherUnhandledException += (_, ex) =>
        {
            if (isCli)
            {
                Console.Error.WriteLine($"Unhandled error: {ex.Exception}");
                ex.Handled = true;
                Shutdown(1);
                return;
            }
            DarkDialog.Show($"Unexpected error:\n{ex.Exception.Message}\n\n{ex.Exception.StackTrace}",
                "xSecurityCenter Error", DarkDialogKind.Error);
            ex.Handled = true;
        };

        if (isCli)
        {
            AttachConsole(-1);
            var exitCode = await RunCliAsync(e.Args);
            Shutdown(exitCode);
            return;
        }

        _instanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowWindowEventName);
                evt.Set();
            }
            catch { }
            Shutdown(0);
            return;
        }

        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);

        base.OnStartup(e);
        var win = new MainWindow(startMinimized: startSilent);
        MainWindow = win;

        _ = Task.Run(() =>
        {
            while (_showWindowEvent != null)
            {
                try
                {
                    if (_showWindowEvent.WaitOne())
                    {
                        Dispatcher.BeginInvoke(() => win.BringToForeground());
                    }
                }
                catch { break; }
            }
        });

        if (!startSilent)
        {
            win.Show();
        }
        else
        {
            win.ShowInTaskbar = false;
            win.Visibility = Visibility.Hidden;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _showWindowEvent?.Dispose(); _showWindowEvent = null; } catch { }
        try { _instanceMutex?.ReleaseMutex(); _instanceMutex?.Dispose(); } catch { }
        base.OnExit(e);
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        var cmd = args[0].Trim().ToLowerInvariant();
        if (cmd is "--help" or "-h" or "/?")
        {
            Console.WriteLine("xSecurityCenter — All-in-One Windows Security, Privacy, Network, Drive, Driver, Power & System Suite");
            Console.WriteLine("Usage:");
            Console.WriteLine("  xSecurityCenter.exe                                         Launch GUI");
            Console.WriteLine("  xSecurityCenter.exe --silent                                Launch minimized to system tray");
            Console.WriteLine("  xSecurityCenter.exe --audit                                 Inspect hardware, OEM/laptop status, and driver health");
            Console.WriteLine("  xSecurityCenter.exe --scan                                  Scan Windows Update + Direct Manufacturer + Repositories");
            Console.WriteLine("  xSecurityCenter.exe --backup [dir]                          Export all third-party drivers via pnputil");
            Console.WriteLine("  xSecurityCenter.exe --list-repos                            List configured driver repositories");
            Console.WriteLine("  xSecurityCenter.exe --scheduled-update                      Silently check, download & install updates (alerts xSecurity on failure)");
            Console.WriteLine("  xSecurityCenter.exe --register-task [Daily|Weekly] [HH:mm]  Register Windows Scheduled Task");
            Console.WriteLine("  xSecurityCenter.exe --unregister-task                       Remove Windows Scheduled Task");
            Console.WriteLine("  xSecurityCenter.exe --test-alert                            Send a test alert to xSecurity & connected devices");
            Console.WriteLine("  xSecurityCenter.exe --power-audit                           Inspect hardware and audit active Windows power plan");
            Console.WriteLine("  xSecurityCenter.exe --power-list                            List all Windows power plans");
            Console.WriteLine("  xSecurityCenter.exe --power-apply <dev|perf|quiet>          Generate, apply & verify power plan preset (alias: --apply)");
            return 0;
        }

        var configStore = new ConfigStore();
        var settings = configStore.Load();

        if (cmd is "--power-list" or "--power-audit" or "--power-apply" or "--apply")
        {
            var powerCfg = new PowerCfgService();
            var powerInspector = new PowerHardwareInspector(powerCfg);
            var engine = new SmartPlanEngine(powerCfg);
            var hw = await powerInspector.InspectAsync();

            if (cmd == "--power-list")
            {
                Console.WriteLine("Windows Power Schemes:");
                foreach (var s in hw.ExistingSchemes)
                    Console.WriteLine($"  {(s.IsActive ? "*" : " ")} {s.Guid}  {s.Name}");
                return 0;
            }

            if (cmd == "--power-audit")
            {
                Console.WriteLine($"OS:          {hw.OsCaption} (Build {hw.OsBuild})");
                Console.WriteLine($"CPU:         {hw.CpuName} ({hw.PhysicalCores}C/{hw.LogicalProcessors}T)");
                Console.WriteLine($"GPU(s):      {string.Join(", ", hw.Gpus.Select(g => g.Name))}");
                Console.WriteLine($"AMD iGPU:    {(hw.HasDisabledAmdIgpu ? "Disabled in BIOS" : hw.HasActiveAmdGpu ? "Active" : "N/A")}");
                Console.WriteLine($"Storage:     {string.Join(" | ", hw.Disks.Select(d => d.Summary))}");
                Console.WriteLine($"Active Plan: {hw.ActiveScheme?.Name} ({hw.ActiveScheme?.Guid})");
                Console.WriteLine();

                var activeVals = hw.ActiveScheme != null ? powerCfg.QueryAllAcValues(hw.ActiveScheme.Guid) : new();
                var findings = engine.AuditActivePlan(hw, activeVals);
                Console.WriteLine("Smart Power Audit Findings:");
                foreach (var f in findings)
                    Console.WriteLine($"  [{f.BadgeText,-4}] ({f.Category}) {f.Title}\n         {f.Detail}");
                return 0;
            }

            var mode = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "dev";
            var preset = mode switch
            {
                "perf" or "max" or "performance" => Models.PresetKind.MaxPerformance,
                "quiet" or "eco" or "cool" => Models.PresetKind.CoolAndQuiet,
                _ => Models.PresetKind.SmartDevAi
            };

            var cfg = engine.CreatePreset(preset, hw);
            var backupDir = System.IO.Path.Combine(settings.BackupDirectory, "PowerPlans");
            var result = await engine.GenerateAndApplyAsync(cfg, hw, settings.PowerPlanManagedSchemeGuid, backupDir);
            foreach (var line in result.Logs)
                Console.WriteLine(line);

            if (result.Success)
            {
                settings.PowerPlanManagedSchemeGuid = result.AppliedSchemeGuid;
                settings.PowerPlanLastConfig = cfg;
                settings.PowerPlanLastAppliedUtc = DateTime.UtcNow;
                configStore.Save(settings);
            }
            return result.Success ? 0 : 1;
        }

        if (cmd is "/scheduled" or "--scheduled-scan")
        {
            var db = new xScanner.Database.ScanDatabase();
            var clam = new xScanner.Engines.ClamAV.ClamAvManager(db);
            var orch = new xScanner.Core.ScanEngine.ScanOrchestrator(db, clam);
            if (clam.IsInstalled())
            {
                await clam.UpdateDefinitionsAsync(new Progress<string>(Console.WriteLine));
            }
            await orch.RunBasicScanAsync(db.GetExclusions(), p =>
            {
                if (!string.IsNullOrEmpty(p.LogMessage)) Console.WriteLine(p.LogMessage);
            });
            return 0;
        }

        if (cmd == "--scheduled-update")
        {
            var silentSvc = new SilentDriverUpdateService();
            var runResult = await silentSvc.RunSilentUpdateAsync(settings, Console.WriteLine);
            Console.WriteLine(runResult.Summary);
            Console.WriteLine($"Log: {runResult.LogFilePath}");
            return runResult.Success ? 0 : 1;
        }

        if (cmd == "--register-task")
        {
            if (args.Length > 1) settings.ScheduledTaskFrequency = args[1];
            if (args.Length > 2) settings.ScheduledTaskTime = args[2];
            var taskSvc = new ScheduledTaskService();
            var (ok, msg) = await taskSvc.RegisterOrUpdateTaskAsync(settings, Console.WriteLine);
            if (ok)
            {
                settings.ScheduledTaskEnabled = true;
                configStore.Save(settings);
            }
            Console.WriteLine(msg);
            return ok ? 0 : 1;
        }

        if (cmd == "--unregister-task")
        {
            var taskSvc = new ScheduledTaskService();
            var (ok, msg) = await taskSvc.RemoveTaskAsync(Console.WriteLine);
            if (ok)
            {
                settings.ScheduledTaskEnabled = false;
                configStore.Save(settings);
            }
            Console.WriteLine(msg);
            return ok ? 0 : 1;
        }

        if (cmd == "--test-alert")
        {
            var alertSvc = new XSecurityAlertService();
            var logPath = System.IO.Path.Combine(settings.LogDirectory, "scheduled-update-latest.log");
            var (ok, msg) = await alertSvc.SendTestAlertAsync(settings, logPath, Console.WriteLine);
            Console.WriteLine(msg);
            return ok ? 0 : 1;
        }

        var inspector = new HardwareDriverInspector();
        var storeSvc = new DriverStoreAndInstallService();

        if (cmd == "--list-repos")
        {
            Console.WriteLine("Configured Driver Repositories:");
            foreach (var r in settings.Repositories)
                Console.WriteLine($"  [{(r.Enabled ? "ON " : "OFF")}] {r.Name} -> {r.UrlOrPath}");
            return 0;
        }

        if (cmd == "--backup")
        {
            var dir = args.Length > 1 ? args[1] : settings.BackupDirectory;
            var (ok, outDir, msg) = await storeSvc.ExportAllDriversBackupAsync(dir, Console.WriteLine);
            Console.WriteLine(msg);
            return ok ? 0 : 1;
        }

        var (profile, devices) = await inspector.InspectSystemAsync(settings.PreferOemDriversOnLaptops);
        var storePkgs = await storeSvc.EnumerateDriverStoreAsync(devices);

        if (cmd == "--audit")
        {
            Console.WriteLine($"OS:          {profile.OsCaption} (Build {profile.OsBuild}, {profile.OsArch})");
            Console.WriteLine($"Platform:    {profile.PlatformSummary}");
            Console.WriteLine($"OEM:         {profile.NormalizedOem} | BIOS: {profile.BiosVersion} ({profile.BiosReleaseDate})");
            Console.WriteLine($"CPU:         {profile.CpuName}");
            Console.WriteLine($"Devices:     {devices.Count} inspected | Driver Store: {storePkgs.Count} OEM packages");
            Console.WriteLine();

            var findings = inspector.RunSmartAudit(profile, devices, storePkgs, new());
            Console.WriteLine("Smart Audit Findings:");
            foreach (var f in findings)
                Console.WriteLine($"  [{f.BadgeText,-4}] ({f.Category}) {f.Title}\n         {f.Detail}");
            return 0;
        }

        if (cmd == "--scan")
        {
            var wua = new WindowsUpdateDriverService();
            var direct = new DirectManufacturerService();
            var repos = new RepositoryManagerService();
            var allUpdates = new List<Models.DriverUpdateCandidate>();

            if (settings.IncludeWindowsUpdateChannel)
            {
                var (wuaList, _) = await wua.ScanDriverUpdatesAsync(devices, Console.WriteLine);
                allUpdates.AddRange(wuaList);
            }
            if (settings.IncludeDirectManufacturerChecks)
            {
                var dList = await direct.CheckDirectManufacturerUpdatesAsync(profile, devices, settings.PreferOemDriversOnLaptops, Console.WriteLine);
                allUpdates.AddRange(dList);
            }
            if (settings.IncludeCustomRepositories)
            {
                var rList = await repos.ScanRepositoriesAsync(profile, devices, settings.Repositories, settings.PreferOemDriversOnLaptops, Console.WriteLine);
                allUpdates.AddRange(rList);
            }

            Console.WriteLine();
            Console.WriteLine($"Available Updates ({allUpdates.Count}):");
            foreach (var u in allUpdates.OrderByDescending(x => x.MatchScore))
                Console.WriteLine($"  [{u.SourceLabel}] {u.DeviceName}: {u.InstalledVersion} -> {u.Version} ({u.Title})");

            return 0;
        }

        Console.WriteLine($"Unknown argument '{args[0]}'. Run with --help for usage.");
        return 1;
    }
}
