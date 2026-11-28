using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public static class StartupService
{
    private const uint SLDF_RUNAS_USER = 0x00002000;
    private const string DisabledSubKeyName = "AutorunsDisabled";

    private sealed record StartupRegistryTarget(
        RegistryHive Hive,
        string HivePrefix,
        string SubKeyPath,
        string? ApprovedSubKeyPath,
        string LocationLabel);

    private static readonly StartupRegistryTarget[] RegistryTargets =
    [
        new(
            RegistryHive.CurrentUser,
            "HKEY_CURRENT_USER",
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            "Startup (HKCU\\Run)"),
        new(
            RegistryHive.CurrentUser,
            "HKEY_CURRENT_USER",
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
            null,
            "Startup (HKCU\\RunOnce)"),
        new(
            RegistryHive.LocalMachine,
            "HKEY_LOCAL_MACHINE",
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            "Startup (HKLM\\Run)"),
        new(
            RegistryHive.LocalMachine,
            "HKEY_LOCAL_MACHINE",
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
            null,
            "Startup (HKLM\\RunOnce)"),
        new(
            RegistryHive.LocalMachine,
            "HKEY_LOCAL_MACHINE",
            @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
            "Startup (HKLM\\Run 32-bit)"),
        new(
            RegistryHive.LocalMachine,
            "HKEY_LOCAL_MACHINE",
            @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",
            null,
            "Startup (HKLM\\RunOnce 32-bit)")
    ];

    public static List<MenuEntry> Scan()
    {
        var entries = new List<MenuEntry>();

        foreach (var target in RegistryTargets)
            ScanRegistryTarget(target, entries);

        var userStartupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        ScanStartupFolder(
            userStartupDir,
            RegistryHive.CurrentUser,
            "HKEY_CURRENT_USER",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
            "Startup Folder (Current User)",
            entries);

        var commonStartupDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        ScanStartupFolder(
            commonStartupDir,
            RegistryHive.LocalMachine,
            "HKEY_LOCAL_MACHINE",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
            "Startup Folder (All Users)",
            entries);

        return entries;
    }

    private static void ScanRegistryTarget(StartupRegistryTarget target, List<MenuEntry> entries)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(target.Hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(target.SubKeyPath, writable: false);
            using var approvedKey = target.ApprovedSubKeyPath is not null
                ? root.OpenSubKey(target.ApprovedSubKeyPath, writable: false)
                : null;

            var parentFullPath = $"{target.HivePrefix}\\{target.SubKeyPath}";
            var approvedFullPath = target.ApprovedSubKeyPath is not null
                ? $"{target.HivePrefix}\\{target.ApprovedSubKeyPath}"
                : "";

            if (key is not null)
            {
                foreach (var valueName in key.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(valueName)) continue;

                    var rawCommand = key.GetValue(valueName)?.ToString() ?? "";
                    bool isDisabled = IsStartupApprovedDisabled(approvedKey, valueName);

                    entries.Add(new MenuEntry
                    {
                        Name = valueName,
                        Location = target.LocationLabel,
                        RegistryPath = $"{parentFullPath}\\{valueName}",
                        ParentKeyPath = parentFullPath,
                        ValueName = valueName,
                        StartupApprovedPath = approvedFullPath,
                        Kind = EntryKind.StartupRegistry,
                        Command = rawCommand,
                        IsDisabled = isDisabled
                    });
                }

                using var disabledSubKey = key.OpenSubKey(DisabledSubKeyName, writable: false);
                if (disabledSubKey is not null)
                {
                    foreach (var valueName in disabledSubKey.GetValueNames())
                    {
                        if (string.IsNullOrWhiteSpace(valueName)) continue;
                        if (entries.Any(e => e.Kind == EntryKind.StartupRegistry &&
                                             string.Equals(e.ParentKeyPath, parentFullPath, StringComparison.OrdinalIgnoreCase) &&
                                             string.Equals(e.ValueName, valueName, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var rawCommand = disabledSubKey.GetValue(valueName)?.ToString() ?? "";
                        entries.Add(new MenuEntry
                        {
                            Name = valueName,
                            Location = target.LocationLabel,
                            RegistryPath = $"{parentFullPath}\\{valueName}",
                            ParentKeyPath = parentFullPath,
                            ValueName = valueName,
                            StartupApprovedPath = approvedFullPath,
                            Kind = EntryKind.StartupRegistry,
                            Command = rawCommand,
                            IsDisabled = true
                        });
                    }
                }
            }
        }
        catch { }
    }

    private static void ScanStartupFolder(
        string folderPath,
        RegistryHive approvedHive,
        string approvedHivePrefix,
        string approvedSubKeyPath,
        string locationLabel,
        List<MenuEntry> entries)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return;

            using var root = RegistryKey.OpenBaseKey(approvedHive, RegistryView.Registry64);
            using var approvedKey = root.OpenSubKey(approvedSubKeyPath, writable: false);
            var approvedFullPath = $"{approvedHivePrefix}\\{approvedSubKeyPath}";

            foreach (var file in Directory.GetFiles(folderPath))
            {
                var fileName = Path.GetFileName(file);
                if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool hasDisabledExt = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var baseFileName = hasDisabledExt ? fileName[..^".disabled".Length] : fileName;
                bool isApprovedDisabled = IsStartupApprovedDisabled(approvedKey, baseFileName) ||
                                          IsStartupApprovedDisabled(approvedKey, fileName);

                var displayName = Path.GetFileNameWithoutExtension(baseFileName);
                string commandDisplay = file;
                string arguments = "";
                bool runAsAdmin = false;

                if (baseFileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var (target, args, admin) = ReadShortcut(file);
                        if (!string.IsNullOrWhiteSpace(target))
                        {
                            commandDisplay = string.IsNullOrWhiteSpace(args) ? target : $"\"{target}\" {args}";
                            arguments = args;
                            runAsAdmin = admin;
                        }
                    }
                    catch { }
                }

                entries.Add(new MenuEntry
                {
                    Name = displayName,
                    Location = locationLabel,
                    RegistryPath = file,
                    ParentKeyPath = folderPath,
                    ValueName = baseFileName,
                    StartupApprovedPath = approvedFullPath,
                    Kind = EntryKind.StartupFolder,
                    Command = commandDisplay,
                    Arguments = arguments,
                    RunAsAdmin = runAsAdmin,
                    IsDisabled = hasDisabledExt || isApprovedDisabled
                });
            }
        }
        catch { }
    }

    private static bool IsStartupApprovedDisabled(RegistryKey? approvedKey, string valueName)
    {
        if (approvedKey is null || string.IsNullOrEmpty(valueName))
            return false;

        try
        {
            if (approvedKey.GetValue(valueName) is byte[] data && data.Length >= 4)
                return (data[0] & 1) != 0;
        }
        catch { }

        return false;
    }

    private static byte[] CreateStartupApprovedBytes(bool enabled)
    {
        var bytes = new byte[12];
        if (enabled)
        {
            bytes[0] = 0x02;
        }
        else
        {
            bytes[0] = 0x03;
            var ftBytes = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
            Buffer.BlockCopy(ftBytes, 0, bytes, 4, 8);
        }
        return bytes;
    }

    public static string SetEnabled(MenuEntry entry, bool enabled, RegistryBackupService backups)
    {
        if (entry.Kind == EntryKind.StartupRegistry)
        {
            var backup = backups.BackupKey(entry.ParentKeyPath, enabled ? "startup-before-enable" : "startup-before-disable", entry.StartupApprovedPath);
            var (hive, subPath) = RegistryBackupService.ParsePath(entry.ParentKeyPath);
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var runKey = root.OpenSubKey(subPath, writable: true)
                ?? throw new InvalidOperationException($"Registry key no longer exists: {entry.ParentKeyPath}");

            using (var disabledSub = runKey.OpenSubKey(DisabledSubKeyName, writable: true))
            {
                if (enabled && disabledSub is not null && disabledSub.GetValue(entry.ValueName) is object val)
                {
                    var kind = disabledSub.GetValueKind(entry.ValueName);
                    runKey.SetValue(entry.ValueName, val, kind);
                    disabledSub.DeleteValue(entry.ValueName, throwOnMissingValue: false);
                }
                else if (!enabled && string.IsNullOrEmpty(entry.StartupApprovedPath))
                {
                    var activeVal = runKey.GetValue(entry.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                        ?? throw new InvalidOperationException($"Registry value '{entry.ValueName}' no longer exists.");
                    var kind = runKey.GetValueKind(entry.ValueName);
                    using var createdSub = runKey.CreateSubKey(DisabledSubKeyName, writable: true)!;
                    createdSub.SetValue(entry.ValueName, activeVal, kind);
                    runKey.DeleteValue(entry.ValueName, throwOnMissingValue: false);
                }
            }

            if (!string.IsNullOrEmpty(entry.StartupApprovedPath))
            {
                var (appHive, appSubPath) = RegistryBackupService.ParsePath(entry.StartupApprovedPath);
                using var appRoot = RegistryKey.OpenBaseKey(appHive, RegistryView.Registry64);
                using var appKey = appRoot.CreateSubKey(appSubPath, writable: true)
                    ?? throw new InvalidOperationException($"Could not open StartupApproved key: {entry.StartupApprovedPath}");

                appKey.SetValue(entry.ValueName, CreateStartupApprovedBytes(enabled), RegistryValueKind.Binary);
            }

            entry.IsDisabled = !enabled;
            return backup;
        }

        if (entry.Kind == EntryKind.StartupFolder)
        {
            var filePath = entry.RegistryPath;
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Startup file no longer exists: {filePath}");

            var backup = backups.BackupFile(filePath, enabled ? "startup-before-enable" : "startup-before-disable", entry.StartupApprovedPath);

            if (enabled && filePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            {
                var restoredPath = filePath[..^".disabled".Length];
                File.Move(filePath, restoredPath, overwrite: true);
                entry.RegistryPath = restoredPath;
                filePath = restoredPath;
            }

            if (!string.IsNullOrEmpty(entry.StartupApprovedPath))
            {
                var (appHive, appSubPath) = RegistryBackupService.ParsePath(entry.StartupApprovedPath);
                using var appRoot = RegistryKey.OpenBaseKey(appHive, RegistryView.Registry64);
                using var appKey = appRoot.CreateSubKey(appSubPath, writable: true)
                    ?? throw new InvalidOperationException($"Could not open StartupApproved key: {entry.StartupApprovedPath}");

                var valueName = Path.GetFileName(filePath);
                appKey.SetValue(valueName, CreateStartupApprovedBytes(enabled), RegistryValueKind.Binary);
            }

            entry.IsDisabled = !enabled;
            return backup;
        }

        throw new InvalidOperationException($"Unsupported startup entry kind: {entry.Kind}");
    }

    public static string Delete(MenuEntry entry, RegistryBackupService backups)
    {
        if (entry.Kind == EntryKind.StartupRegistry)
        {
            var backup = backups.BackupKey(entry.ParentKeyPath, "startup-before-delete", entry.StartupApprovedPath);
            var (hive, subPath) = RegistryBackupService.ParsePath(entry.ParentKeyPath);
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var runKey = root.OpenSubKey(subPath, writable: true)
                ?? throw new InvalidOperationException($"Registry key no longer exists: {entry.ParentKeyPath}");

            runKey.DeleteValue(entry.ValueName, throwOnMissingValue: false);
            using (var disabledSub = runKey.OpenSubKey(DisabledSubKeyName, writable: true))
                disabledSub?.DeleteValue(entry.ValueName, throwOnMissingValue: false);

            if (!string.IsNullOrEmpty(entry.StartupApprovedPath))
            {
                try
                {
                    var (appHive, appSubPath) = RegistryBackupService.ParsePath(entry.StartupApprovedPath);
                    using var appRoot = RegistryKey.OpenBaseKey(appHive, RegistryView.Registry64);
                    using var appKey = appRoot.OpenSubKey(appSubPath, writable: true);
                    appKey?.DeleteValue(entry.ValueName, throwOnMissingValue: false);
                }
                catch { }
            }

            return backup;
        }

        if (entry.Kind == EntryKind.StartupFolder)
        {
            var filePath = entry.RegistryPath;
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Startup file no longer exists: {filePath}");

            var backup = backups.BackupFile(filePath, "startup-before-delete", entry.StartupApprovedPath);
            File.Delete(filePath);

            if (!string.IsNullOrEmpty(entry.StartupApprovedPath))
            {
                try
                {
                    var (appHive, appSubPath) = RegistryBackupService.ParsePath(entry.StartupApprovedPath);
                    using var appRoot = RegistryKey.OpenBaseKey(appHive, RegistryView.Registry64);
                    using var appKey = appRoot.OpenSubKey(appSubPath, writable: true);
                    appKey?.DeleteValue(entry.ValueName, throwOnMissingValue: false);
                    appKey?.DeleteValue(Path.GetFileName(filePath), throwOnMissingValue: false);
                }
                catch { }
            }

            return backup;
        }

        throw new InvalidOperationException($"Unsupported startup entry kind: {entry.Kind}");
    }

    public static string SaveStartupEntry(
        MenuEntry entry,
        string newName,
        string newTargetOrCommand,
        string newArguments,
        bool runAsAdmin,
        RegistryBackupService backups)
    {
        if (entry.Kind == EntryKind.StartupRegistry)
        {
            var backup = backups.BackupKey(entry.ParentKeyPath, "startup-before-edit");
            var (hive, subPath) = RegistryBackupService.ParsePath(entry.ParentKeyPath);
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var runKey = root.OpenSubKey(subPath, writable: true)
                ?? throw new InvalidOperationException($"Registry key no longer exists: {entry.ParentKeyPath}");

            if (!string.Equals(entry.ValueName, newName, StringComparison.OrdinalIgnoreCase))
                runKey.DeleteValue(entry.ValueName, throwOnMissingValue: false);

            runKey.SetValue(newName, newTargetOrCommand, RegistryValueKind.String);
            entry.Name = newName;
            entry.ValueName = newName;
            entry.Command = newTargetOrCommand;
            entry.RegistryPath = $"{entry.ParentKeyPath}\\{newName}";
            return backup;
        }

        if (entry.Kind == EntryKind.StartupFolder)
        {
            var filePath = entry.RegistryPath;
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Startup file no longer exists: {filePath}");

            var backup = backups.BackupFile(filePath, "startup-before-edit");
            var folder = Path.GetDirectoryName(filePath) ?? entry.ParentKeyPath;
            bool wasDisabledExt = filePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);

            var cleanName = string.Concat(newName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            if (string.IsNullOrEmpty(cleanName)) cleanName = "StartupItem";

            var destFileName = cleanName + ".lnk" + (wasDisabledExt ? ".disabled" : "");
            var destPath = Path.Combine(folder, destFileName);

            var link = (IShellLinkW)new CShellLink();
            var persist = (IPersistFile)link;
            if (filePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || wasDisabledExt)
            {
                try { persist.Load(filePath, 0); } catch { }
            }

            link.SetPath(newTargetOrCommand);
            link.SetArguments(newArguments ?? "");

            var targetDir = Path.GetDirectoryName(newTargetOrCommand);
            if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
                link.SetWorkingDirectory(targetDir);

            var dataList = (IShellLinkDataList)link;
            dataList.GetFlags(out uint flags);
            if (runAsAdmin) flags |= SLDF_RUNAS_USER;
            else flags &= ~SLDF_RUNAS_USER;
            dataList.SetFlags(flags);

            persist.Save(destPath, true);

            if (!string.Equals(filePath, destPath, StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
                File.Delete(filePath);

            entry.Name = cleanName;
            entry.RegistryPath = destPath;
            entry.ValueName = cleanName + ".lnk";
            entry.Command = string.IsNullOrWhiteSpace(newArguments) ? newTargetOrCommand : $"\"{newTargetOrCommand}\" {newArguments}";
            entry.Arguments = newArguments ?? "";
            entry.RunAsAdmin = runAsAdmin;
            return backup;
        }

        throw new InvalidOperationException($"Unsupported startup entry kind: {entry.Kind}");
    }

    public static (string target, string args, bool runAsAdmin) ReadShortcut(string filePath)
    {
        var link = (IShellLinkW)new CShellLink();
        var persist = (IPersistFile)link;
        persist.Load(filePath, 0);

        var targetSb = new StringBuilder(260);
        link.GetPath(targetSb, targetSb.Capacity, IntPtr.Zero, 0);
        var argsSb = new StringBuilder(1024);
        link.GetArguments(argsSb, argsSb.Capacity);

        var dataList = (IShellLinkDataList)link;
        dataList.GetFlags(out uint flags);
        bool runAsAdmin = (flags & SLDF_RUNAS_USER) != 0;

        return (targetSb.ToString(), argsSb.ToString(), runAsAdmin);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("45E2B4AE-B1C3-11D0-B92F-00A0C90312E1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkDataList
    {
        void AddDataBlock(IntPtr pDataBlock);
        void CopyDataBlock(uint dwSig, out IntPtr ppDataBlock);
        void RemoveDataBlock(uint dwSig);
        void GetFlags(out uint pdwFlags);
        void SetFlags(uint dwFlags);
    }
}
