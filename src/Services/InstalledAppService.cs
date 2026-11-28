using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public static partial class InstalledAppService
{
    private const string HiddenMarkerValue = "xContextGuard.Hidden";

    private sealed record UninstallRegistryTarget(
        RegistryHive Hive,
        string HivePrefix,
        string SubKeyPath,
        string LocationLabel);

    private static readonly UninstallRegistryTarget[] Targets =
    [
        new(
            RegistryHive.LocalMachine,
            "HKEY_LOCAL_MACHINE",
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
            "Installed App (HKLM 64-bit)"),
        new(
            RegistryHive.LocalMachine,
            "HKEY_LOCAL_MACHINE",
            @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            "Installed App (HKLM 32-bit)"),
        new(
            RegistryHive.CurrentUser,
            "HKEY_CURRENT_USER",
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
            "Installed App (Current User)")
    ];

    private static readonly HashSet<string> GenericSystemExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "msiexec.exe",
        "msiexec",
        "rundll32.exe",
        "rundll32",
        "cmd.exe",
        "powershell.exe",
        "regsvr32.exe",
        "setup.exe"
    };

    public static List<MenuEntry> Scan()
    {
        var entries = new List<MenuEntry>();

        foreach (var target in Targets)
            ScanTarget(target, entries);

        return entries
            .OrderByDescending(e => e.IsMissingFiles)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanTarget(UninstallRegistryTarget target, List<MenuEntry> entries)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(target.Hive, RegistryView.Registry64);
            using var uninstallRoot = root.OpenSubKey(target.SubKeyPath, writable: false);
            if (uninstallRoot is null) return;

            foreach (var subKeyName in uninstallRoot.GetSubKeyNames())
            {
                try
                {
                    using var appKey = uninstallRoot.OpenSubKey(subKeyName, writable: false);
                    if (appKey is null) continue;

                    var displayName = appKey.GetValue("DisplayName")?.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(displayName))
                        continue;

                    var parentKeyName = appKey.GetValue("ParentKeyName")?.ToString();
                    if (!string.IsNullOrWhiteSpace(parentKeyName))
                        continue;

                    bool hiddenByUs = appKey.GetValue(HiddenMarkerValue) is not null;
                    bool isSystemComponent = appKey.GetValue("SystemComponent") is int sysComp && sysComp == 1;
                    if (isSystemComponent && !hiddenByUs)
                        continue;

                    var displayVersion = appKey.GetValue("DisplayVersion")?.ToString()?.Trim() ?? "";
                    var installLocation = appKey.GetValue("InstallLocation")?.ToString()?.Trim() ?? "";
                    var uninstallString = appKey.GetValue("UninstallString")?.ToString()?.Trim()
                        ?? appKey.GetValue("QuietUninstallString")?.ToString()?.Trim()
                        ?? "";
                    var displayIcon = appKey.GetValue("DisplayIcon")?.ToString()?.Trim() ?? "";

                    bool missingFiles = DetectMissingFiles(installLocation, uninstallString, displayIcon, out string missingDetail);

                    var fullName = !string.IsNullOrWhiteSpace(displayVersion) &&
                                   !displayName.Contains(displayVersion, StringComparison.OrdinalIgnoreCase)
                        ? $"{displayName} ({displayVersion})"
                        : displayName;

                    var commandSummary = !string.IsNullOrWhiteSpace(installLocation)
                        ? installLocation
                        : (!string.IsNullOrWhiteSpace(uninstallString) ? uninstallString : subKeyName);

                    if (missingFiles && !string.IsNullOrWhiteSpace(missingDetail))
                        commandSummary = $"[Missing: {missingDetail}] {commandSummary}";

                    var fullKeyPath = $"{target.HivePrefix}\\{target.SubKeyPath}\\{subKeyName}";

                    entries.Add(new MenuEntry
                    {
                        Name = fullName,
                        Location = target.LocationLabel,
                        RegistryPath = fullKeyPath,
                        ParentKeyPath = $"{target.HivePrefix}\\{target.SubKeyPath}",
                        ValueName = displayName,
                        Kind = EntryKind.InstalledApp,
                        Command = commandSummary,
                        Arguments = uninstallString,
                        IsMissingFiles = missingFiles,
                        IsHiddenApp = hiddenByUs,
                        IsDisabled = missingFiles || hiddenByUs
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool DetectMissingFiles(
        string installLocation,
        string uninstallString,
        string displayIcon,
        out string missingDetail)
    {
        missingDetail = "";

        var uninstallerExe = ExtractExecutablePath(uninstallString);
        bool hasCustomUninstaller = !string.IsNullOrEmpty(uninstallerExe) &&
            !GenericSystemExecutables.Contains(Path.GetFileName(uninstallerExe));

        if (hasCustomUninstaller && File.Exists(uninstallerExe))
            return false;

        var cleanInstallPath = NormalizePath(installLocation);
        if (!string.IsNullOrEmpty(cleanInstallPath))
        {
            bool exists = Directory.Exists(cleanInstallPath) || File.Exists(cleanInstallPath);
            if (!exists && Path.HasExtension(cleanInstallPath))
            {
                try
                {
                    var parentDir = Path.GetDirectoryName(cleanInstallPath);
                    if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                        exists = true;
                }
                catch { }
            }

            if (!exists)
            {
                missingDetail = cleanInstallPath;
                return true;
            }
        }

        if (hasCustomUninstaller && !File.Exists(uninstallerExe))
        {
            missingDetail = uninstallerExe;
            return true;
        }

        if (string.IsNullOrEmpty(cleanInstallPath) && !string.IsNullOrWhiteSpace(displayIcon))
        {
            var iconPath = ExtractIconFilePath(displayIcon);
            if (!string.IsNullOrEmpty(iconPath))
            {
                var fileName = Path.GetFileName(iconPath);
                if (!GenericSystemExecutables.Contains(fileName) && !File.Exists(iconPath))
                {
                    missingDetail = iconPath;
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return "";
        var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"').Trim());
        if (expanded.Length >= 3 && char.IsLetter(expanded[0]) && expanded[1] == ':' && (expanded[2] == '\\' || expanded[2] == '/'))
            return expanded;
        return "";
    }

    private static string ExtractExecutablePath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "";
        var expanded = Environment.ExpandEnvironmentVariables(commandLine.Trim());

        if (expanded.StartsWith('"'))
        {
            var endQuote = expanded.IndexOf('"', 1);
            if (endQuote > 1)
            {
                var candidate = expanded[1..endQuote].Trim();
                if (IsLocalFilePath(candidate))
                    return candidate;
            }
        }

        var match = ExePathRegex().Match(expanded);
        if (match.Success)
        {
            var candidate = match.Groups[1].Value.Trim();
            if (IsLocalFilePath(candidate))
                return candidate;
        }

        return "";
    }

    private static string ExtractIconFilePath(string displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return "";
        var expanded = Environment.ExpandEnvironmentVariables(displayIcon.Trim().Trim('"'));

        var lastComma = expanded.LastIndexOf(',');
        if (lastComma > 2)
        {
            var suffix = expanded[(lastComma + 1)..].Trim();
            if (int.TryParse(suffix, out _))
                expanded = expanded[..lastComma].Trim().Trim('"');
        }

        return IsLocalFilePath(expanded) ? expanded : "";
    }

    private static bool IsLocalFilePath(string path) =>
        path.Length >= 3 &&
        char.IsLetter(path[0]) &&
        path[1] == ':' &&
        (path[2] == '\\' || path[2] == '/');

    [GeneratedRegex(@"^([A-Za-z]:\\[^""]+?\.(?:exe|bat|cmd|msc))\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExePathRegex();

    public static string SetEnabled(MenuEntry entry, bool enabled, RegistryBackupService backups)
    {
        var backup = backups.BackupKey(entry.RegistryPath, enabled ? "app-before-enable" : "app-before-disable");
        var (hive, subPath) = RegistryBackupService.ParsePath(entry.RegistryPath);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(subPath, writable: true)
            ?? throw new InvalidOperationException("This installed application registry entry no longer exists.");

        if (!enabled)
        {
            key.SetValue("SystemComponent", 1, RegistryValueKind.DWord);
            key.SetValue(HiddenMarkerValue, 1, RegistryValueKind.DWord);
            entry.IsHiddenApp = true;
            entry.IsDisabled = true;
        }
        else
        {
            key.DeleteValue("SystemComponent", throwOnMissingValue: false);
            key.DeleteValue(HiddenMarkerValue, throwOnMissingValue: false);
            entry.IsHiddenApp = false;
            entry.IsDisabled = entry.IsMissingFiles;
        }

        return backup;
    }

    public static string SaveInstalledApp(MenuEntry entry, string displayName, string uninstallString, RegistryBackupService backups)
    {
        var backup = backups.BackupKey(entry.RegistryPath, "app-before-edit");
        var (hive, subPath) = RegistryBackupService.ParsePath(entry.RegistryPath);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(subPath, writable: true)
            ?? throw new InvalidOperationException("This installed application registry entry no longer exists.");

        key.SetValue("DisplayName", displayName, RegistryValueKind.String);
        if (!string.IsNullOrWhiteSpace(uninstallString))
            key.SetValue("UninstallString", uninstallString, RegistryValueKind.String);

        entry.Name = displayName;
        entry.ValueName = displayName;
        entry.Arguments = uninstallString;
        return backup;
    }
}
