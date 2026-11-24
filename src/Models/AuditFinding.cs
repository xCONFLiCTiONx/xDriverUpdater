namespace xDriverUpdater.Models;

public enum AuditSeverity
{
    Ok,
    Info,
    Warn,
    Critical
}

public sealed class AuditFinding
{
    public AuditSeverity Severity { get; set; }
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";

    public string BadgeText => Severity switch
    {
        AuditSeverity.Critical => "CRIT",
        AuditSeverity.Warn => "WARN",
        AuditSeverity.Info => "INFO",
        _ => "OK"
    };

    public string BadgeColor => Severity switch
    {
        AuditSeverity.Critical => "#B91C1C",
        AuditSeverity.Warn => "#D97706",
        AuditSeverity.Info => "#2563EB",
        _ => "#15803D"
    };
}

public sealed class DriverStorePackage
{
    public string PublishedName { get; set; } = ""; // e.g., oem42.inf
    public string OriginalName { get; set; } = "";  // e.g., nv_dispi.inf
    public string ProviderName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public DateTime? DriverDate { get; set; }
    public string SignerName { get; set; } = "";
    public bool IsInUse { get; set; }
    public bool IsSuperseded { get; set; }

    public string DateText => DriverDate.HasValue ? DriverDate.Value.ToString("yyyy-MM-dd") : "—";
    public string StateBadgeText => IsInUse ? "ACTIVE" : IsSuperseded ? "STALE" : "CACHED";
    public string StateBadgeColor => IsInUse ? "#15803D" : IsSuperseded ? "#D97706" : "#475569";
}
