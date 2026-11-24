namespace xDriverUpdater.Models;

public enum DriverHealthStatus
{
    UpToDate,
    UpdateAvailable,
    InboxGeneric,
    OemAdvisory,
    Outdated,
    Problem
}

public enum RecommendedSource
{
    WindowsUpdate,
    OemVendorFirst,
    DirectManufacturer,
    CustomRepository
}

public sealed class DriverDevice
{
    public string DeviceName { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string DeviceClass { get; set; } = "OTHER";
    public string Category { get; set; } = "Other";
    public string DeviceId { get; set; } = "";
    public string PnpDeviceId { get; set; } = "";
    public string HardwareId { get; set; } = "";
    public List<string> HardwareIds { get; set; } = new();
    public List<string> CompatibleIds { get; set; } = new();

    public string Manufacturer { get; set; } = "";
    public string DriverProvider { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public DateTime? DriverDate { get; set; }
    public string InfName { get; set; } = "";
    public bool IsSigned { get; set; } = true;
    public string Signer { get; set; } = "";
    public int ConfigManagerErrorCode { get; set; }
    public string Status { get; set; } = "OK";

    // Parsed PCI / USB identifiers
    public string BusType { get; set; } = ""; // PCI, USB, HDAUDIO, ACPI, HID, etc.
    public string VendorId { get; set; } = ""; // e.g. 10DE, 8086, 1002
    public string ProductId { get; set; } = ""; // e.g. 2684
    public string SubsystemVendorId { get; set; } = ""; // e.g. 1025 (Acer), 1028 (Dell), 1462 (MSI)
    public string SubsystemFull { get; set; } = ""; // e.g. 38911462

    // Smart OEM & Vendor Classification
    public string SiliconVendor { get; set; } = ""; // NVIDIA, AMD, Intel, Realtek, Focusrite, etc.
    public string SubsystemOemName { get; set; } = ""; // Acer, Dell, Lenovo, HP, ASUS, MSI, etc.
    public bool IsLaptopOemCustomized { get; set; }
    public bool IsInboxMicrosoftDriver { get; set; }
    public bool IsDisabledByUser => ConfigManagerErrorCode == 22;
    public bool HasProblem => ConfigManagerErrorCode != 0 && ConfigManagerErrorCode != 22;

    // Recommendation & Update Candidates
    public RecommendedSource RecommendedSource { get; set; } = RecommendedSource.WindowsUpdate;
    public string SourceRationale { get; set; } = "";
    public string DirectVendorUrl { get; set; } = "";
    public string OemVendorUrl { get; set; } = "";
    public string MicrosoftCatalogUrl { get; set; } = "";

    public DriverHealthStatus HealthStatus { get; set; } = DriverHealthStatus.UpToDate;
    public List<DriverUpdateCandidate> AvailableUpdates { get; set; } = new();

    // Display helpers
    public string DisplayName => !string.IsNullOrWhiteSpace(FriendlyName) ? FriendlyName : DeviceName;
    public string DriverDateText => DriverDate.HasValue ? DriverDate.Value.ToString("yyyy-MM-dd") : "—";
    public string AgeText
    {
        get
        {
            if (!DriverDate.HasValue) return "—";
            var days = (DateTime.Now - DriverDate.Value).TotalDays;
            if (days < 30) return $"{Math.Max(0, (int)days)}d";
            if (days < 365) return $"{(int)(days / 30)}mo";
            return $"{days / 365.25:0.0}yr";
        }
    }

    public string EffectiveSourceLabel => RecommendedSource switch
    {
        RecommendedSource.OemVendorFirst => $"OEM ({(string.IsNullOrEmpty(SubsystemOemName) ? "Laptop OEM" : SubsystemOemName)}) / WU",
        RecommendedSource.DirectManufacturer => $"Direct ({(string.IsNullOrEmpty(SiliconVendor) ? Manufacturer : SiliconVendor)})",
        RecommendedSource.CustomRepository => "Custom Repo",
        _ => "Windows Update"
    };

    public string BestUpdateSummary
    {
        get
        {
            if (AvailableUpdates.Count == 0) return "—";
            var best = AvailableUpdates.OrderByDescending(u => u.MatchScore).First();
            return $"{best.Version} ({best.SourceLabel})";
        }
    }

    public string StatusBadgeText => HealthStatus switch
    {
        DriverHealthStatus.Problem => $"ERR {ConfigManagerErrorCode}",
        DriverHealthStatus.UpdateAvailable => "UPDATE",
        DriverHealthStatus.OemAdvisory => "OEM",
        DriverHealthStatus.InboxGeneric => "INBOX",
        DriverHealthStatus.Outdated => "AGED",
        _ => "OK"
    };

    public string StatusBadgeColor => HealthStatus switch
    {
        DriverHealthStatus.Problem => "#B91C1C",
        DriverHealthStatus.UpdateAvailable => "#2563EB",
        DriverHealthStatus.OemAdvisory => "#9333EA",
        DriverHealthStatus.InboxGeneric => "#D97706",
        DriverHealthStatus.Outdated => "#B45309",
        _ => "#15803D"
    };
}
