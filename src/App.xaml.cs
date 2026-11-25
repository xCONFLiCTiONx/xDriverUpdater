using System.Runtime.InteropServices;
using System.Windows;
using xDriverUpdater.Services;

namespace xDriverUpdater;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    protected override async void OnStartup(StartupEventArgs e)
    {
        bool isCli = e.Args.Length > 0;
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
                "xDriverUpdater Error", DarkDialogKind.Error);
            ex.Handled = true;
        };

        if (isCli)
        {
            AttachConsole(-1);
            var exitCode = await RunCliAsync(e.Args);
            Shutdown(exitCode);
            return;
        }

        base.OnStartup(e);
        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        var cmd = args[0].Trim().ToLowerInvariant();
        if (cmd is "--help" or "-h" or "/?")
        {
            Console.WriteLine("xDriverUpdater — Smart Hardware-Aware Windows Driver Manager");
            Console.WriteLine("Usage:");
            Console.WriteLine("  xDriverUpdater.exe                Launch GUI");
            Console.WriteLine("  xDriverUpdater.exe --audit        Inspect hardware, OEM/laptop status, and driver health");
            Console.WriteLine("  xDriverUpdater.exe --scan         Scan Windows Update + Direct Manufacturer + Repositories");
            Console.WriteLine("  xDriverUpdater.exe --backup [dir] Export all third-party drivers via pnputil");
            Console.WriteLine("  xDriverUpdater.exe --list-repos   List configured driver repositories");
            Console.WriteLine("  xDriverUpdater.exe --scheduled-update  Silently check, download & install updates (alerts xSecurity on failure)");
            Console.WriteLine("  xDriverUpdater.exe --register-task [Daily|Weekly] [HH:mm]  Register Windows Scheduled Task");
            Console.WriteLine("  xDriverUpdater.exe --unregister-task                       Remove Windows Scheduled Task");
            Console.WriteLine("  xDriverUpdater.exe --test-alert                            Send a test alert to xSecurity & connected devices");
            return 0;
        }

        var configStore = new ConfigStore();
        var settings = configStore.Load();

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
