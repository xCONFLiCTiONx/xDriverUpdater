using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;

namespace xDriverUpdater.Services;

public sealed class PrivacyGuardStatus
{
    public bool IsProcessRunning { get; set; }
    public int ProcessId { get; set; }
    public bool IsTunnelUp { get; set; }
    public string TunnelIp { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public DateTime CheckedAt { get; set; } = DateTime.Now;

    public string Headline => IsProcessRunning
        ? (IsTunnelUp ? "Protected (Tunnel Active)" : "Running (Tunnel Idle)")
        : "NOT RUNNING";

    public string BadgeText => IsProcessRunning
        ? (IsTunnelUp ? "PROTECTED" : "IDLE")
        : "STOPPED";

    public string BadgeBg => IsProcessRunning
        ? (IsTunnelUp ? "#0E2918" : "#2B1B0E")
        : "#331417";

    public string BadgeFg => IsProcessRunning
        ? (IsTunnelUp ? "#22C55E" : "#FAAD3F")
        : "#EF4444";
}

public sealed class PrivacyGuardMonitorService
{
    public PrivacyGuardStatus CheckStatus(string? configuredExePath = null)
    {
        var status = new PrivacyGuardStatus
        {
            ExecutablePath = ResolveExecutablePath(configuredExePath)
        };

        try
        {
            var procs = Process.GetProcessesByName("xPrivacyGuard");
            if (procs.Length > 0)
            {
                status.IsProcessRunning = true;
                status.ProcessId = procs[0].Id;
                try
                {
                    var mainPath = procs[0].MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(mainPath))
                        status.ExecutablePath = mainPath;
                }
                catch { }
                foreach (var p in procs) p.Dispose();
            }
        }
        catch { }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.Name.Contains("xPrivacyGuard", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("xPrivacyGuard", StringComparison.OrdinalIgnoreCase))
                {
                    if (nic.OperationalStatus == OperationalStatus.Up)
                    {
                        status.IsTunnelUp = true;
                        var ipv4 = nic.GetIPProperties().UnicastAddresses
                            .FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                        if (ipv4 != null)
                            status.TunnelIp = ipv4.Address.ToString();
                    }
                    break;
                }
            }
        }
        catch { }

        return status;
    }

    public static string ResolveExecutablePath(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xPrograms", "xPrivacyGuard", "xPrivacyGuard.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "xPrivacyGuard", "xPrivacyGuard.exe")
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return candidates[0];
    }

    public (bool Success, string Message) LaunchPrivacyGuard(string? configuredPath = null)
    {
        var exe = ResolveExecutablePath(configuredPath);
        if (!File.Exists(exe))
            return (false, $"xPrivacyGuard executable not found at: {exe}");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                UseShellExecute = true
            });
            return (true, $"Launched xPrivacyGuard ({exe}).");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to launch xPrivacyGuard: {ex.Message}");
        }
    }
}
