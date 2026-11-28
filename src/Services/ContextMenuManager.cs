using Microsoft.Win32;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed class ContextMenuManager
{
    private const string DisabledPrefix = "xContextGuard.Disabled:";
    private readonly RegistryBackupService _backups = new();

    public string BackupDirectory => _backups.BackupDirectory;
    public string LegacyBackupDirectory => _backups.LegacyBackupDirectory;

    public string SetEnabled(MenuEntry entry, bool enabled)
    {
        if (entry.Kind == EntryKind.WinXShortcut)
            return WinXService.SetEnabled(entry, enabled, _backups);

        if (entry.IsStartup)
            return StartupService.SetEnabled(entry, enabled, _backups);

        if (entry.Kind == EntryKind.InstalledApp)
            return InstalledAppService.SetEnabled(entry, enabled, _backups);

        var backup = _backups.BackupKey(entry.RegistryPath, enabled ? "before-enable" : "before-disable");
        var (hive, subPath) = RegistryBackupService.ParsePath(entry.RegistryPath);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(subPath, writable: true)
            ?? throw new InvalidOperationException("This registry entry no longer exists.");

        if (entry.Kind == EntryKind.StaticVerb)
        {
            if (enabled)
                key.DeleteValue("LegacyDisable", throwOnMissingValue: false);
            else
                key.SetValue("LegacyDisable", "", RegistryValueKind.String);
        }
        else
        {
            var current = key.GetValue(null)?.ToString() ?? "";
            if (enabled && current.StartsWith(DisabledPrefix, StringComparison.Ordinal))
                key.SetValue(null, current[DisabledPrefix.Length..], RegistryValueKind.String);
            else if (!enabled && !current.StartsWith(DisabledPrefix, StringComparison.Ordinal))
                key.SetValue(null, DisabledPrefix + current, RegistryValueKind.String);
        }

        entry.IsDisabled = !enabled;
        return backup;
    }

    public string Delete(MenuEntry entry)
    {
        if (entry.Kind == EntryKind.WinXShortcut)
            return WinXService.Delete(entry, _backups);

        if (entry.IsStartup)
            return StartupService.Delete(entry, _backups);

        var backup = _backups.BackupKey(entry.RegistryPath, "before-delete");
        var (hive, subPath) = RegistryBackupService.ParsePath(entry.RegistryPath);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        var lastSlash = subPath.LastIndexOf('\\');
        if (lastSlash < 0) throw new InvalidOperationException("Refusing to delete a registry hive root.");
        using var parent = root.OpenSubKey(subPath[..lastSlash], writable: true)
            ?? throw new InvalidOperationException("The parent registry key no longer exists.");
        parent.DeleteSubKeyTree(subPath[(lastSlash + 1)..], throwOnMissingSubKey: true);
        return backup;
    }

    public string SaveWinXShortcut(
        string? originalPath,
        string displayName,
        string groupName,
        string targetPath,
        string arguments,
        bool runAsAdmin)
        => WinXService.SaveShortcut(originalPath, displayName, groupName, targetPath, arguments, runAsAdmin, _backups);

    public string SaveStartupEntry(
        MenuEntry entry,
        string newName,
        string newTargetOrCommand,
        string newArguments,
        bool runAsAdmin)
        => StartupService.SaveStartupEntry(entry, newName, newTargetOrCommand, newArguments, runAsAdmin, _backups);

    public string SaveInstalledApp(MenuEntry entry, string displayName, string uninstallString)
        => InstalledAppService.SaveInstalledApp(entry, displayName, uninstallString, _backups);

    public string SaveStaticVerb(MenuEntry entry, string label, string command)
    {
        var backup = _backups.BackupKey(entry.RegistryPath, "before-edit");
        var (hive, subPath) = RegistryBackupService.ParsePath(entry.RegistryPath);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(subPath, writable: true)
            ?? throw new InvalidOperationException("This registry entry no longer exists.");

        key.SetValue(null, label, RegistryValueKind.String);
        using var commandKey = key.CreateSubKey("command", writable: true);
        commandKey.SetValue(null, command, RegistryValueKind.String);

        entry.Name = label;
        entry.Command = command;
        return backup;
    }

    public void Restore(string backupPath) => _backups.RestoreBackup(backupPath);

    public string CreateBackup(MenuEntry entry)
    {
        if (entry.Kind is EntryKind.WinXShortcut or EntryKind.StartupFolder)
            return _backups.BackupFile(entry.RegistryPath, "manual-backup", entry.StartupApprovedPath);
        if (entry.Kind == EntryKind.StartupRegistry)
            return _backups.BackupKey(entry.ParentKeyPath, "manual-backup", entry.StartupApprovedPath);
        return _backups.BackupKey(entry.RegistryPath, "manual-backup");
    }

    public void RestartExplorer() => WinXService.RestartExplorer();
}
