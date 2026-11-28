using System.IO;
using System.Text;
using System.Text.Json;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed class ScheduledTaskStatusInfo
{
    public bool Exists { get; set; }
    public string State { get; set; } = "Not Registered";
    public string NextRun { get; set; } = "—";
    public string LastRun { get; set; } = "Never";
    public string LastResult { get; set; } = "—";
    public string ExecutablePath { get; set; } = "";
}

/// <summary>
/// Manages the Windows Task Scheduler job (\xDriverUpdater\SilentDriverUpdates)
/// that silently checks, downloads, and installs driver updates and alerts xSecurity on failures.
/// </summary>
public sealed class ScheduledTaskService
{
    // NOTE: Must NOT be \xSecurityCenter\ - a root-level logon task named "xSecurityCenter" already exists,
    // and Task Scheduler cannot create a folder with the same name as an existing task (0x80070003).
    public const string TaskFolder = @"\xPrograms\xSecurityCenter\";
    public const string TaskName = "SilentDriverUpdates";
    public const string FullTaskPath = @"\xPrograms\xSecurityCenter\SilentDriverUpdates";

    public Task<ScheduledTaskStatusInfo> GetStatusAsync()
    {
        return Task.Run(() =>
        {
            var ps = @"
$t = Get-ScheduledTask -TaskPath '\xPrograms\xSecurityCenter\' -TaskName 'SilentDriverUpdates' -ErrorAction SilentlyContinue
if ($null -eq $t) {
    $t = Get-ScheduledTask -TaskPath '\xSystemForge\' -TaskName 'SilentDriverUpdates' -ErrorAction SilentlyContinue
}
if ($null -eq $t) {
    $t = Get-ScheduledTask -TaskPath '\xDriverUpdater\' -TaskName 'SilentDriverUpdates' -ErrorAction SilentlyContinue
}
if ($null -eq $t) {
    @{ exists = $false } | ConvertTo-Json -Compress
} else {
    $i = Get-ScheduledTaskInfo -TaskPath $t.TaskPath -TaskName $t.TaskName -ErrorAction SilentlyContinue
    $exe = ''
    if ($t.Actions -and $t.Actions.Count -gt 0) { $exe = $t.Actions[0].Execute }
    @{
        exists = $true
        state = $t.State.ToString()
        nextRun = if ($i -and $i.NextRunTime -and $i.NextRunTime.Year -gt 2000) { $i.NextRunTime.ToString('yyyy-MM-dd HH:mm') } else { '—' }
        lastRun = if ($i -and $i.LastRunTime -and $i.LastRunTime.Year -gt 2000) { $i.LastRunTime.ToString('yyyy-MM-dd HH:mm') } else { 'Never' }
        lastResult = if ($i) { ('0x{0:X}' -f $i.LastTaskResult) } else { '—' }
        exe = $exe
    } | ConvertTo-Json -Compress
}";
            var (exit, output) = RunEncodedPowerShell(ps);
            if (exit != 0 || string.IsNullOrWhiteSpace(output))
                return new ScheduledTaskStatusInfo();

            try
            {
                using var doc = JsonDocument.Parse(output.Trim());
                var root = doc.RootElement;
                if (!root.TryGetProperty("exists", out var exProp) || !exProp.GetBoolean())
                    return new ScheduledTaskStatusInfo();

                return new ScheduledTaskStatusInfo
                {
                    Exists = true,
                    State = root.TryGetProperty("state", out var st) ? st.GetString() ?? "Ready" : "Ready",
                    NextRun = root.TryGetProperty("nextRun", out var nr) ? nr.GetString() ?? "—" : "—",
                    LastRun = root.TryGetProperty("lastRun", out var lr) ? lr.GetString() ?? "Never" : "Never",
                    LastResult = root.TryGetProperty("lastResult", out var res) ? res.GetString() ?? "—" : "—",
                    ExecutablePath = root.TryGetProperty("exe", out var exe) ? exe.GetString() ?? "" : ""
                };
            }
            catch
            {
                return new ScheduledTaskStatusInfo();
            }
        });
    }

    public Task<(bool Success, string Message)> RegisterOrUpdateTaskAsync(AppSettings settings, Action<string>? log = null)
    {
        return Task.Run(() =>
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return (false, "Could not resolve xDriverUpdater.exe path.");

            var workDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            var timeStr = ParseTimeOrDefault(settings.ScheduledTaskTime);
            var isWeekly = string.Equals(settings.ScheduledTaskFrequency, "Weekly", StringComparison.OrdinalIgnoreCase);

            var triggerExpr = isWeekly
                ? $"New-ScheduledTaskTrigger -Weekly -DaysOfWeek Sunday -At '{timeStr}'"
                : $"New-ScheduledTaskTrigger -Daily -At '{timeStr}'";

            var escExe = exePath.Replace("'", "''");
            var escDir = workDir.Replace("'", "''");

            var ps = $@"
$ErrorActionPreference = 'Stop'
$action = New-ScheduledTaskAction -Execute '{escExe}' -Argument '--scheduled-update' -WorkingDirectory '{escDir}'
$trigger = {triggerExpr}
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -Hidden -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew
$user = ""$env:USERDOMAIN\$env:USERNAME""
# Clean up legacy xDriverUpdater / xSystemForge scheduled tasks if migrating to xSecurityCenter
Unregister-ScheduledTask -TaskPath '\xDriverUpdater\' -TaskName 'SilentDriverUpdates' -Confirm:$false -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskPath '\xSystemForge\' -TaskName 'SilentDriverUpdates' -Confirm:$false -ErrorAction SilentlyContinue
try {{
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType S4U -RunLevel Highest
    Register-ScheduledTask -TaskPath '\xPrograms\xSecurityCenter\' -TaskName 'SilentDriverUpdates' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Description 'xSecurityCenter silent driver update check, download, install, and xSecurity failure alerting' -Force | Out-Null
    Write-Output 'Registered (Elevated S4U)'
}} catch {{
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    Register-ScheduledTask -TaskPath '\xPrograms\xSecurityCenter\' -TaskName 'SilentDriverUpdates' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Description 'xSecurityCenter silent driver update check, download, install, and xSecurity failure alerting' -Force | Out-Null
    Write-Output 'Registered (Elevated Interactive)'
}}
";
            log?.Invoke($"[Scheduler] Registering {FullTaskPath} ({(isWeekly ? "Weekly Sun" : "Daily")} @ {timeStr}) -> \"{exePath}\" --scheduled-update");
            var (exit, output) = RunEncodedPowerShell(ps);
            var trimmed = output.Trim();
            if (exit == 0)
            {
                var msg = $"Scheduled task {FullTaskPath} registered ({(isWeekly ? "Weekly Sundays" : "Daily")} at {timeStr}). {trimmed}";
                log?.Invoke($"[Scheduler] {msg}");
                return (true, msg);
            }

            var err = $"Failed to register scheduled task (exit {exit}): {trimmed}";
            log?.Invoke($"[Scheduler] {err}");
            return (false, err);
        });
    }

    public Task<(bool Success, string Message)> RemoveTaskAsync(Action<string>? log = null)
    {
        return Task.Run(() =>
        {
            var ps = @"
$ErrorActionPreference = 'Stop'
$removed = $false
foreach ($p in '\xPrograms\xSecurityCenter\','\xSystemForge\','\xDriverUpdater\') {
    $t = Get-ScheduledTask -TaskPath $p -TaskName 'SilentDriverUpdates' -ErrorAction SilentlyContinue
    if ($null -ne $t) {
        Unregister-ScheduledTask -TaskPath $p -TaskName 'SilentDriverUpdates' -Confirm:$false
        $removed = $true
    }
}
if ($removed) {
    Write-Output 'Removed'
} else {
    Write-Output 'NotFound'
}";
            var (exit, output) = RunEncodedPowerShell(ps);
            var trimmed = output.Trim();
            if (exit == 0)
            {
                var msg = trimmed == "NotFound"
                    ? $"Scheduled task {FullTaskPath} was not registered."
                    : $"Removed scheduled task {FullTaskPath}.";
                log?.Invoke($"[Scheduler] {msg}");
                return (true, msg);
            }

            var err = $"Failed to remove scheduled task: {trimmed}";
            log?.Invoke($"[Scheduler] {err}");
            return (false, err);
        });
    }

    public Task<(bool Success, string Message)> RunTaskNowAsync(Action<string>? log = null)
    {
        return Task.Run(() =>
        {
            var ps = @"
$ErrorActionPreference = 'Stop'
$p = '\xPrograms\xSecurityCenter\'
if ($null -eq (Get-ScheduledTask -TaskPath $p -TaskName 'SilentDriverUpdates' -ErrorAction SilentlyContinue)) { $p = '\xSystemForge\' }
Start-ScheduledTask -TaskPath $p -TaskName 'SilentDriverUpdates'
Write-Output 'Started'";
            var (exit, output) = RunEncodedPowerShell(ps);
            var trimmed = output.Trim();
            if (exit == 0)
            {
                var msg = $"Triggered scheduled task {FullTaskPath} in the background.";
                log?.Invoke($"[Scheduler] {msg}");
                return (true, msg);
            }

            var err = $"Failed to start scheduled task: {trimmed}";
            log?.Invoke($"[Scheduler] {err}");
            return (false, err);
        });
    }

    private static string ParseTimeOrDefault(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw) && TimeSpan.TryParse(raw.Trim(), out var ts) && ts.Hours is >= 0 and <= 23 && ts.Minutes is >= 0 and <= 59)
            return $"{ts.Hours:D2}:{ts.Minutes:D2}";
        return "03:00";
    }

    private static (int ExitCode, string Output) RunEncodedPowerShell(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return DriverStoreAndInstallService.RunProcess(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}");
    }
}
