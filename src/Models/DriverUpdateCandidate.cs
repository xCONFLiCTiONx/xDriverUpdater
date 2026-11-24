namespace xDriverUpdater.Models;

public enum UpdateSourceKind
{
    WindowsUpdate,
    MicrosoftCatalog,
    DirectManufacturer,
    CustomRepository
}

public sealed class DriverUpdateCandidate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public UpdateSourceKind SourceKind { get; set; }
    public string SourceName { get; set; } = ""; // e.g. "Windows Update (WUA)", "NVIDIA Official", "My Custom Repo"
    public string Title { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime? ReleaseDate { get; set; }
    public long SizeBytes { get; set; }
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string InstallerType { get; set; } = "wua"; // wua, inf-zip, cab, exe, msi, page
    public string SilentArgs { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string Channel { get; set; } = "stable"; // whql, stable, oem, studio, beta
    public bool IsOemSpecific { get; set; }
    public int MatchScore { get; set; } // 100 = exact OEM+HWID, 85 = exact HWID, 70 = WUA matched, 50 = generic
    public string MatchedHardwareId { get; set; } = "";
    public bool RebootRequired { get; set; }

    // Reference to matched device (for UI display in Updates tab)
    public string DeviceName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public bool IsSelected { get; set; } = true;

    // For WUA COM update handle (stored as UpdateID + RevisionNumber)
    public string WuaUpdateId { get; set; } = "";
    public int WuaRevisionNumber { get; set; }

    public string SourceLabel => SourceKind switch
    {
        UpdateSourceKind.WindowsUpdate => "Windows Update",
        UpdateSourceKind.MicrosoftCatalog => "MS Catalog",
        UpdateSourceKind.DirectManufacturer => string.IsNullOrWhiteSpace(SourceName) ? "Manufacturer" : SourceName,
        UpdateSourceKind.CustomRepository => string.IsNullOrWhiteSpace(SourceName) ? "Custom Repo" : SourceName,
        _ => "Unknown"
    };

    public string SourceBadgeColor => SourceKind switch
    {
        UpdateSourceKind.WindowsUpdate => "#2563EB",
        UpdateSourceKind.MicrosoftCatalog => "#0284C7",
        UpdateSourceKind.DirectManufacturer => "#16A34A",
        UpdateSourceKind.CustomRepository => "#7C3AED",
        _ => "#475569"
    };

    public string SizeText => SizeBytes <= 0
        ? "—"
        : SizeBytes >= 1024 * 1024
            ? $"{SizeBytes / (1024.0 * 1024.0):0.0} MB"
            : $"{SizeBytes / 1024.0:0} KB";

    public string ReleaseDateText => ReleaseDate.HasValue ? ReleaseDate.Value.ToString("yyyy-MM-dd") : "—";

    public string CatalogUpdateGuid { get; set; } = "";
    public bool IsNewerThanInstalled { get; set; } = true;

    public string PickerLabel
    {
        get
        {
            var tag = IsNewerThanInstalled ? "" : " [Older/Rollback]";
            var ch = string.IsNullOrWhiteSpace(Channel) ? "" : $" • {Channel.ToUpperInvariant()}";
            var dt = ReleaseDate.HasValue ? $" ({ReleaseDate.Value:yyyy-MM-dd})" : "";
            return $"v{Version}{ch}{dt}{tag}";
        }
    }
}

/// <summary>
/// Represents one device in the Updates Available grid with a dropdown of all discovered
/// versions (newer, current, and older/rollback) so the user can choose the exact version to install.
/// </summary>
public sealed class DeviceUpdateGroup : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Category { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public List<DriverUpdateCandidate> Versions { get; set; } = new();

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Notify(nameof(IsSelected)); }
    }

    private DriverUpdateCandidate? _selectedVersion;
    public DriverUpdateCandidate? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            _selectedVersion = value;
            Notify(nameof(SelectedVersion));
            Notify(nameof(SourceLabel));
            Notify(nameof(SourceBadgeColor));
            Notify(nameof(ReleaseDateText));
            Notify(nameof(SizeText));
            Notify(nameof(InstallerType));
            Notify(nameof(VersionCountText));
        }
    }

    public string SourceLabel => SelectedVersion?.SourceLabel ?? "—";
    public string SourceBadgeColor => SelectedVersion?.SourceBadgeColor ?? "#475569";
    public string ReleaseDateText => SelectedVersion?.ReleaseDateText ?? "—";
    public string SizeText => SelectedVersion?.SizeText ?? "—";
    public string InstallerType => SelectedVersion?.InstallerType ?? "—";
    public string VersionCountText => $"{Versions.Count} ver{(Versions.Count == 1 ? "" : "s")}";
    public bool HasNewerVersion => Versions.Any(v => v.IsNewerThanInstalled);
}
