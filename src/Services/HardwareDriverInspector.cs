using System.Management;
using System.Text.RegularExpressions;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed class HardwareDriverInspector
{
    private static readonly Regex PciRegex = new(
        @"^(PCI|HDAUDIO)\\VEN_([0-9A-Fa-f]{4})&DEV_([0-9A-Fa-f]{4})(?:&SUBSYS_([0-9A-Fa-f]{8}))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UsbRegex = new(
        @"^(USB|HID)\\VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MonitorRegex = new(
        @"^MONITOR\\([A-Za-z]{3})([0-9A-Fa-f]{4})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<int> LaptopChassisCodes = new()
    {
        8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32
    };

    private static readonly HashSet<int> TabletChassisCodes = new()
    {
        30, 31, 32
    };

    private static readonly HashSet<int> DesktopChassisCodes = new()
    {
        3, 4, 5, 6, 7, 13, 15, 16, 35, 36
    };

    public Task<(SystemProfile Profile, List<DriverDevice> Devices)> InspectSystemAsync(
        bool preferOemOnLaptops = true,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var profile = InspectSystemProfile();
            ct.ThrowIfCancellationRequested();
            var devices = InspectDriverDevices(profile, preferOemOnLaptops);
            return (profile, devices);
        }, ct);
    }

    private static SystemProfile InspectSystemProfile()
    {
        var p = new SystemProfile();

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in s.Get())
            {
                p.OsCaption = mo["Caption"]?.ToString()?.Trim() ?? "Windows";
                p.OsVersion = mo["Version"]?.ToString()?.Trim() ?? "";
                p.OsBuild = mo["BuildNumber"]?.ToString()?.Trim() ?? "";
                p.OsArch = mo["OSArchitecture"]?.ToString()?.Trim() ?? "x64";
                break;
            }
        }
        catch { }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Model, SystemSKUNumber, PCSystemType FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in s.Get())
            {
                p.SystemManufacturer = mo["Manufacturer"]?.ToString()?.Trim() ?? "Unknown";
                p.SystemModel = mo["Model"]?.ToString()?.Trim() ?? "Unknown";
                p.SystemSku = mo["SystemSKUNumber"]?.ToString()?.Trim() ?? "";
                p.PcSystemType = Convert.ToInt32(mo["PCSystemType"] ?? 0);
                break;
            }
        }
        catch { }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT ChassisTypes, SerialNumber FROM Win32_SystemEnclosure");
            foreach (ManagementObject mo in s.Get())
            {
                if (mo["ChassisTypes"] is ushort[] arr)
                    p.ChassisTypes = arr.Select(x => (int)x).ToArray();
                p.SerialNumber = mo["SerialNumber"]?.ToString()?.Trim() ?? "";
                break;
            }
        }
        catch { }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject mo in s.Get())
            {
                p.BoardManufacturer = mo["Manufacturer"]?.ToString()?.Trim() ?? "Unknown";
                p.BoardProduct = mo["Product"]?.ToString()?.Trim() ?? "Unknown";
                break;
            }
        }
        catch { }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            foreach (ManagementObject mo in s.Get())
            {
                p.BiosManufacturer = mo["Manufacturer"]?.ToString()?.Trim() ?? "";
                p.BiosVersion = mo["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "";
                var rawDate = mo["ReleaseDate"]?.ToString();
                if (!string.IsNullOrWhiteSpace(rawDate) && rawDate.Length >= 8)
                    p.BiosReleaseDate = $"{rawDate[..4]}-{rawDate[4..6]}-{rawDate[6..8]}";
                break;
            }
        }
        catch { }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject mo in s.Get())
            {
                p.CpuName = mo["Name"]?.ToString()?.Trim() ?? "Unknown";
                break;
            }
        }
        catch { }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery");
            p.HasBattery = s.Get().Count > 0;
        }
        catch { }

        // Determine Form Factor intelligently
        bool chassisTablet = p.ChassisTypes.Any(TabletChassisCodes.Contains);
        bool chassisLaptop = p.ChassisTypes.Any(LaptopChassisCodes.Contains);
        bool chassisDesktop = p.ChassisTypes.Any(DesktopChassisCodes.Contains);

        if (chassisTablet)
            p.FormFactor = ChassisFormFactor.TabletOrConvertible;
        else if (chassisLaptop || p.PcSystemType == 2 || p.HasBattery)
            p.FormFactor = ChassisFormFactor.Laptop;
        else if (p.PcSystemType is 4 or 5 or 7)
            p.FormFactor = ChassisFormFactor.ServerOrWorkstation;
        else if (chassisDesktop || p.PcSystemType is 1 or 3)
            p.FormFactor = ChassisFormFactor.Desktop;
        else
            p.FormFactor = ChassisFormFactor.Desktop;

        var oemRaw = p.IsLaptop ? p.SystemManufacturer : p.BoardManufacturer;
        if (string.IsNullOrWhiteSpace(oemRaw) || oemRaw.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase))
            oemRaw = p.SystemManufacturer;

        p.NormalizedOem = VendorDatabase.NormalizeVendorName(oemRaw);
        var modelForSearch = p.IsLaptop ? p.SystemModel : p.BoardProduct;
        p.OemSupportUrl = VendorDatabase.GetOemSupportUrl(p.NormalizedOem, modelForSearch);

        return p;
    }

    private static List<DriverDevice> InspectDriverDevices(SystemProfile profile, bool preferOemOnLaptops)
    {
        // Step 1: Collect Win32_PnPEntity for full HardwareID[], CompatibleID[], ConfigManagerErrorCode, and problem devices
        var pnpMap = new Dictionary<string, (string Name, string Class, string Manufacturer, int ErrorCode, string Status, List<string> HwIds, List<string> CompatIds)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, PNPClass, Manufacturer, ConfigManagerErrorCode, Status, HardwareID, CompatibleID FROM Win32_PnPEntity");
            foreach (ManagementObject mo in s.Get())
            {
                var devId = mo["DeviceID"]?.ToString();
                if (string.IsNullOrWhiteSpace(devId)) continue;

                var name = mo["Name"]?.ToString()?.Trim() ?? "";
                var pnpClass = mo["PNPClass"]?.ToString()?.Trim() ?? "";
                var mfr = mo["Manufacturer"]?.ToString()?.Trim() ?? "";
                var err = Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0);
                var status = mo["Status"]?.ToString()?.Trim() ?? "OK";
                var hwIds = (mo["HardwareID"] as string[])?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
                var compatIds = (mo["CompatibleID"] as string[])?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();

                pnpMap[devId] = (name, pnpClass, mfr, err, status, hwIds, compatIds);
            }
        }
        catch { }

        // Step 2: Collect Win32_PnPSignedDriver
        var devices = new List<DriverDevice>();
        var seenDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT DeviceID, DeviceName, FriendlyName, DeviceClass, HardWareID, Manufacturer, DriverProviderName, DriverVersion, DriverDate, InfName, IsSigned, Signer FROM Win32_PnPSignedDriver");
            foreach (ManagementObject mo in s.Get())
            {
                var devId = mo["DeviceID"]?.ToString() ?? "";
                var devName = mo["DeviceName"]?.ToString()?.Trim() ?? "";
                var friendly = mo["FriendlyName"]?.ToString()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(devName) && string.IsNullOrWhiteSpace(friendly))
                    continue;

                if (!string.IsNullOrWhiteSpace(devId))
                    seenDeviceIds.Add(devId);

                var devClass = mo["DeviceClass"]?.ToString()?.Trim() ?? "";
                var hwid = mo["HardWareID"]?.ToString()?.Trim() ?? "";
                var mfr = mo["Manufacturer"]?.ToString()?.Trim() ?? "";
                var provider = mo["DriverProviderName"]?.ToString()?.Trim() ?? "";
                var version = mo["DriverVersion"]?.ToString()?.Trim() ?? "";
                var infName = mo["InfName"]?.ToString()?.Trim() ?? "";
                var isSigned = mo["IsSigned"] is bool b ? b : true;
                var signer = mo["Signer"]?.ToString()?.Trim() ?? "";
                var date = ParseWmiDate(mo["DriverDate"]?.ToString());

                var d = new DriverDevice
                {
                    DeviceId = devId,
                    PnpDeviceId = devId,
                    DeviceName = devName,
                    FriendlyName = friendly,
                    DeviceClass = devClass,
                    HardwareId = hwid,
                    Manufacturer = mfr,
                    DriverProvider = provider,
                    DriverVersion = version,
                    DriverDate = date,
                    InfName = infName,
                    IsSigned = isSigned,
                    Signer = signer
                };

                if (!string.IsNullOrWhiteSpace(devId) && pnpMap.TryGetValue(devId, out var pnp))
                {
                    if (string.IsNullOrWhiteSpace(d.FriendlyName) && !string.IsNullOrWhiteSpace(pnp.Name))
                        d.FriendlyName = pnp.Name;
                    if (string.IsNullOrWhiteSpace(d.DeviceClass))
                        d.DeviceClass = pnp.Class;
                    d.ConfigManagerErrorCode = pnp.ErrorCode;
                    d.Status = pnp.Status;
                    d.HardwareIds = pnp.HwIds;
                    d.CompatibleIds = pnp.CompatIds;
                }

                if (d.HardwareIds.Count == 0 && !string.IsNullOrWhiteSpace(hwid))
                    d.HardwareIds.Add(hwid);

                // Skip noisy virtual/software-only noise unless it has an error
                if (!d.HasProblem && IsLowValueVirtualDevice(d))
                    continue;

                EnrichDeviceIntelligence(d, profile, preferOemOnLaptops);
                devices.Add(d);
            }
        }
        catch { }

        // Step 3: Add any PnP problem devices that didn't have a signed driver entry at all (e.g. Yellow Bang / Code 28 Missing Driver!)
        foreach (var kv in pnpMap)
        {
            if (seenDeviceIds.Contains(kv.Key)) continue;
            if (kv.Value.ErrorCode == 0) continue;

            var d = new DriverDevice
            {
                DeviceId = kv.Key,
                PnpDeviceId = kv.Key,
                DeviceName = string.IsNullOrWhiteSpace(kv.Value.Name) ? kv.Key : kv.Value.Name,
                FriendlyName = kv.Value.Name,
                DeviceClass = string.IsNullOrWhiteSpace(kv.Value.Class) ? "UNKNOWN" : kv.Value.Class,
                HardwareId = kv.Value.HwIds.FirstOrDefault() ?? kv.Key,
                HardwareIds = kv.Value.HwIds,
                CompatibleIds = kv.Value.CompatIds,
                Manufacturer = kv.Value.Manufacturer,
                DriverProvider = "None Installed",
                DriverVersion = "Missing",
                ConfigManagerErrorCode = kv.Value.ErrorCode,
                Status = kv.Value.Status,
                IsSigned = false
            };

            EnrichDeviceIntelligence(d, profile, preferOemOnLaptops);
            devices.Add(d);
        }

        // Deduplicate identical display+deviceId and sort cleanly: Problem -> Display -> Network -> Audio -> Chipset -> Storage -> USB -> Other
        return devices
            .OrderByDescending(d => d.HasProblem)
            .ThenBy(d => CategorySortOrder(d.Category))
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsLowValueVirtualDevice(DriverDevice d)
    {
        var cls = d.DeviceClass.ToUpperInvariant();
        if (cls is "PRINTQUEUE" or "VOLUMESNAPSHOT" or "SOFTWAREDEVICE" or "EHSTORSILO" or "WPD" or "AUDIOENDPOINT")
            return true;

        if (d.DeviceId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase) &&
            !cls.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static void EnrichDeviceIntelligence(DriverDevice d, SystemProfile profile, bool preferOemOnLaptops)
    {
        d.Category = MapCategory(d.DeviceClass, d.DisplayName);

        // Parse PCI / HDAUDIO / USB Vendor, Product, and Subsystem Vendor IDs
        var allIds = d.HardwareIds.Concat(new[] { d.HardwareId, d.DeviceId }).Where(x => !string.IsNullOrWhiteSpace(x));
        foreach (var id in allIds)
        {
            var mPci = PciRegex.Match(id);
            if (mPci.Success)
            {
                d.BusType = mPci.Groups[1].Value.ToUpperInvariant();
                d.VendorId = mPci.Groups[2].Value.ToUpperInvariant();
                d.ProductId = mPci.Groups[3].Value.ToUpperInvariant();
                if (mPci.Groups[4].Success && mPci.Groups[4].Value.Length == 8)
                {
                    d.SubsystemFull = mPci.Groups[4].Value.ToUpperInvariant();
                    // In PCI SUBSYS_DDDVVVV (8 hex chars), the LAST 4 hex chars are the Subsystem Vendor ID (SVID)!
                    // Example: SUBSYS_142A1025 -> SVID = 1025 (Acer)
                    d.SubsystemVendorId = d.SubsystemFull.Substring(4, 4);
                    break;
                }
            }

            var mUsb = UsbRegex.Match(id);
            if (mUsb.Success && string.IsNullOrEmpty(d.VendorId))
            {
                d.BusType = mUsb.Groups[1].Value.ToUpperInvariant();
                d.VendorId = mUsb.Groups[2].Value.ToUpperInvariant();
                d.ProductId = mUsb.Groups[3].Value.ToUpperInvariant();
            }

            var mMon = MonitorRegex.Match(id);
            if (mMon.Success && string.IsNullOrEmpty(d.VendorId))
            {
                d.BusType = "MONITOR";
                d.VendorId = mMon.Groups[1].Value.ToUpperInvariant();
                d.ProductId = mMon.Groups[2].Value.ToUpperInvariant();
            }
        }

        if (string.IsNullOrEmpty(d.BusType) && d.DeviceId.Contains('\\'))
            d.BusType = d.DeviceId.Split('\\')[0].ToUpperInvariant();

        d.SiliconVendor = VendorDatabase.LookupSiliconVendor(d.BusType, d.VendorId,
            !string.IsNullOrWhiteSpace(d.DriverProvider) ? d.DriverProvider : d.Manufacturer);

        d.SubsystemOemName = VendorDatabase.LookupSubsystemOem(d.SubsystemVendorId);

        // Detect inbox Microsoft driver
        d.IsInboxMicrosoftDriver =
            d.DriverProvider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) &&
            !d.SiliconVendor.Equals("Microsoft", StringComparison.OrdinalIgnoreCase);

        // CRITICAL OEM / LAPTOP INTELLIGENCE:
        // If this is a laptop, check whether a GPU / Audio / Network / Bluetooth / Chipset device is OEM-customized
        // (e.g. Acer-customized Intel/NVIDIA/AMD GPU via SUBSYS SVID 1025 or OEM provider string).
        bool subsysIsDifferentOem = !string.IsNullOrEmpty(d.SubsystemOemName) &&
            !d.SubsystemOemName.Equals(d.SiliconVendor, StringComparison.OrdinalIgnoreCase);

        bool laptopCoreComponent = profile.IsLaptop &&
            d.Category is "Display" or "Audio" or "Network" or "Bluetooth" or "Chipset & System";

        d.IsLaptopOemCustomized = profile.IsLaptop &&
            (subsysIsDifferentOem || (laptopCoreComponent && !string.IsNullOrWhiteSpace(profile.NormalizedOem) && profile.NormalizedOem != "Generic"));

        var effectiveOem = !string.IsNullOrEmpty(d.SubsystemOemName) ? d.SubsystemOemName : profile.NormalizedOem;
        d.OemVendorUrl = VendorDatabase.GetOemSupportUrl(effectiveOem, profile.IsLaptop ? profile.SystemModel : profile.BoardProduct);
        d.DirectVendorUrl = VendorDatabase.GetDirectManufacturerUrl(d.SiliconVendor, d.Category, d.DisplayName, profile.IsLaptop);
        d.MicrosoftCatalogUrl = VendorDatabase.GetMicrosoftCatalogUrl(d);

        // Smart Source Recommendation Engine
        if (d.IsLaptopOemCustomized && preferOemOnLaptops && d.Category is "Display" or "Audio" or "Network" or "Bluetooth")
        {
            d.RecommendedSource = RecommendedSource.OemVendorFirst;
            var svidNote = !string.IsNullOrEmpty(d.SubsystemVendorId)
                ? $" (PCI SVID {d.SubsystemVendorId} = {effectiveOem})"
                : "";
            d.SourceRationale = $"Laptop OEM-customized {d.SiliconVendor} {d.Category} detected{svidNote}. " +
                $"Prefer {effectiveOem} OEM or Windows Update DCH drivers first to preserve laptop power, thermal, MUX/hybrid graphics, and hotkey tuning. " +
                $"Generic {d.SiliconVendor} reference drivers are still available if you explicitly choose Direct Manufacturer.";
        }
        else if (d.Category == "Display" && d.SiliconVendor is "NVIDIA" or "AMD" or "Intel")
        {
            d.RecommendedSource = RecommendedSource.DirectManufacturer;
            d.SourceRationale = $"Desktop {d.SiliconVendor} GPU detected. Direct {d.SiliconVendor} drivers typically ship newer performance optimizations, game/compute fixes, and security updates ahead of Windows Update.";
        }
        else if (d.Category == "Audio" && d.SiliconVendor is "Focusrite" or "Creative" or "Realtek")
        {
            d.RecommendedSource = RecommendedSource.DirectManufacturer;
            d.SourceRationale = $"{d.SiliconVendor} audio hardware benefits from official manufacturer drivers (ASIO/control panel & low-latency DSP) rather than generic Windows inbox audio.";
        }
        else if (d.Category == "Chipset & System" && d.SiliconVendor is "AMD" or "Intel")
        {
            d.RecommendedSource = RecommendedSource.DirectManufacturer;
            d.SourceRationale = $"{d.SiliconVendor} platform chipset driver — direct {d.SiliconVendor} chipset packages include the latest power/CPPC/SMBus/PCIe routing updates.";
        }
        else if (d.BusType == "MONITOR")
        {
            d.RecommendedSource = RecommendedSource.WindowsUpdate;
            if (VendorDatabase.IsTvOrBuiltinPanelMonitor(d.VendorId, d.DisplayName))
            {
                d.SourceRationale = $"EDID-managed TV or built-in panel ({d.SiliconVendor}, MONITOR\\{d.VendorId}{d.ProductId}). " +
                    "TVs and laptop panels rely on EDID + Windows inbox monitor.inf (2006-06-21 is Microsoft's standard inbox date stamp, not an outdated driver).";
            }
            else
            {
                d.SourceRationale = $"{d.SiliconVendor} monitor (MONITOR\\{d.VendorId}{d.ProductId}). " +
                    "xDriverUpdater checks Windows Update & Microsoft Update Catalog for official WHQL monitor INF + ICC color profile drivers.";
            }
        }
        else
        {
            d.RecommendedSource = RecommendedSource.WindowsUpdate;
            d.SourceRationale = "Windows Update (WHQL Hardware-ID matched) is the safest and most reliable source for this device class.";
        }

        // Baseline Health Status (updated again after update scan)
        // Note: 2006-06-21 is Microsoft's intentional inbox driver timestamp so vendor drivers rank higher;
        // do NOT falsely flag 2006-06-21 inbox drivers as "aged" unless they are actually suspicious inbox drivers.
        bool isMicrosoftInboxStamp = d.DriverDate.HasValue && d.DriverDate.Value.Year == 2006 && d.DriverDate.Value.Month == 6 && d.DriverDate.Value.Day == 21;

        if (d.HasProblem)
        {
            d.HealthStatus = DriverHealthStatus.Problem;
        }
        else if (IsSuspiciousInboxDriver(d))
        {
            d.HealthStatus = DriverHealthStatus.InboxGeneric;
        }
        else if (d.IsLaptopOemCustomized && d.Category == "Display")
        {
            d.HealthStatus = DriverHealthStatus.OemAdvisory;
        }
        else if (!isMicrosoftInboxStamp && d.DriverDate.HasValue && d.DriverDate.Value < DateTime.Now.AddYears(-2) &&
                 d.Category is "Display" or "Network" or "Bluetooth" or "Audio" or "Storage & NVMe")
        {
            d.HealthStatus = DriverHealthStatus.Outdated;
        }
        else
        {
            d.HealthStatus = DriverHealthStatus.UpToDate;
        }
    }

    private static bool IsSuspiciousInboxDriver(DriverDevice d)
    {
        if (d.DisplayName.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!d.IsInboxMicrosoftDriver) return false;

        if (d.Category == "Display") return true;
        if (d.Category == "Audio" && d.SiliconVendor is "Realtek" or "Focusrite" or "Creative" or "Conexant / Synaptics")
            return true;
        if (d.Category == "Network" && d.SiliconVendor is "Realtek" or "Intel" or "Qualcomm" or "Qualcomm Atheros" or "MediaTek" or "Broadcom")
            return true;

        return false;
    }

    public List<AuditFinding> RunSmartAudit(
        SystemProfile profile,
        List<DriverDevice> devices,
        List<DriverStorePackage> storePackages,
        List<DriverUpdateCandidate> availableUpdates)
    {
        var findings = new List<AuditFinding>();

        // 1. Problem / Missing Driver Devices
        var problems = devices.Where(d => d.HasProblem).ToList();
        if (problems.Count > 0)
        {
            findings.Add(new AuditFinding
            {
                Severity = AuditSeverity.Critical,
                Category = "Hardware Health",
                Title = $"{problems.Count} device(s) have driver errors or missing drivers",
                Detail = string.Join("; ", problems.Take(4).Select(p => $"{p.DisplayName} (Code {p.ConfigManagerErrorCode})")) +
                         (problems.Count > 4 ? $" +{problems.Count - 4} more" : "")
            });
        }
        else
        {
            findings.Add(new AuditFinding
            {
                Severity = AuditSeverity.Ok,
                Category = "Hardware Health",
                Title = "Zero Device Manager error codes detected",
                Detail = $"All {devices.Count} inspected PnP hardware devices are functioning with no driver errors."
            });
        }

        var disabled = devices.Where(d => d.IsDisabledByUser).ToList();
        if (disabled.Count > 0)
        {
            findings.Add(new AuditFinding
            {
                Severity = AuditSeverity.Info,
                Category = "Hardware Health",
                Title = $"{disabled.Count} device(s) manually disabled in Device Manager (Code 22)",
                Detail = string.Join("; ", disabled.Take(4).Select(d => d.DisplayName)) +
                         (disabled.Count > 4 ? $" +{disabled.Count - 4} more" : "")
            });
        }

        // 2. Laptop OEM Customization Intelligence
        if (profile.IsLaptop)
        {
            var oemGpus = devices.Where(d => d.Category == "Display" && d.IsLaptopOemCustomized).ToList();
            if (oemGpus.Count > 0)
            {
                var gpuNames = string.Join(", ", oemGpus.Select(g =>
                    $"{g.DisplayName} [{g.SiliconVendor} silicon / {(string.IsNullOrEmpty(g.SubsystemOemName) ? profile.NormalizedOem : g.SubsystemOemName)} OEM]"));
                findings.Add(new AuditFinding
                {
                    Severity = AuditSeverity.Info,
                    Category = "Laptop OEM Guard",
                    Title = $"OEM-customized laptop GPU detected ({profile.NormalizedOem} {profile.SystemModel})",
                    Detail = $"{gpuNames}. xDriverUpdater prioritizes {profile.NormalizedOem} OEM / Windows Update DCH drivers first to avoid breaking hybrid graphics, brightness, or thermal profiles — while still letting you override to direct silicon drivers."
                });
            }
        }
        else
        {
            var gpus = devices.Where(d => d.Category == "Display").ToList();
            if (gpus.Count > 0)
            {
                findings.Add(new AuditFinding
                {
                    Severity = AuditSeverity.Info,
                    Category = "Platform Profile",
                    Title = $"Desktop platform detected ({profile.BoardManufacturer} {profile.BoardProduct})",
                    Detail = $"Discrete/Desktop display adapter(s): {string.Join(", ", gpus.Select(g => $"{g.DisplayName} v{g.DriverVersion}"))}. Direct manufacturer drivers (NVIDIA/AMD/Intel) are safe and recommended for peak performance."
                });
            }
        }

        // 3. Available Updates summary
        if (availableUpdates.Count > 0)
        {
            var wuaCount = availableUpdates.Count(u => u.SourceKind == UpdateSourceKind.WindowsUpdate);
            var directCount = availableUpdates.Count(u => u.SourceKind == UpdateSourceKind.DirectManufacturer);
            var repoCount = availableUpdates.Count(u => u.SourceKind == UpdateSourceKind.CustomRepository);
            findings.Add(new AuditFinding
            {
                Severity = AuditSeverity.Warn,
                Category = "Updates Ready",
                Title = $"{availableUpdates.Count} newer driver update(s) available",
                Detail = $"Breakdown: {wuaCount} via Windows Update (Device Manager channel), {directCount} via Direct Manufacturer, {repoCount} via Custom Repositories. Open the Updates tab to review and install."
            });
        }

        // 4. Generic Microsoft Inbox Drivers on Vendor Hardware
        var inbox = devices.Where(d => d.HealthStatus == DriverHealthStatus.InboxGeneric).ToList();
        if (inbox.Count > 0)
        {
            findings.Add(new AuditFinding
            {
                Severity = AuditSeverity.Warn,
                Category = "Inbox Drivers",
                Title = $"{inbox.Count} device(s) running generic Microsoft inbox drivers",
                Detail = string.Join("; ", inbox.Take(4).Select(i => $"{i.DisplayName} ({i.SiliconVendor} hardware)")) +
                         ". Installing vendor or Windows Update WHQL drivers unlocks full hardware features and stability."
            });
        }

        // 5. Aged Primary Drivers (>2 years old)
        var aged = devices.Where(d => d.HealthStatus == DriverHealthStatus.Outdated).ToList();
        if (aged.Count > 0)
        {
            findings.Add(new AuditFinding
            {
                Severity = AuditSeverity.Info,
                Category = "Driver Age",
                Title = $"{aged.Count} primary device driver(s) are older than 2 years",
                Detail = string.Join("; ", aged.Take(4).Select(a => $"{a.DisplayName} ({a.DriverDateText})")) +
                         (aged.Count > 4 ? $" +{aged.Count - 4} more" : "")
            });
        }

        // 6. Unsigned drivers
        var unsigned = devices.Where(d => !d.IsSigned && !d.HasProblem).ToList();
        if (unsigned.Count > 0)
        {
            findings.Add(new AuditFinding
            {
                Severity = AuditSeverity.Warn,
                Category = "Security",
                Title = $"{unsigned.Count} unsigned driver(s) active",
                Detail = string.Join(", ", unsigned.Take(4).Select(u => $"{u.DisplayName} ({u.InfName})"))
            });
        }

        // 7. Stale / Superseded Driver Store packages
        var stale = storePackages.Where(p => p.IsSuperseded && !p.IsInUse).ToList();
        if (stale.Count >= 3)
        {
            findings.Add(new AuditFinding
            {
                Severity = AuditSeverity.Info,
                Category = "Driver Store",
                Title = $"{stale.Count} superseded/old OEM driver package(s) cached in Windows Driver Store",
                Detail = "Older versions of installed drivers remain in C:\\Windows\\System32\\DriverStore. You can safely review and prune stale packages in the Driver Store & Backup tab."
            });
        }

        return findings;
    }

    private static DateTime? ParseWmiDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 8) return null;
        if (DateTime.TryParseExact(raw[..8], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt))
            return dt;
        return null;
    }

    private static string MapCategory(string deviceClass, string displayName)
    {
        var c = deviceClass.ToUpperInvariant();
        if (c == "DISPLAY") return "Display";
        if (c == "NET") return "Network";
        if (c == "BLUETOOTH") return "Bluetooth";
        if (c is "MEDIA" or "AUDIOPROCESSINGOBJECT" || c.Contains("AUDIO") || displayName.Contains("Focusrite", StringComparison.OrdinalIgnoreCase))
            return "Audio";
        if (c is "SCSIADAPTER" or "DISKDRIVE" or "HDC" or "NVME") return "Storage & NVMe";
        if (c is "USB" or "USBDEVICE") return "USB";
        if (c is "SYSTEM" or "PROCESSOR" or "COMPUTER") return "Chipset & System";
        if (c is "HIDCLASS" or "KEYBOARD" or "MOUSE") return "Input & HID";
        if (c is "SECURITYDEVICES" or "FIRMWARE" or "BIOMETRIC") return "Security & Firmware";
        if (c is "MONITOR" or "CAMERA" or "IMAGE") return "Display & Imaging";
        return "Other";
    }

    private static int CategorySortOrder(string category) => category switch
    {
        "Display" => 1,
        "Network" => 2,
        "Audio" => 3,
        "Bluetooth" => 4,
        "Storage & NVMe" => 5,
        "Chipset & System" => 6,
        "USB" => 7,
        "Security & Firmware" => 8,
        "Input & HID" => 9,
        _ => 10
    };
}
