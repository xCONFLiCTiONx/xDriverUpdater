using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace xDriverUpdater.Models;

public sealed class LiveConnection : INotifyPropertyChanged
{
    private string _remoteHost = string.Empty;
    private bool _isFirewallBlocked;
    private int _connectionCount = 1;

    public string Key { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public int Pid { get; init; }
    public string ProcessPath { get; init; } = string.Empty;
    public string RemoteAddress { get; init; } = string.Empty;
    public int RemotePort { get; init; }
    public string State { get; init; } = "ESTABLISHED";

    public string RemoteHost
    {
        get => _remoteHost;
        set { if (_remoteHost != value) { _remoteHost = value; OnPropertyChanged(); } }
    }

    public int ConnectionCount
    {
        get => _connectionCount;
        set { if (_connectionCount != value) { _connectionCount = value; OnPropertyChanged(); } }
    }

    public bool IsSuspectedTelemetry { get; init; }
    public string TelemetryReason { get; init; } = string.Empty;
    public string FlagLabel => IsSuspectedTelemetry ? "FLAGGED" : "Normal";
    public string FlagBadgeColor => IsSuspectedTelemetry ? "#B91C1C" : "#334155";

    public bool IsFirewallBlocked
    {
        get => _isFirewallBlocked;
        set
        {
            if (_isFirewallBlocked != value)
            {
                _isFirewallBlocked = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FirewallButtonLabel));
            }
        }
    }

    public string FirewallButtonLabel => IsFirewallBlocked ? "Unblock" : "Block Outbound";
    public bool CanFirewallBlock => !string.IsNullOrWhiteSpace(ProcessPath) && System.IO.File.Exists(ProcessPath);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
