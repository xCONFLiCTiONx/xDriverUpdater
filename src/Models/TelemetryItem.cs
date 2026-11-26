using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace xDriverUpdater.Models;

public enum TelemetryCategory
{
    Service,
    ScheduledTask,
    RegistryPolicy,
    Startup,
    AppxBloat
}

public enum RiskLevel
{
    Safe,      // Pure telemetry / bloat safe to disable or remove with zero UX impact
    Moderate,  // Diagnostics, updaters, or web search features
    Optional   // Features some users want (Phone Link, Xbox, HP Printer, Insider, GPU auto-update)
}

public sealed class TelemetryItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isActive;
    private string _statusText = string.Empty;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public TelemetryCategory Category { get; init; }
    public string CategoryLabel => Category switch
    {
        TelemetryCategory.Service => "Service",
        TelemetryCategory.ScheduledTask => "Scheduled Task",
        TelemetryCategory.RegistryPolicy => "Registry Policy",
        TelemetryCategory.Startup => "Startup",
        TelemetryCategory.AppxBloat => "Appx Bloat",
        _ => Category.ToString()
    };

    public string Vendor { get; init; } = "Microsoft";
    public string Description { get; init; } = string.Empty;
    public RiskLevel Risk { get; init; } = RiskLevel.Safe;
    public string RiskLabel => Risk switch
    {
        RiskLevel.Safe => "Safe",
        RiskLevel.Moderate => "Moderate",
        RiskLevel.Optional => "Optional",
        _ => Risk.ToString()
    };

    public string RiskBadgeColor => Risk switch
    {
        RiskLevel.Safe => "#15803D",
        RiskLevel.Moderate => "#D97706",
        _ => "#475569"
    };

    public string StateBadgeColor => IsActive ? "#B91C1C" : "#15803D";
    public string StateBadgeText => IsActive
        ? (Category == TelemetryCategory.AppxBloat ? "INSTALLED" : "ACTIVE")
        : (Category == TelemetryCategory.AppxBloat ? "REMOVED" : "HARDENED");

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActionButtonLabel));
                OnPropertyChanged(nameof(StateBadgeColor));
                OnPropertyChanged(nameof(StateBadgeText));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
    }

    public string ActionButtonLabel => Category == TelemetryCategory.AppxBloat
        ? (IsActive ? "Remove" : "Removed")
        : (IsActive ? "Disable" : "Restore");

    // Target metadata for TelemetryController
    public string? ServiceName { get; init; }
    public int RestoreServiceStart { get; init; } = 3; // 2=Auto, 3=Manual

    public string? TaskFolder { get; init; }
    public string? TaskName { get; init; }

    public RegistryHive RegistryHive { get; init; }
    public string? RegistrySubKey { get; init; }
    public string? RegistryValueName { get; init; }
    public object? DisabledValue { get; init; }
    public object? RestoredValue { get; init; } // null means delete value on restore
    public RegistryValueKind RegistryValueKind { get; init; } = RegistryValueKind.DWord;

    public string? StartupKeyPath { get; init; }
    public RegistryHive StartupHive { get; init; }
    public string? StartupValueName { get; init; }
    public string? StartupCommand { get; init; }

    public string? AppxPackageFullName { get; init; }
    public string? AppxPackageName { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
