using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed class TelemetryScanner
{
    private record KnownServiceDef(string Name, string Vendor, string Description, RiskLevel Risk, int RestoreStart = 3);
    private record KnownPolicyDef(
        string Id,
        string DisplayName,
        string Vendor,
        string Description,
        RiskLevel Risk,
        RegistryHive Hive,
        string SubKey,
        string ValueName,
        object DisabledValue,
        object? RestoredValue = null,
        RegistryValueKind Kind = RegistryValueKind.DWord);
    private record KnownAppxDef(string PackageName, string DisplayName, string Vendor, string Description, RiskLevel Risk);

    private static readonly KnownServiceDef[] KnownServices =
    [
        new("DiagTrack", "Microsoft", "Connected User Experiences and Telemetry — primary Windows telemetry upload service.", RiskLevel.Safe, 2),
        new("dmwappushservice", "Microsoft", "WAP Push Message Routing Service — routes telemetry and diagnostic push messages.", RiskLevel.Safe, 3),
        new("InventorySvc", "Microsoft", "Inventory and Compatibility Appraisal — collects installed app inventory for Microsoft.", RiskLevel.Safe, 2),
        new("whesvc", "Microsoft", "Windows Health and Optimized Experiences — sends system health & experience diagnostics.", RiskLevel.Safe, 2),
        new("WerSvc", "Microsoft", "Windows Error Reporting — uploads crash dumps and error reports to Microsoft.", RiskLevel.Safe, 3),
        new("PcaSvc", "Microsoft", "Program Compatibility Assistant — monitors app execution and logs compatibility telemetry.", RiskLevel.Safe, 2),
        new("MapsBroker", "Microsoft", "Downloaded Maps Manager — background service checking for offline map updates.", RiskLevel.Safe, 2),
        new("RetailDemo", "Microsoft", "Retail Demo Service — store demo telemetry and configuration service.", RiskLevel.Safe, 3),
        new("RemoteRegistry", "Microsoft", "Remote Registry — allows remote users to modify registry settings on this PC.", RiskLevel.Safe, 3),
        new("WpcMonSvc", "Microsoft", "Parental Controls — Microsoft Family Safety enforcement and activity reporting.", RiskLevel.Safe, 3),
        new("AMD Crash Defender Service", "AMD", "AMD Crash Defender — collects and reports AMD driver/GPU crash telemetry.", RiskLevel.Safe, 2),
        new("HPPrintScanDoctorService", "HP", "HP Print Scan Doctor — background printer health telemetry and diagnostics.", RiskLevel.Safe, 2),
        new("NvTelemetryContainer", "NVIDIA", "NVIDIA Telemetry Container — uploads GPU/driver usage telemetry to NVIDIA.", RiskLevel.Safe, 2),
        new("DPS", "Microsoft", "Diagnostic Policy Service — runs local Windows problem detection diagnostics.", RiskLevel.Moderate, 2),
        new("WdiSystemHost", "Microsoft", "Diagnostic System Host — hosts system-level diagnostic scenarios.", RiskLevel.Moderate, 3),
        new("WdiServiceHost", "Microsoft", "Diagnostic Service Host — hosts service-level diagnostic scenarios.", RiskLevel.Moderate, 3),
        new("edgeupdate", "Microsoft", "Microsoft Edge Update Service — background Edge update checker.", RiskLevel.Moderate, 2),
        new("edgeupdatem", "Microsoft", "Microsoft Edge Update Service (Manual) — on-demand Edge updater.", RiskLevel.Moderate, 3),
        new("SysMain", "Microsoft", "SysMain (Superfetch) — preloads apps into RAM; optional on fast NVMe SSDs.", RiskLevel.Optional, 2),
        new("wisvc", "Microsoft", "Windows Insider Service — keep enabled if you are on Windows Insider builds.", RiskLevel.Optional, 3),
        new("EpicGamesUpdater", "Epic Games", "Epic Games Updater — background Epic launcher update service.", RiskLevel.Optional, 3),
        new("CDPSvc", "Microsoft", "Connected Devices Platform — powers Phone Link, Nearby Share, and cross-device sync.", RiskLevel.Optional, 2),
        new("WpnService", "Microsoft", "Windows Push Notifications — maintains persistent connection to Microsoft WNS for toast notifications.", RiskLevel.Optional, 2),
        new("XblAuthManager", "Microsoft", "Xbox Live Auth Manager — keep enabled if you use PC Game Pass or Xbox apps.", RiskLevel.Optional, 3),
        new("XblGameSave", "Microsoft", "Xbox Live Game Save — syncs Xbox game saves.", RiskLevel.Optional, 3),
        new("XboxNetApiSvc", "Microsoft", "Xbox Live Networking Service — Xbox Live multiplayer networking.", RiskLevel.Optional, 3)
    ];

    private static readonly Regex DynamicServiceRegex = new(
        @"telemetry|crash.*defender|crash.*report|diagtrack|googleupdater|adobearm|adobe.*update|razer.*telemetry|icue.*telemetry|armoury.*telemetry|hp.*touchpoint|hp.*analytics|intel.*telemetry|dell.*telemetry",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TaskMatchRegex = new(
        @"Telemetry|CEIP|Customer Experience|Application Experience|CompatTel|Consolidator|UsbCeip|DiskFootprint\\Diagnostics|DiskDiagnostic|Feedback|Autochk\\Proxy|CloudExperienceHost|MapsToast|MapsUpdate|NvTm|NvDriverUpdate|NVIDIA App SelfUpdate|GoogleUserPEH|HP Print Scan Doctor|AdobeGCInvoker|AdobeAAMUpdater|Office Performance Monitor|Office Feature Updates|Office Background Push|SustainabilityTelemetry|Power Efficiency Diagnostics|ShowFeedbackToast|FamilySafety|QueueReporting|DmClient|SpeechModelDownload",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StartupTelemetryRegex = new(
        @"MicrosoftEdgeAutoLaunch|OneDrive|Teams|Skype|AdobeGC|AdobeAAM|CCXProcess|HP.*Smart|HP.*Status|GoogleUpdate|Razer|NvBackend|NvTelemetry|Cortana|Bing|Copilot",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly KnownPolicyDef[] KnownPolicies =
    [
        new("reg:AllowTelemetry", "Windows Diagnostic Data (AllowTelemetry = 0)", "Microsoft",
            "Sets Windows DataCollection policy AllowTelemetry to 0 (Security/Off).", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, null),

        new("reg:DoNotShowFeedbackNotifications", "Disable Windows Feedback Prompts", "Microsoft",
            "Stops Windows from prompting for feedback and diagnostic surveys.", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", 1, null),

        new("reg:DisableOneSettingsDownloads", "Disable OneSettings Telemetry Config Downloads", "Microsoft",
            "Prevents DiagTrack from downloading dynamic telemetry configuration from Microsoft.", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DisableOneSettingsDownloads", 1, null),

        new("reg:DisableAIDataAnalysis", "Block Windows Recall / AI Data Analysis", "Microsoft",
            "Explicitly disables Windows Recall / AI snapshot data collection via Group Policy.", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, null),

        new("reg:TurnOffWindowsCopilot", "Disable Windows Copilot Policy", "Microsoft",
            "Disables Windows Copilot sidebar and background integration via policy.", RiskLevel.Safe,
            RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),

        new("reg:SystemPaneSuggestions", "Disable Start Menu App & Content Suggestions", "Microsoft",
            "Stops Windows from showing promoted apps and suggestions in the Start menu.", RiskLevel.Safe,
            RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0, 1),

        new("reg:SubscribedContent338388", "Disable Start Menu Suggested Apps (338388)", "Microsoft",
            "Blocks silent Start menu suggested app tiles and promotional banners.", RiskLevel.Safe,
            RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", 0, 1),

        new("reg:SubscribedContent338389", "Disable Windows Tips & Tricks Prompts (338389)", "Microsoft",
            "Stops Windows from showing 'Get tips and suggestions as you use Windows' notifications.", RiskLevel.Safe,
            RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0, 1),

        new("reg:TaskbarWidgets", "Hide Taskbar Widgets Button (TaskbarDa = 0)", "Microsoft",
            "Hides the Windows 11 Widgets button on the taskbar.", RiskLevel.Safe,
            RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", 0, 1),

        new("reg:PublishUserActivities", "Disable Activity History Publishing", "Microsoft",
            "Prevents Windows from recording and publishing your app/file activity history.", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, null),

        new("reg:UploadUserActivities", "Disable Activity History Cloud Upload", "Microsoft",
            "Prevents Windows from uploading activity history to Microsoft cloud.", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, null),

        new("reg:DisableWindowsConsumerFeatures", "Disable Windows Consumer Features & App Suggestions", "Microsoft",
            "Blocks silent promotional app installs and Microsoft Store suggestions.", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1, null),

        new("reg:WERDisabled", "Disable Windows Error Reporting (Policy)", "Microsoft",
            "Disables automatic crash report uploads via Windows Error Reporting.", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, null),

        new("reg:AITEnable", "Disable Application Impact Telemetry (AIT)", "Microsoft",
            "Stops Windows from collecting Application Impact Telemetry.", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "AITEnable", 0, null),

        new("reg:DisableInventory", "Disable Program Compatibility Inventory Collector", "Microsoft",
            "Stops Windows from scanning installed software and sending inventory to Microsoft.", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "DisableInventory", 1, null),

        new("reg:CEIPEnable", "Disable Customer Experience Improvement Program (CEIP)", "Microsoft",
            "Disables the Windows SQM / CEIP telemetry pipeline.", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0, null),

        new("reg:DisableSearchBoxSuggestions", "Disable Start Menu / Search Box Web Suggestions", "Microsoft",
            "Stops SearchHost and StartMenuExperienceHost from calling Bing/Microsoft when you open Start.", RiskLevel.Safe,
            RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null),

        new("reg:BingSearchEnabled", "Disable Bing Web Search in Windows Search", "Microsoft",
            "Stops Windows Search from sending keystrokes and queries to Bing.", RiskLevel.Safe,
            RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, 1),

        new("reg:AllowNewsAndInterests", "Disable Widgets / News & Interests Feed", "Microsoft",
            "Disables the Widgets/News background web feed (msedgewebview2).", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, null),

        new("reg:AdvertisingInfo", "Disable Windows Advertising ID", "Microsoft",
            "Prevents apps from using your Windows Advertising ID for cross-app tracking.", RiskLevel.Safe,
            RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1),

        new("reg:TailoredExperiences", "Disable Tailored Experiences with Diagnostic Data", "Microsoft",
            "Stops Microsoft from using diagnostic data for personalized tips and ads.", RiskLevel.Safe,
            RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1),

        new("reg:RestrictInk", "Disable Implicit Inking Telemetry", "Microsoft",
            "Stops Windows from collecting inking/handwriting samples for personalization.", RiskLevel.Safe,
            RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, 0),

        new("reg:RestrictText", "Disable Implicit Typing Telemetry", "Microsoft",
            "Stops Windows from collecting typing patterns for personalization.", RiskLevel.Safe,
            RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, 0),

        new("reg:EdgeDiagnosticData", "Disable Microsoft Edge Diagnostic Data", "Microsoft Edge",
            "Stops Edge from sending diagnostic and usage telemetry.", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "DiagnosticData", 0, null),

        new("reg:EdgeStartupBoost", "Disable Microsoft Edge Startup Boost", "Microsoft Edge",
            "Prevents Edge from silently running in the background at Windows startup.", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0, null),

        new("reg:EdgeBackgroundMode", "Disable Microsoft Edge Background Mode", "Microsoft Edge",
            "Stops Edge processes from staying alive after you close the browser.", RiskLevel.Safe,
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0, null),

        new("reg:OfficeDisableTelemetry", "Disable Microsoft Office Client Telemetry", "Microsoft Office",
            "Turns off Office diagnostic and client telemetry uploads.", RiskLevel.Safe,
            RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Office\16.0\Common\ClientTelemetry", "DisableTelemetry", 1, 0),

        new("reg:OfficeSendTelemetry", "Block Microsoft Office Telemetry Policy (SendTelemetry = 3)", "Microsoft Office",
            "Sets Office Group Policy SendTelemetry to 3 (Neither required nor optional).", RiskLevel.Safe,
            RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Office\16.0\Common\ClientTelemetry", "SendTelemetry", 3, null)
    ];

    // Curated Appx bloatware list — NEVER includes Store, Winget, WSL, Terminal, PowerToys, Codecs, or NVIDIA Control Panel.
    private static readonly KnownAppxDef[] KnownAppxBloat =
    [
        new("Microsoft.BingSearch", "Bing Search Integration", "Microsoft", "Bing web search Appx integration package.", RiskLevel.Safe),
        new("Microsoft.BingWeather", "MSN Weather", "Microsoft", "Pre-installed MSN Weather app.", RiskLevel.Safe),
        new("Microsoft.BingNews", "Microsoft News", "Microsoft", "Pre-installed MSN News app.", RiskLevel.Safe),
        new("Microsoft.GetHelp", "Windows Get Help", "Microsoft", "Microsoft support & troubleshooter app.", RiskLevel.Safe),
        new("Microsoft.Getstarted", "Microsoft Tips", "Microsoft", "Pre-installed Windows Tips promotional app.", RiskLevel.Safe),
        new("Microsoft.Edge.GameAssist", "Edge Game Assist Overlay", "Microsoft Edge", "Edge in-game sidebar overlay package.", RiskLevel.Safe),
        new("Microsoft.StartExperiencesApp", "Start Menu Promotional Experiences", "Microsoft", "Start menu feed and promotional content package.", RiskLevel.Safe),
        new("Microsoft.WidgetsPlatformRuntime", "Windows Widgets Platform Runtime", "Microsoft", "Background runtime for Windows Widgets feed.", RiskLevel.Safe),
        new("MicrosoftWindows.Client.WebExperience", "Windows Widgets & MSN Web Feed", "Microsoft", "Powers the Windows 11 Widgets & MSN news overlay.", RiskLevel.Safe),
        new("Microsoft.OfficePushNotificationUtility", "Office Push Notification Utility", "Microsoft Office", "Background Office marketing/push notification helper.", RiskLevel.Safe),
        new("Microsoft.Office.ActionsServer", "Office Actions Server", "Microsoft Office", "Background Office cloud actions helper.", RiskLevel.Safe),
        new("Microsoft.MicrosoftSolitaireCollection", "Microsoft Solitaire Collection", "Microsoft", "Pre-installed ad-supported Solitaire bundle.", RiskLevel.Safe),
        new("Clipchamp.Clipchamp", "Microsoft Clipchamp", "Microsoft", "Pre-installed cloud video editor.", RiskLevel.Safe),
        new("Microsoft.PowerAutomateDesktop", "Power Automate Desktop", "Microsoft", "Pre-installed automation agent.", RiskLevel.Safe),
        new("Microsoft.549981C3F5F10", "Legacy Cortana App", "Microsoft", "Deprecated Cortana app package.", RiskLevel.Safe),
        new("Microsoft.WindowsFeedbackHub", "Feedback Hub", "Microsoft", "Windows diagnostic feedback submission app.", RiskLevel.Safe),
        new("Microsoft.YourPhone", "Phone Link (Your Phone)", "Microsoft", "Android/iPhone sync app — keep if you use Phone Link.", RiskLevel.Optional),
        new("MicrosoftWindows.CrossDevice", "Cross-Device Experience Host", "Microsoft", "Powers Phone Link & cross-device sharing — keep if you use Phone Link.", RiskLevel.Optional),
        new("Microsoft.Windows.DevHome", "Windows Dev Home", "Microsoft", "Optional Microsoft developer dashboard app.", RiskLevel.Optional),
        new("Microsoft.ZuneMusic", "Windows Media Player (Modern)", "Microsoft", "Default Windows media player — keep if you use it.", RiskLevel.Optional),
        new("Microsoft.WindowsSoundRecorder", "Sound Recorder", "Microsoft", "Built-in voice recorder app.", RiskLevel.Optional),
        new("Microsoft.WindowsCamera", "Windows Camera", "Microsoft", "Built-in webcam app — keep if you use a webcam.", RiskLevel.Optional),
        new("Microsoft.WindowsScan", "Windows Scan", "Microsoft", "Built-in document scanner app.", RiskLevel.Optional),
        new("AD2F1837.HPPrinterControl", "HP Smart / Printer Control", "HP", "HP printer companion app — keep if you use an HP printer.", RiskLevel.Optional),
        new("AD2F1837.HPPrinterDriver", "HP Printer Driver App", "HP", "HP printer UWP helper — keep if you use an HP printer.", RiskLevel.Optional)
    ];

    public Task<List<TelemetryItem>> ScanAllAsync() => Task.Run(ScanAll);

    public List<TelemetryItem> ScanAll()
    {
        var results = new List<TelemetryItem>();
        results.AddRange(ScanServices());
        results.AddRange(ScanScheduledTasks());
        results.AddRange(ScanRegistryPolicies());
        results.AddRange(ScanStartupItems());
        results.AddRange(ScanAppxBloat());
        return results;
    }

    private List<TelemetryItem> ScanServices()
    {
        var list = new List<TelemetryItem>();
        var knownMap = KnownServices.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ServiceController[] installed;
        try { installed = ServiceController.GetServices(); }
        catch { return list; }

        foreach (var svc in installed)
        {
            using (svc)
            {
                bool isKnown = knownMap.TryGetValue(svc.ServiceName, out var def);
                bool isDynamic = !isKnown && (DynamicServiceRegex.IsMatch(svc.ServiceName) || DynamicServiceRegex.IsMatch(svc.DisplayName));
                if (!isKnown && !isDynamic) continue;
                if (!seen.Add(svc.ServiceName)) continue;

                int startVal = GetServiceStartType(svc.ServiceName);
                bool isDisabled = startVal == 4 && svc.Status == ServiceControllerStatus.Stopped;
                string startLabel = startVal switch
                {
                    2 => "Auto",
                    3 => "Manual",
                    4 => "Disabled",
                    _ => "Other"
                };

                list.Add(new TelemetryItem
                {
                    Id = $"svc:{svc.ServiceName}",
                    Name = svc.ServiceName,
                    DisplayName = svc.DisplayName,
                    Category = TelemetryCategory.Service,
                    Vendor = def?.Vendor ?? GuessVendor(svc.ServiceName + " " + svc.DisplayName),
                    Description = def?.Description ?? $"Detected background telemetry/updater service ({svc.ServiceName}).",
                    Risk = def?.Risk ?? RiskLevel.Moderate,
                    IsActive = !isDisabled,
                    StatusText = isDisabled ? "Disabled" : $"{svc.Status} ({startLabel})",
                    ServiceName = svc.ServiceName,
                    RestoreServiceStart = def?.RestoreStart ?? 3
                });
            }
        }

        return list.OrderByDescending(x => x.IsActive).ThenBy(x => x.Risk).ThenBy(x => x.Name).ToList();
    }

    private List<TelemetryItem> ScanScheduledTasks()
    {
        var list = new List<TelemetryItem>();
        object? schedulerObj = null;
        try
        {
            var t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) return list;
            schedulerObj = Activator.CreateInstance(t);
            if (schedulerObj == null) return list;

            dynamic scheduler = schedulerObj;
            scheduler.Connect();
            dynamic rootFolder = scheduler.GetFolder("\\");
            TraverseTaskFolder(rootFolder, list);
        }
        catch { }
        finally
        {
            if (schedulerObj != null && Marshal.IsComObject(schedulerObj))
                Marshal.ReleaseComObject(schedulerObj);
        }

        return list.OrderByDescending(x => x.IsActive).ThenBy(x => x.Risk).ThenBy(x => x.Name).ToList();
    }

    private void TraverseTaskFolder(dynamic folder, List<TelemetryItem> results)
    {
        try
        {
            string folderPath = folder.Path;
            dynamic tasks = folder.GetTasks(1);
            foreach (dynamic task in tasks)
            {
                try
                {
                    string name = task.Name;
                    string fullPath = folderPath.EndsWith("\\") ? folderPath + name : folderPath + "\\" + name;
                    if (TaskMatchRegex.IsMatch(fullPath))
                    {
                        bool enabled = task.Enabled;
                        int state = task.State;
                        string stateStr = state switch
                        {
                            1 => "Disabled",
                            2 => "Queued",
                            3 => "Ready",
                            4 => "Running",
                            _ => enabled ? "Enabled" : "Disabled"
                        };

                        results.Add(new TelemetryItem
                        {
                            Id = $"task:{fullPath}",
                            Name = fullPath,
                            DisplayName = name,
                            Category = TelemetryCategory.ScheduledTask,
                            Vendor = GuessVendor(fullPath),
                            Description = $"Scheduled task in {folderPath}",
                            Risk = ClassifyTaskRisk(fullPath),
                            IsActive = enabled,
                            StatusText = enabled ? $"Enabled ({stateStr})" : "Disabled",
                            TaskFolder = folderPath,
                            TaskName = name
                        });
                    }
                }
                catch { }
            }

            dynamic subFolders = folder.GetFolders(0);
            foreach (dynamic sub in subFolders)
                TraverseTaskFolder(sub, results);
        }
        catch { }
    }

    private List<TelemetryItem> ScanRegistryPolicies()
    {
        var list = new List<TelemetryItem>();
        foreach (var def in KnownPolicies)
        {
            object? current = ReadRegValue(def.Hive, def.SubKey, def.ValueName);
            bool isDisabled = current != null && string.Equals(current.ToString(), def.DisabledValue.ToString(), StringComparison.OrdinalIgnoreCase);

            list.Add(new TelemetryItem
            {
                Id = def.Id,
                Name = $@"{(def.Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\{def.SubKey}\{def.ValueName}",
                DisplayName = def.DisplayName,
                Category = TelemetryCategory.RegistryPolicy,
                Vendor = def.Vendor,
                Description = def.Description,
                Risk = def.Risk,
                IsActive = !isDisabled,
                StatusText = isDisabled ? $"Hardened ({current})" : (current == null ? "Not Set (Default Active)" : $"Active ({current})"),
                RegistryHive = def.Hive,
                RegistrySubKey = def.SubKey,
                RegistryValueName = def.ValueName,
                DisabledValue = def.DisabledValue,
                RestoredValue = def.RestoredValue,
                RegistryValueKind = def.Kind
            });
        }

        return list.OrderByDescending(x => x.IsActive).ThenBy(x => x.DisplayName).ToList();
    }

    private List<TelemetryItem> ScanStartupItems()
    {
        var list = new List<TelemetryItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var runLocations = new (RegistryHive Hive, string Path)[]
        {
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run")
        };

        foreach (var (hive, path) in runLocations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(path, false);
                if (key == null) continue;

                foreach (var valName in key.GetValueNames())
                {
                    var cmd = key.GetValue(valName)?.ToString() ?? string.Empty;
                    if (!StartupTelemetryRegex.IsMatch(valName) && !StartupTelemetryRegex.IsMatch(cmd))
                        continue;

                    string id = $"startup:{hive}:{path}:{valName}";
                    seen.Add(id);

                    list.Add(new TelemetryItem
                    {
                        Id = id,
                        Name = valName,
                        DisplayName = $"{valName} (Auto-Launch)",
                        Category = TelemetryCategory.Startup,
                        Vendor = GuessVendor(valName + " " + cmd),
                        Description = cmd,
                        Risk = valName.Contains("Edge", StringComparison.OrdinalIgnoreCase) ? RiskLevel.Safe : RiskLevel.Optional,
                        IsActive = true,
                        StatusText = "Active at Logon",
                        StartupHive = hive,
                        StartupKeyPath = path,
                        StartupValueName = valName,
                        StartupCommand = cmd
                    });
                }
            }
            catch { }
        }

        // Show backed-up (disabled) startup entries from both xSystemForge and legacy xTelemetry keys
        foreach (var backupSubKey in new[] { @"SOFTWARE\xSecurityCenter\Backup\Startup", @"SOFTWARE\xSystemForge\Backup\Startup", @"SOFTWARE\xTelemetry\Backup\Startup" })
        {
            try
            {
                using var backupRoot = Registry.CurrentUser.OpenSubKey(backupSubKey, false);
                if (backupRoot == null) continue;

                foreach (var entryName in backupRoot.GetValueNames())
                {
                    var payload = backupRoot.GetValue(entryName)?.ToString();
                    if (string.IsNullOrWhiteSpace(payload)) continue;
                    var parts = payload.Split('|', 4);
                    if (parts.Length < 4) continue;
                    if (!Enum.TryParse<RegistryHive>(parts[0], out var hive)) continue;
                    string path = parts[1];
                    string valName = parts[2];
                    string cmd = parts[3];
                    string id = $"startup:{hive}:{path}:{valName}";
                    if (!seen.Add(id)) continue;

                    list.Add(new TelemetryItem
                    {
                        Id = id,
                        Name = valName,
                        DisplayName = $"{valName} (Auto-Launch)",
                        Category = TelemetryCategory.Startup,
                        Vendor = GuessVendor(valName + " " + cmd),
                        Description = cmd,
                        Risk = RiskLevel.Safe,
                        IsActive = false,
                        StatusText = "Disabled (Backed Up)",
                        StartupHive = hive,
                        StartupKeyPath = path,
                        StartupValueName = valName,
                        StartupCommand = cmd
                    });
                }
            }
            catch { }
        }

        return list;
    }

    private List<TelemetryItem> ScanAppxBloat()
    {
        var list = new List<TelemetryItem>();
        var knownMap = KnownAppxBloat.ToDictionary(a => a.PackageName, StringComparer.OrdinalIgnoreCase);

        try
        {
            var ps = "Get-AppxPackage | Where-Object { -not $_.IsFramework -and $_.NonRemovable -eq $false } | Select-Object Name, PackageFullName | ConvertTo-Json -Compress";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
            var (exit, output) = DriverStoreAndInstallService.RunProcess(
                "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}");
            if (exit != 0 || string.IsNullOrWhiteSpace(output))
                return list;

            using var doc = JsonDocument.Parse(output.Trim());
            var elements = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };

            foreach (var el in elements)
            {
                var name = el.TryGetProperty("Name", out var nProp) ? nProp.GetString() ?? "" : "";
                var fullName = el.TryGetProperty("PackageFullName", out var fProp) ? fProp.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name) || !knownMap.TryGetValue(name, out var def))
                    continue;

                list.Add(new TelemetryItem
                {
                    Id = $"appx:{name}",
                    Name = name,
                    DisplayName = def.DisplayName,
                    Category = TelemetryCategory.AppxBloat,
                    Vendor = def.Vendor,
                    Description = def.Description,
                    Risk = def.Risk,
                    IsActive = true,
                    StatusText = "Installed (Appx)",
                    AppxPackageName = name,
                    AppxPackageFullName = fullName
                });
            }
        }
        catch { }

        return list.OrderBy(x => x.Risk).ThenBy(x => x.DisplayName).ToList();
    }

    private static int GetServiceStartType(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", false);
            var val = key?.GetValue("Start");
            if (val is int i) return i;
        }
        catch { }
        return 3;
    }

    private static object? ReadRegValue(RegistryHive hive, string subKey, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey, false);
            return key?.GetValue(valueName);
        }
        catch { return null; }
    }

    private static string GuessVendor(string text)
    {
        if (text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || text.Contains("NvDriver", StringComparison.OrdinalIgnoreCase) || text.Contains("NvTm", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (text.Contains("AMD", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (text.Contains("HP", StringComparison.OrdinalIgnoreCase)) return "HP";
        if (text.Contains("Google", StringComparison.OrdinalIgnoreCase)) return "Google";
        if (text.Contains("Adobe", StringComparison.OrdinalIgnoreCase)) return "Adobe";
        if (text.Contains("Office", StringComparison.OrdinalIgnoreCase)) return "Microsoft Office";
        if (text.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
        if (text.Contains("Epic", StringComparison.OrdinalIgnoreCase)) return "Epic Games";
        if (text.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        return "Microsoft";
    }

    private static RiskLevel ClassifyTaskRisk(string fullPath)
    {
        if (fullPath.Contains("NvDriverUpdate", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("NVIDIA App SelfUpdate", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("Office Feature Updates", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("Office Background Push", StringComparison.OrdinalIgnoreCase))
            return RiskLevel.Optional;

        if (fullPath.Contains("Power Efficiency", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("Autochk", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("FamilySafety", StringComparison.OrdinalIgnoreCase))
            return RiskLevel.Moderate;

        return RiskLevel.Safe;
    }
}
