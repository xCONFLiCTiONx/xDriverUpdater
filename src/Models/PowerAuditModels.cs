namespace xDriverUpdater.Models;

public enum PowerFindingSeverity
{
    Good,
    Info,
    Warning
}

public sealed class PowerAuditFinding
{
    public PowerFindingSeverity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Category { get; set; } = "";

    public string BadgeText => Severity switch
    {
        PowerFindingSeverity.Warning => "TUNE",
        PowerFindingSeverity.Good => "OK",
        _ => "INFO"
    };

    public string BadgeColor => Severity switch
    {
        PowerFindingSeverity.Warning => "#D97706",
        PowerFindingSeverity.Good => "#15803D",
        _ => "#2563EB"
    };
}

public sealed class PowerDiffRow
{
    public string Category { get; set; } = "";
    public string SettingName { get; set; } = "";
    public string CurrentValue { get; set; } = "—";
    public string TargetValue { get; set; } = "";
    public bool IsChanged { get; set; }
    public string StatusText => IsChanged ? "CHANGE" : "MATCH";
    public string StatusColor => IsChanged ? "#3B82F6" : "#475569";
    public string Reason { get; set; } = "";
}

public sealed class PowerApplyResult
{
    public bool Success { get; set; }
    public Guid AppliedSchemeGuid { get; set; }
    public string AppliedSchemeName { get; set; } = "";
    public int SettingsApplied { get; set; }
    public int SettingsVerified { get; set; }
    public int SettingsFailed { get; set; }
    public string? BackupPath { get; set; }
    public List<string> Logs { get; set; } = new();
    public string Summary { get; set; } = "";
}
