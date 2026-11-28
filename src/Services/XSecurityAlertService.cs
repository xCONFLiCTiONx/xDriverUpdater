using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed class DriverUpdateFailureRecord
{
    public string DeviceName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string Title { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>
/// Sends driver update failure notifications to the xServer xSecurity events module
/// (which records the incident and broadcasts toast/Web Push notifications to all connected devices),
/// triggers a local Windows toast notification when interactive, and writes to Windows Event Log.
/// </summary>
public sealed class XSecurityAlertService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    static XSecurityAlertService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("xDriverUpdater/1.0 (Windows NT 10.0; Win64; x64)");
    }

    public async Task<(bool Sent, string Message)> SendDriverUpdateFailureAlertAsync(
        AppSettings settings,
        string title,
        string summary,
        IReadOnlyList<DriverUpdateFailureRecord> failures,
        string logFilePath,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var host = Environment.MachineName;
        var sb = new StringBuilder();
        sb.AppendLine($"Host: {host} ({Environment.UserName})");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (!string.IsNullOrWhiteSpace(summary))
            sb.AppendLine($"Summary: {summary}");

        if (failures.Count > 0)
        {
            sb.AppendLine("Failed Driver(s):");
            foreach (var f in failures.Take(8))
                sb.AppendLine($"- {f.DeviceName} ({f.InstalledVersion} -> {f.TargetVersion}, {f.SourceLabel}): {f.Error}");
            if (failures.Count > 8)
                sb.AppendLine($"- ... and {failures.Count - 8} more (see log file)");
        }

        if (!string.IsNullOrWhiteSpace(logFilePath))
            sb.AppendLine($"Log File: {logFilePath}");

        var pushBody = failures.Count > 0
            ? $"{failures.Count} driver update(s) failed on {host}: {failures[0].DeviceName} — {failures[0].Error}"
            : $"{summary} (Host: {host})";

        var payload = new
        {
            key = $"xdriverupdater-fail-{host.ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMddHHmmss}",
            title,
            severity = "high",
            confidence = "high",
            source = "xDriverUpdater",
            eventType = "driver_update_failure",
            url = "/security/#incidents",
            pushTitle = title,
            pushBody = pushBody.Length > 450 ? pushBody[..450] : pushBody,
            details = sb.ToString().Trim(),
            reopenOnPersist = true,
            recommendedAction = new
            {
                type = "info",
                label = string.IsNullOrWhiteSpace(logFilePath)
                    ? $"Inspect xDriverUpdater logs on {host}"
                    : $"Inspect log: {logFilePath}"
            }
        };

        return await PostToXSecurityAsync(settings, payload, log, ct);
    }

    public async Task<(bool Sent, string Message)> SendTestAlertAsync(
        AppSettings settings,
        string logFilePath,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var host = Environment.MachineName;
        var title = $"[{host}] xDriverUpdater Test Alert";
        var body = $"Test notification from xDriverUpdater on {host} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. Confirms xSecurity events ingestion and multi-device toast notifications.";

        var payload = new
        {
            key = $"xdriverupdater-test-{host.ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMddHHmmss}",
            title,
            severity = "medium",
            confidence = "high",
            source = "xDriverUpdater",
            eventType = "driver_update_test",
            url = "/security/#incidents",
            pushTitle = title,
            pushBody = body,
            details = $"{body}\nLog File: {logFilePath}",
            reopenOnPersist = true,
            recommendedAction = new
            {
                type = "info",
                label = "Safe to resolve — test alert verified"
            }
        };

        var result = await PostToXSecurityAsync(settings, payload, log, ct);
        if (settings.EnableLocalToastOnFailure)
            TryShowLocalWindowsToast(title, body, log);
        return result;
    }

    private static async Task<(bool Sent, string Message)> PostToXSecurityAsync(
        AppSettings settings,
        object payload,
        Action<string>? log,
        CancellationToken ct)
    {
        var endpoints = new List<string>();
        var configured = settings.XSecurityEndpointUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            endpoints.Add(configured);

        if (endpoints.Count == 0)
            return (false, "No xSecurity Events Endpoint configured. Enter your xServer events URL in Security Center settings.");

        var json = JsonSerializer.Serialize(payload);
        var errors = new List<string>();

        foreach (var endpoint in endpoints)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(settings.XSecurityApiKey))
                    req.Headers.Add("X-API-Key", settings.XSecurityApiKey.Trim());

                using var resp = await Http.SendAsync(req, ct);
                var respBody = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    log?.Invoke($"[xSecurity] Event delivered to {endpoint} (HTTP {(int)resp.StatusCode}).");
                    return (true, $"Delivered to xSecurity ({endpoint}) and broadcast to connected devices.");
                }

                var err = $"{endpoint} returned HTTP {(int)resp.StatusCode}: {respBody}";
                errors.Add(err);
                log?.Invoke($"[xSecurity] {err}");
            }
            catch (Exception ex)
            {
                var err = $"{endpoint}: {ex.Message}";
                errors.Add(err);
                log?.Invoke($"[xSecurity] Delivery error ({err})");
            }
        }

        return (false, "Failed to reach xSecurity: " + string.Join(" | ", errors));
    }

    public static void TryShowLocalWindowsToast(string title, string body, Action<string>? log = null)
    {
        if (!Environment.UserInteractive)
        {
            log?.Invoke("[Toast] Skipping local WinRT toast (non-interactive session); xServer/xSecurity handles multi-device push.");
            return;
        }

        try
        {
            var safeTitle = EscapeXml(title);
            var safeBody = EscapeXml(body);
            var ps = $@"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml('<toast><visual><binding template=""ToastGeneric""><text>{safeTitle}</text><text>{safeBody}</text></binding></visual></toast>')
$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('xDriverUpdater').Show($toast)
";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
            var (exit, output) = DriverStoreAndInstallService.RunProcess(
                "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}");
            if (exit == 0)
                log?.Invoke("[Toast] Local Windows toast notification triggered.");
            else if (!string.IsNullOrWhiteSpace(output))
                log?.Invoke($"[Toast] Local toast warning: {output.Trim()}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[Toast] Local toast skipped: {ex.Message}");
        }
    }

    public static void TryWriteWindowsEventLog(string message, EventLogEntryType entryType, Action<string>? log = null)
    {
        try
        {
            const string source = "xDriverUpdater";
            if (!EventLog.SourceExists(source))
                EventLog.CreateEventSource(source, "Application");

            EventLog.WriteEntry(source, message.Length > 30000 ? message[..30000] : message, entryType, 4101);
            log?.Invoke($"[EventLog] Recorded {entryType} entry in Windows Application Event Log.");
        }
        catch
        {
            // Non-admin or restricted sessions fall back to the structured file log without failing.
        }
    }

    private static string EscapeXml(string s)
    {
        return (s ?? "")
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
