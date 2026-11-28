using System.Management;
using System.Text.RegularExpressions;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed class PowerHardwareInspector
{
    private readonly PowerCfgService _powerCfg;

    public PowerHardwareInspector(PowerCfgService powerCfg)
    {
        _powerCfg = powerCfg;
    }

    public async Task<PowerHardwareProfile> InspectAsync()
    {
        return await Task.Run(() =>
        {
            var profile = new PowerHardwareProfile();

            InspectOs(profile);
            InspectCpu(profile);
            InspectGpus(profile);
            InspectBoardAndMemory(profile);
            InspectBattery(profile);
            InspectDisks(profile);
            InspectSleepStates(profile);
            profile.ExistingSchemes = _powerCfg.ListSchemes();

            return profile;
        });
    }

    private static void InspectOs(PowerHardwareProfile p)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                p.OsCaption = obj["Caption"]?.ToString()?.Trim() ?? "Windows";
                p.OsVersion = obj["Version"]?.ToString()?.Trim() ?? "";
                p.OsBuild = obj["BuildNumber"]?.ToString()?.Trim() ?? "";
                break;
            }
        }
        catch { }
    }

    private static void InspectCpu(PowerHardwareProfile p)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                p.CpuName = obj["Name"]?.ToString()?.Trim() ?? "Unknown CPU";
                p.CpuVendor = obj["Manufacturer"]?.ToString()?.Trim() ?? "Unknown";
                p.PhysicalCores = Convert.ToInt32(obj["NumberOfCores"] ?? 0);
                p.LogicalProcessors = Convert.ToInt32(obj["NumberOfLogicalProcessors"] ?? 0);
                p.MaxClockMhz = Convert.ToInt32(obj["MaxClockSpeed"] ?? 0);
                break;
            }
        }
        catch { }
    }

    private static void InspectGpus(PowerHardwareProfile p)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility, DriverVersion, Status FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                p.Gpus.Add(new PowerGpuInfo
                {
                    Name = name,
                    Vendor = obj["AdapterCompatibility"]?.ToString()?.Trim() ?? "",
                    DriverVersion = obj["DriverVersion"]?.ToString()?.Trim() ?? "",
                    Status = obj["Status"]?.ToString()?.Trim() ?? ""
                });
            }
        }
        catch { }
    }

    private static void InspectBoardAndMemory(PowerHardwareProfile p)
    {
        try
        {
            using var boardSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject obj in boardSearcher.Get())
            {
                var mfg = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                var prod = obj["Product"]?.ToString()?.Trim() ?? "";
                p.Motherboard = string.IsNullOrWhiteSpace(mfg) ? prod : $"{mfg} {prod}";
                break;
            }
        }
        catch { }

        try
        {
            using var csSearcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in csSearcher.Get())
            {
                var bytes = Convert.ToDouble(obj["TotalPhysicalMemory"] ?? 0);
                p.TotalRamGb = Math.Round(bytes / (1024d * 1024 * 1024), 1);
                break;
            }
        }
        catch { }
    }

    private static void InspectBattery(PowerHardwareProfile p)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery");
            p.HasBattery = searcher.Get().Count > 0;
        }
        catch
        {
            p.HasBattery = false;
        }
    }

    private static void InspectDisks(PowerHardwareProfile p)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\ROOT\Microsoft\Windows\Storage");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["FriendlyName"]?.ToString()?.Trim() ?? "Disk";
                var busCode = Convert.ToInt32(obj["BusType"] ?? 0);
                var mediaCode = Convert.ToInt32(obj["MediaType"] ?? 0);
                var sizeBytes = Convert.ToDouble(obj["Size"] ?? 0);

                var bus = busCode switch
                {
                    7 => "USB",
                    11 => "SATA",
                    17 => "NVMe",
                    8 => "RAID",
                    14 => "File Backed Virtual",
                    _ => $"Bus-{busCode}"
                };

                if (busCode == 14 || name.Contains("Virtual Disk", StringComparison.OrdinalIgnoreCase))
                    continue;

                var media = mediaCode switch
                {
                    3 => "HDD",
                    4 => "SSD",
                    5 => "SCM",
                    _ => "Unspecified"
                };

                p.Disks.Add(new PowerDiskInfo
                {
                    FriendlyName = name,
                    BusType = bus,
                    MediaType = media,
                    SizeGb = Math.Round(sizeBytes / 1_000_000_000d, 0)
                });
            }
        }
        catch { }
    }

    private void InspectSleepStates(PowerHardwareProfile p)
    {
        var (exitCode, output) = _powerCfg.RunPowerCfg("/a");
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output)) return;

        var parts = Regex.Split(output, @"The following sleep states are not available on this system:", RegexOptions.IgnoreCase);
        var available = parts.Length > 0 ? parts[0] : "";
        var unavailable = parts.Length > 1 ? parts[1] : "";

        p.SupportsS3Standby = available.Contains("Standby (S3)", StringComparison.OrdinalIgnoreCase);
        p.SupportsS0LowPowerIdle = available.Contains("Standby (S0 Low Power Idle)", StringComparison.OrdinalIgnoreCase);
        p.SupportsHibernate = available.Contains("Hibernate", StringComparison.OrdinalIgnoreCase);
        p.SupportsHybridSleep = available.Contains("Hybrid Sleep", StringComparison.OrdinalIgnoreCase);
        p.HypervisorBlocksHybridSleep = unavailable.Contains("Hybrid Sleep", StringComparison.OrdinalIgnoreCase)
                                     && unavailable.Contains("hypervisor", StringComparison.OrdinalIgnoreCase);
    }
}
