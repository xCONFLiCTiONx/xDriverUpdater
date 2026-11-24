namespace xDriverUpdater.Models;

public enum ChassisFormFactor
{
    Unknown,
    Desktop,
    Laptop,
    TabletOrConvertible,
    ServerOrWorkstation
}

public sealed class SystemProfile
{
    public string OsCaption { get; set; } = "Windows";
    public string OsVersion { get; set; } = "";
    public string OsBuild { get; set; } = "";
    public string OsArch { get; set; } = "x64";

    public string SystemManufacturer { get; set; } = "Unknown";
    public string SystemModel { get; set; } = "Unknown";
    public string SystemSku { get; set; } = "";
    public string SerialNumber { get; set; } = "";

    public string BoardManufacturer { get; set; } = "Unknown";
    public string BoardProduct { get; set; } = "Unknown";

    public string BiosManufacturer { get; set; } = "";
    public string BiosVersion { get; set; } = "";
    public string BiosReleaseDate { get; set; } = "";

    public string CpuName { get; set; } = "Unknown";
    public bool HasBattery { get; set; }
    public int PcSystemType { get; set; }
    public int[] ChassisTypes { get; set; } = Array.Empty<int>();
    public ChassisFormFactor FormFactor { get; set; } = ChassisFormFactor.Unknown;

    public bool IsLaptop => FormFactor is ChassisFormFactor.Laptop or ChassisFormFactor.TabletOrConvertible;

    public string NormalizedOem { get; set; } = "Generic";
    public string OemSupportUrl { get; set; } = "";

    public string FormFactorDisplay => FormFactor switch
    {
        ChassisFormFactor.Laptop => "Laptop / Mobile OEM",
        ChassisFormFactor.TabletOrConvertible => "Convertible / Tablet OEM",
        ChassisFormFactor.Desktop => "Desktop / Custom PC",
        ChassisFormFactor.ServerOrWorkstation => "Workstation / Server",
        _ => "Unknown Form Factor"
    };

    public string PlatformSummary => IsLaptop
        ? $"{SystemManufacturer} {SystemModel} ({FormFactorDisplay})"
        : $"{BoardManufacturer} {BoardProduct} ({FormFactorDisplay})";
}
