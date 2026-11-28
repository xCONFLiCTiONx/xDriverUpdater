namespace xDriverUpdater.Models;

public sealed class RepositorySourceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string UrlOrPath { get; set; } = ""; // https://... or C:\... or builtin://default
    public bool Enabled { get; set; } = true;
    public bool IsBuiltIn { get; set; }
    public string Description { get; set; } = "";
    public DateTime? LastSyncedUtc { get; set; }
    public int PackageCount { get; set; }
    public string LastStatus { get; set; } = "Not synced";

    public string LastSyncedText => LastSyncedUtc.HasValue
        ? LastSyncedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "Never";
}

public sealed class RepositoryManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Maintainer { get; set; } = "";
    public string Description { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
    public List<RepoDriverPackage> Packages { get; set; } = new();
}

public sealed class RepoDriverPackage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string OemVendor { get; set; } = ""; // e.g., "Acer", "Dell", "Lenovo" (optional)
    public string FormFactor { get; set; } = "any"; // "any", "laptop", "desktop"
    public string Category { get; set; } = ""; // Display, Network, Audio, Chipset, Storage, Bluetooth, USB
    public List<string> HardwareIds { get; set; } = new(); // Supports exact or wildcard '*' patterns
    public string Version { get; set; } = "";
    public string ReleaseDate { get; set; } = ""; // YYYY-MM-DD
    public long SizeBytes { get; set; }
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string InstallerType { get; set; } = "inf-zip"; // inf-zip, cab, exe, msi, page
    public string SilentArgs { get; set; } = "";
    public string Channel { get; set; } = "stable"; // whql, stable, oem, studio, beta
    public string ReleaseNotes { get; set; } = "";
}

public sealed class AppSettings
{
    public bool CreateRestorePointBeforeInstall { get; set; } = true;
    public bool BackupDriverBeforeInstall { get; set; } = true;
    public bool PreferOemDriversOnLaptops { get; set; } = true;
    public bool IncludeWindowsUpdateChannel { get; set; } = true;
    public bool IncludeDirectManufacturerChecks { get; set; } = true;
    public bool IncludeCustomRepositories { get; set; } = true;
    public string BackupDirectory { get; set; } = "";
    public string DownloadCacheDirectory { get; set; } = "";
    public string LogDirectory { get; set; } = "";
    public bool ScheduledTaskEnabled { get; set; } = true;
    public string ScheduledTaskFrequency { get; set; } = "Daily"; // Daily, Weekly
    public string ScheduledTaskTime { get; set; } = "03:00"; // HH:mm 24-hour
    public string XSecurityEndpointUrl { get; set; } = "http://192.168.88.50:4789/api/events/ingest";
    public string XSecurityApiKey { get; set; } = "";
    public bool EnableLocalToastOnFailure { get; set; } = true;
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }
    public Dictionary<string, string> PinnedDevices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<RepositorySourceConfig> Repositories { get; set; } = new();

    // Power Plan state (merged from xPowerPlan)
    public Guid? PowerPlanManagedSchemeGuid { get; set; }
    public PowerPlanConfig? PowerPlanLastConfig { get; set; }
    public DateTime? PowerPlanLastAppliedUtc { get; set; }
}
