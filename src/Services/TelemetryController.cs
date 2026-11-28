using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed class TelemetryController
{
    public static bool IsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public (bool Success, string Message) DisableItem(TelemetryItem item)
    {
        try
        {
            return item.Category switch
            {
                TelemetryCategory.Service => SetServiceState(item, disable: true),
                TelemetryCategory.ScheduledTask => SetTaskEnabled(item, enabled: false),
                TelemetryCategory.RegistryPolicy => SetRegistryPolicy(item, disable: true),
                TelemetryCategory.Startup => SetStartupState(item, disable: true),
                TelemetryCategory.AppxBloat => RemoveAppxPackage(item),
                _ => (false, $"Unsupported category: {item.Category}")
            };
        }
        catch (Exception ex)
        {
            return (false, $"Failed to disable {item.DisplayName}: {ex.Message}");
        }
    }

    public (bool Success, string Message) RestoreItem(TelemetryItem item)
    {
        try
        {
            return item.Category switch
            {
                TelemetryCategory.Service => SetServiceState(item, disable: false),
                TelemetryCategory.ScheduledTask => SetTaskEnabled(item, enabled: true),
                TelemetryCategory.RegistryPolicy => SetRegistryPolicy(item, disable: false),
                TelemetryCategory.Startup => SetStartupState(item, disable: false),
                TelemetryCategory.AppxBloat => (false, $"Reinstall '{item.DisplayName}' from Microsoft Store if needed."),
                _ => (false, $"Unsupported category: {item.Category}")
            };
        }
        catch (Exception ex)
        {
            return (false, $"Failed to restore {item.DisplayName}: {ex.Message}");
        }
    }

    private static (bool, string) RemoveAppxPackage(TelemetryItem item)
    {
        var target = !string.IsNullOrWhiteSpace(item.AppxPackageFullName)
            ? item.AppxPackageFullName
            : item.AppxPackageName;
        if (string.IsNullOrWhiteSpace(target))
            return (false, "Missing Appx package identifier.");

        var escaped = target.Replace("'", "''");
        var ps = !string.IsNullOrWhiteSpace(item.AppxPackageFullName)
            ? $"Remove-AppxPackage -Package '{escaped}' -ErrorAction Stop"
            : $"Get-AppxPackage -Name '{escaped}' | Remove-AppxPackage -ErrorAction Stop";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
        var (exit, output) = DriverStoreAndInstallService.RunProcess(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}");
        if (exit == 0)
        {
            item.IsActive = false;
            item.StatusText = "Removed";
            return (true, $"Removed Appx package '{item.DisplayName}' ({item.AppxPackageName}).");
        }

        return (false, $"Failed to remove '{item.DisplayName}': {output.Trim()}");
    }

    private static (bool, string) SetServiceState(TelemetryItem item, bool disable)
    {
        if (string.IsNullOrWhiteSpace(item.ServiceName))
            return (false, "Missing service name.");

        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{item.ServiceName}", writable: true);
        if (key == null)
            return (false, $"Service registry key not found: {item.ServiceName}");

        if (disable)
        {
            try
            {
                using var sc = new ServiceController(item.ServiceName);
                if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                }
            }
            catch { }

            key.SetValue("Start", 4, RegistryValueKind.DWord);
            item.IsActive = false;
            item.StatusText = "Disabled";
            return (true, $"Disabled service '{item.ServiceName}'.");
        }
        else
        {
            int startVal = item.RestoreServiceStart is 2 or 3 ? item.RestoreServiceStart : 3;
            key.SetValue("Start", startVal, RegistryValueKind.DWord);
            item.IsActive = true;
            item.StatusText = startVal == 2 ? "Stopped (Auto)" : "Stopped (Manual)";
            return (true, $"Restored service '{item.ServiceName}' startup type to {(startVal == 2 ? "Automatic" : "Manual")}.");
        }
    }

    private static (bool, string) SetTaskEnabled(TelemetryItem item, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(item.TaskFolder) || string.IsNullOrWhiteSpace(item.TaskName))
            return (false, "Missing task path/name.");

        object? schedulerObj = null;
        try
        {
            var t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) return (false, "Task Scheduler COM service unavailable.");
            schedulerObj = Activator.CreateInstance(t);
            if (schedulerObj == null) return (false, "Could not create Task Scheduler instance.");

            dynamic scheduler = schedulerObj;
            scheduler.Connect();
            dynamic folder = scheduler.GetFolder(item.TaskFolder);
            dynamic task = folder.GetTask(item.TaskName);
            task.Enabled = enabled;

            item.IsActive = enabled;
            item.StatusText = enabled ? "Enabled (Ready)" : "Disabled";
            return (true, $"{(enabled ? "Restored" : "Disabled")} task '{item.Name}'.");
        }
        finally
        {
            if (schedulerObj != null && Marshal.IsComObject(schedulerObj))
                Marshal.ReleaseComObject(schedulerObj);
        }
    }

    private static (bool, string) SetRegistryPolicy(TelemetryItem item, bool disable)
    {
        if (string.IsNullOrWhiteSpace(item.RegistrySubKey) || string.IsNullOrWhiteSpace(item.RegistryValueName))
            return (false, "Missing registry path.");

        using var baseKey = RegistryKey.OpenBaseKey(item.RegistryHive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(item.RegistrySubKey, writable: true);
        if (key == null)
            return (false, $"Could not open/create key {item.RegistrySubKey}");

        if (disable)
        {
            if (item.DisabledValue == null) return (false, "No disabled value configured.");
            key.SetValue(item.RegistryValueName, item.DisabledValue, item.RegistryValueKind);
            item.IsActive = false;
            item.StatusText = $"Hardened ({item.DisabledValue})";
            return (true, $"Hardened policy '{item.DisplayName}'.");
        }
        else
        {
            if (item.RestoredValue == null)
            {
                key.DeleteValue(item.RegistryValueName, throwOnMissingValue: false);
                item.StatusText = "Not Set (Default Active)";
            }
            else
            {
                key.SetValue(item.RegistryValueName, item.RestoredValue, item.RegistryValueKind);
                item.StatusText = $"Active ({item.RestoredValue})";
            }
            item.IsActive = true;
            return (true, $"Restored policy '{item.DisplayName}'.");
        }
    }

    private static (bool, string) SetStartupState(TelemetryItem item, bool disable)
    {
        if (string.IsNullOrWhiteSpace(item.StartupKeyPath) || string.IsNullOrWhiteSpace(item.StartupValueName))
            return (false, "Missing startup entry metadata.");

        string backupName = $"{item.StartupHive}_{item.StartupValueName}";

        if (disable)
        {
            using (var backup = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\xSecurityCenter\Backup\Startup", writable: true))
            {
                backup?.SetValue(backupName, $"{item.StartupHive}|{item.StartupKeyPath}|{item.StartupValueName}|{item.StartupCommand}", RegistryValueKind.String);
            }

            using var baseKey = RegistryKey.OpenBaseKey(item.StartupHive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(item.StartupKeyPath, writable: true);
            key?.DeleteValue(item.StartupValueName, throwOnMissingValue: false);

            item.IsActive = false;
            item.StatusText = "Disabled (Backed Up)";
            return (true, $"Disabled startup item '{item.StartupValueName}'.");
        }
        else
        {
            using var baseKey = RegistryKey.OpenBaseKey(item.StartupHive, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(item.StartupKeyPath, writable: true);
            key?.SetValue(item.StartupValueName, item.StartupCommand ?? string.Empty, RegistryValueKind.String);

            foreach (var sub in new[] { @"SOFTWARE\xSecurityCenter\Backup\Startup", @"SOFTWARE\xSystemForge\Backup\Startup", @"SOFTWARE\xTelemetry\Backup\Startup" })
            {
                using var backup = Registry.CurrentUser.OpenSubKey(sub, writable: true);
                backup?.DeleteValue(backupName, throwOnMissingValue: false);
            }

            item.IsActive = true;
            item.StatusText = "Active at Logon";
            return (true, $"Restored startup item '{item.StartupValueName}'.");
        }
    }

    public (bool Success, string Message) SetFirewallOutboundBlock(string exePath, string processName, bool block)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return (false, "Executable path unavailable for this process.");

        string ruleName = $"xSecurityCenter Block - {processName} ({exePath.GetHashCode():X8})";
        object? policyObj = null;
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType == null) return (false, "Windows Firewall COM API unavailable.");
            policyObj = Activator.CreateInstance(policyType);
            if (policyObj == null) return (false, "Could not instantiate Firewall Policy.");

            dynamic policy = policyObj;
            dynamic rules = policy.Rules;

            if (block)
            {
                try { rules.Remove(ruleName); } catch { }

                var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
                dynamic rule = Activator.CreateInstance(ruleType!)!;
                rule.Name = ruleName;
                rule.Description = $"Outbound block created by xSecurityCenter for {exePath}";
                rule.ApplicationName = exePath;
                rule.Direction = 2; // NET_FW_RULE_DIR_OUT
                rule.Action = 0;    // NET_FW_ACTION_BLOCK
                rule.Enabled = true;
                rule.Profiles = 0x7FFFFFFF;
                rules.Add(rule);
                return (true, $"Blocked outbound traffic for '{processName}' ({exePath}).");
            }
            else
            {
                var toRemove = new List<string>();
                foreach (dynamic r in rules)
                {
                    try
                    {
                        string rName = r.Name ?? "";
                        string appName = r.ApplicationName ?? "";
                        if ((rName.StartsWith("xSecurityCenter Block - ", StringComparison.OrdinalIgnoreCase) ||
                             rName.StartsWith("xSystemForge Block - ", StringComparison.OrdinalIgnoreCase) ||
                             rName.StartsWith("xTelemetry Block - ", StringComparison.OrdinalIgnoreCase)) &&
                            string.Equals(appName, exePath, StringComparison.OrdinalIgnoreCase))
                        {
                            toRemove.Add(rName);
                        }
                    }
                    catch { }
                }

                foreach (var n in toRemove)
                    try { rules.Remove(n); } catch { }

                return (true, $"Unblocked outbound traffic for '{processName}'.");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Firewall error for '{processName}': {ex.Message}");
        }
        finally
        {
            if (policyObj != null && Marshal.IsComObject(policyObj))
                Marshal.ReleaseComObject(policyObj);
        }
    }

    public HashSet<string> GetBlockedExecutablePaths()
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object? policyObj = null;
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType == null) return blocked;
            policyObj = Activator.CreateInstance(policyType);
            if (policyObj == null) return blocked;

            dynamic policy = policyObj;
            foreach (dynamic r in policy.Rules)
            {
                try
                {
                    string name = r.Name ?? "";
                    if ((name.StartsWith("xSecurityCenter Block - ", StringComparison.OrdinalIgnoreCase) ||
                         name.StartsWith("xSystemForge Block - ", StringComparison.OrdinalIgnoreCase) ||
                         name.StartsWith("xTelemetry Block - ", StringComparison.OrdinalIgnoreCase)) &&
                        r.Enabled == true)
                    {
                        string app = r.ApplicationName ?? "";
                        if (!string.IsNullOrWhiteSpace(app))
                            blocked.Add(app);
                    }
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            if (policyObj != null && Marshal.IsComObject(policyObj))
                Marshal.ReleaseComObject(policyObj);
        }
        return blocked;
    }
}
