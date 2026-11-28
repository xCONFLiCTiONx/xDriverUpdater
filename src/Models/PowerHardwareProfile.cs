namespace xDriverUpdater.Models;

public sealed class PowerHardwareProfile
{
    public string OsCaption { get; set; } = "Windows";
    public string OsVersion { get; set; } = "";
    public string OsBuild { get; set; } = "";

    public string CpuName { get; set; } = "Unknown CPU";
    public string CpuVendor { get; set; } = "Unknown";
    public int PhysicalCores { get; set; }
    public int LogicalProcessors { get; set; }
    public int MaxClockMhz { get; set; }
    public bool IsAmdCpu => CpuVendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) || CpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
    public bool IsAmdApuModel => IsAmdCpu && (CpuName.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase) || CpuName.Contains("G ", StringComparison.OrdinalIgnoreCase) || CpuName.EndsWith("G", StringComparison.OrdinalIgnoreCase));
    public bool IsIntelCpu => CpuVendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) || CpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase);

    public List<PowerGpuInfo> Gpus { get; set; } = new();
    public bool HasNvidiaGpu => Gpus.Any(g => g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
    public bool HasActiveAmdGpu => Gpus.Any(g => g.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || g.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase));
    public bool HasDisabledAmdIgpu => IsAmdApuModel && !HasActiveAmdGpu;

    public string Motherboard { get; set; } = "Unknown";
    public double TotalRamGb { get; set; }
    public bool HasBattery { get; set; }

    public List<PowerDiskInfo> Disks { get; set; } = new();
    public bool HasNvmeSsd => Disks.Any(d => d.BusType.Equals("NVMe", StringComparison.OrdinalIgnoreCase));
    public bool HasSataSsd => Disks.Any(d => d.BusType.Equals("SATA", StringComparison.OrdinalIgnoreCase));
    public bool HasExternalUsbDrive => Disks.Any(d => d.BusType.Equals("USB", StringComparison.OrdinalIgnoreCase));

    public bool SupportsS3Standby { get; set; }
    public bool SupportsS0LowPowerIdle { get; set; }
    public bool SupportsHibernate { get; set; }
    public bool SupportsHybridSleep { get; set; }
    public bool HypervisorBlocksHybridSleep { get; set; }

    public List<PowerSchemeInfo> ExistingSchemes { get; set; } = new();
    public PowerSchemeInfo? ActiveScheme => ExistingSchemes.FirstOrDefault(s => s.IsActive);
}

public sealed class PowerGpuInfo
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public string Status { get; set; } = "";
}

public sealed class PowerDiskInfo
{
    public string FriendlyName { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string BusType { get; set; } = "";
    public double SizeGb { get; set; }
    public string Summary => $"{FriendlyName} ({BusType}{(string.IsNullOrWhiteSpace(MediaType) || MediaType == "Unspecified" ? "" : $" {MediaType}")}, {SizeGb:F0} GB)";
}

public sealed class PowerSchemeInfo
{
    public Guid Guid { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
    public bool IsBuiltIn => Guid == PowerGuids.SchemeBalanced
                          || Guid == PowerGuids.SchemeHighPerformance
                          || Guid == PowerGuids.SchemePowerSaver;
    public bool IsManagedPlan => Name.StartsWith("xPowerPlan", StringComparison.OrdinalIgnoreCase)
                              || Name.StartsWith("xSystemForge", StringComparison.OrdinalIgnoreCase);
    public string ActiveBadge => IsActive ? "ACTIVE" : "";
    public string DisplayLine => IsActive ? $"{Name}  (Active)" : Name;
}
