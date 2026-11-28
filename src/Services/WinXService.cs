using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed partial class WinXService
{
    private const uint SLDF_RUNAS_USER = 0x00002000;
    private const string Salt = "do not prehash links.  this should only be done by the user.";

    private static readonly PROPERTYKEY PkeyWinXHash = new(new Guid("FB8D2D7B-90D1-4E34-BF60-6EAC09922BBF"), 2);

    public static string WinXUserBasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Windows\WinX");

    public static string WinXDefaultBasePath => Path.Combine(
        Environment.GetEnvironmentVariable("SystemDrive") ?? "C:",
        @"Users\Default\AppData\Local\Microsoft\Windows\WinX");

    private static string? GetBuiltInTitle(uint hash, bool runAsAdmin) => hash switch
    {
        0x6861712C => "Computer Management",
        0xEEF7BF44 => "Disk Management",
        0x631C4996 => "Network Connections",
        0xBDD12552 => "Device Manager",
        0x00A4D2C0 => "System",
        0xD0AD8FC0 => "Event Viewer",
        0x90A64483 => "Power Options",
        0xA2F62CDE => "Mobility Center",
        0xA2EDB372 => "Installed apps",
        0x1DA07E32 => "Run",
        0x8E5B9DE2 => "Search",
        0xF24FFAFC => "File Explorer",
        0x3C9C1396 => "Settings",
        0x9632551C => "Task Manager",
        0x36B68C0A => "Desktop",
        0x1A60D163 => runAsAdmin ? "Terminal (Admin) / PowerShell (Admin)" : "Terminal / Windows PowerShell",
        _ => null
    };

    private sealed class ScannedWinXItem
    {
        public string BaseLnkName { get; set; } = "";
        public string GroupName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string PrimaryPath { get; set; } = "";
        public string? UserPath { get; set; }
        public string? DefaultPath { get; set; }
        public string Command { get; set; } = "";
        public string Arguments { get; set; } = "";
        public bool RunAsAdmin { get; set; }
        public uint WinXHash { get; set; }
    }

    public static List<MenuEntry> Scan()
    {
        var dict = new Dictionary<string, ScannedWinXItem>(StringComparer.OrdinalIgnoreCase);

        ScanDirectory(WinXDefaultBasePath, isDefault: true, dict);
        ScanDirectory(WinXUserBasePath, isDefault: false, dict);

        var entries = new List<MenuEntry>();
        foreach (var item in dict.Values.OrderBy(i => i.GroupName, StringComparer.OrdinalIgnoreCase)
                                        .ThenBy(i => Path.GetFileName(i.PrimaryPath), StringComparer.OrdinalIgnoreCase))
        {
            bool hasActiveUser = !string.IsNullOrEmpty(item.UserPath) && item.UserPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
            bool hasActiveDefault = !string.IsNullOrEmpty(item.DefaultPath) && item.DefaultPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
            bool isDisabled = !hasActiveUser && !hasActiveDefault;

            var commandDisplay = item.Command;
            if (string.IsNullOrWhiteSpace(commandDisplay) && !string.IsNullOrWhiteSpace(item.Arguments))
                commandDisplay = $"ms-settings ({item.Arguments})";
            else if (string.IsNullOrWhiteSpace(commandDisplay))
                commandDisplay = item.DisplayName;

            entries.Add(new MenuEntry
            {
                Name = item.DisplayName,
                Location = GetLocationLabel(item.GroupName),
                RegistryPath = item.PrimaryPath,
                Kind = EntryKind.WinXShortcut,
                Command = commandDisplay,
                Arguments = item.Arguments,
                Group = item.GroupName,
                RunAsAdmin = item.RunAsAdmin,
                IsDisabled = isDisabled
            });
        }

        return entries;
    }

    private static void ScanDirectory(string basePath, bool isDefault, Dictionary<string, ScannedWinXItem> dict)
    {
        if (!Directory.Exists(basePath)) return;

        foreach (var groupDir in Directory.GetDirectories(basePath, "Group*"))
        {
            var groupName = Path.GetFileName(groupDir);
            var files = Directory.GetFiles(groupDir)
                .Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                try
                {
                    var fileName = Path.GetFileName(file);
                    bool isDisabled = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    var baseLnkName = isDisabled ? fileName[..^".disabled".Length] : fileName;

                    if (!dict.TryGetValue(baseLnkName, out var item))
                    {
                        item = new ScannedWinXItem
                        {
                            BaseLnkName = baseLnkName,
                            GroupName = groupName,
                            PrimaryPath = file
                        };
                        dict[baseLnkName] = item;
                    }

                    var (target, args, description, runAsAdmin, hash) = ReadShortcutInfo(file);

                    if (isDefault)
                    {
                        item.DefaultPath = file;
                        if (string.IsNullOrEmpty(item.DisplayName))
                            item.DisplayName = ResolveDisplayName(file, description, hash, runAsAdmin);
                    }
                    else
                    {
                        item.UserPath = file;
                        item.PrimaryPath = file;
                        item.GroupName = groupName;
                        item.DisplayName = ResolveDisplayName(file, description, hash, runAsAdmin);
                    }

                    if (!string.IsNullOrEmpty(target) || string.IsNullOrEmpty(item.Command))
                    {
                        item.Command = target;
                        item.Arguments = args;
                        item.RunAsAdmin = runAsAdmin;
                        item.WinXHash = hash;
                    }
                }
                catch { }
            }
        }
    }

    public static string GetLocationLabel(string groupName) => groupName.ToUpperInvariant() switch
    {
        "GROUP1" => "Win+X (Group 1 - Bottom)",
        "GROUP2" => "Win+X (Group 2 - Middle)",
        "GROUP3" => "Win+X (Group 3 - Top)",
        _ => $"Win+X ({groupName})"
    };

    public static string SetEnabled(MenuEntry entry, bool enabled, RegistryBackupService backups)
    {
        var (groupName, baseLnkName) = ParseGroupAndBaseName(entry.RegistryPath);
        string lastBackup = "";

        foreach (var root in new[] { WinXUserBasePath, WinXDefaultBasePath })
        {
            if (!Directory.Exists(root)) continue;
            bool isUserRoot = string.Equals(root, WinXUserBasePath, StringComparison.OrdinalIgnoreCase);

            foreach (var gDir in Directory.GetDirectories(root, "Group*"))
            {
                var lnkPath = Path.Combine(gDir, baseLnkName);
                var disabledPath = lnkPath + ".disabled";

                if (!enabled)
                {
                    if (File.Exists(lnkPath))
                    {
                        try
                        {
                            var b = backups.BackupFile(lnkPath, "winx-before-disable");
                            if (string.IsNullOrEmpty(lastBackup)) lastBackup = b;
                            ClearHiddenAndReadOnly(lnkPath);
                            File.Move(lnkPath, disabledPath, overwrite: true);
                        }
                        catch
                        {
                            if (isUserRoot) throw;
                        }
                    }
                }
                else
                {
                    if (File.Exists(disabledPath))
                    {
                        try
                        {
                            var b = backups.BackupFile(disabledPath, "winx-before-enable");
                            if (string.IsNullOrEmpty(lastBackup)) lastBackup = b;
                            ClearHiddenAndReadOnly(disabledPath);
                            File.Move(disabledPath, lnkPath, overwrite: true);
                            ClearHiddenAndReadOnly(lnkPath);
                        }
                        catch
                        {
                            if (isUserRoot) throw;
                        }
                    }
                }
            }
        }

        var primaryLnk = Path.Combine(WinXUserBasePath, groupName, baseLnkName);
        var primaryDisabled = primaryLnk + ".disabled";
        entry.IsDisabled = !enabled;
        entry.RegistryPath = enabled
            ? (File.Exists(primaryLnk) ? primaryLnk : Path.Combine(WinXDefaultBasePath, groupName, baseLnkName))
            : (File.Exists(primaryDisabled) ? primaryDisabled : Path.Combine(WinXDefaultBasePath, groupName, baseLnkName + ".disabled"));

        return lastBackup;
    }

    public static string Delete(MenuEntry entry, RegistryBackupService backups)
    {
        var (_, baseLnkName) = ParseGroupAndBaseName(entry.RegistryPath);
        string lastBackup = "";

        foreach (var root in new[] { WinXUserBasePath, WinXDefaultBasePath })
        {
            if (!Directory.Exists(root)) continue;
            bool isUserRoot = string.Equals(root, WinXUserBasePath, StringComparison.OrdinalIgnoreCase);

            foreach (var gDir in Directory.GetDirectories(root, "Group*"))
            {
                foreach (var candidate in new[] { Path.Combine(gDir, baseLnkName), Path.Combine(gDir, baseLnkName + ".disabled") })
                {
                    if (File.Exists(candidate))
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(lastBackup))
                                lastBackup = backups.BackupFile(candidate, "winx-before-delete");
                            ClearHiddenAndReadOnly(candidate);
                            File.Delete(candidate);
                        }
                        catch
                        {
                            if (isUserRoot) throw;
                        }
                    }
                }
            }
        }

        return lastBackup;
    }

    public static string SaveShortcut(
        string? originalPath,
        string displayName,
        string groupName,
        string targetPath,
        string arguments,
        bool runAsAdmin,
        RegistryBackupService backups)
    {
        if (string.IsNullOrEmpty(originalPath) || !File.Exists(originalPath))
        {
            throw new InvalidOperationException(
                "Windows 11 24H2 restricts the Win+X menu to the 17 built-in system shortcuts. Select an existing Win+X entry to rename it or move it between groups.");
        }

        backups.BackupFile(originalPath, "winx-before-edit");

        var (origGroup, baseLnkName) = ParseGroupAndBaseName(originalPath);
        bool wasDisabled = originalPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        string destFileName = wasDisabled ? baseLnkName + ".disabled" : baseLnkName;

        var userGroupDir = Path.Combine(WinXUserBasePath, groupName);
        Directory.CreateDirectory(userGroupDir);
        var userDestinationPath = Path.Combine(userGroupDir, destFileName);

        var tempLnkPath = Path.Combine(Path.GetTempPath(), $"winx_edit_{Guid.NewGuid():N}.lnk");
        try
        {
            File.Copy(originalPath, tempLnkPath, overwrite: true);
            ClearHiddenAndReadOnly(tempLnkPath);

            var link = (IShellLinkW)new CShellLink();
            var persist = (IPersistFile)link;
            persist.Load(tempLnkPath, 0);

            link.SetDescription(displayName ?? "");
            persist.Save(tempLnkPath, true);

            if (File.Exists(userDestinationPath))
                ClearHiddenAndReadOnly(userDestinationPath);
            File.Copy(tempLnkPath, userDestinationPath, overwrite: true);
            ClearHiddenAndReadOnly(userDestinationPath);
        }
        finally
        {
            if (File.Exists(tempLnkPath))
            {
                try { File.Delete(tempLnkPath); } catch { }
            }
        }

        try
        {
            var defaultGroupDir = Path.Combine(WinXDefaultBasePath, groupName);
            Directory.CreateDirectory(defaultGroupDir);
            var defaultDestinationPath = Path.Combine(defaultGroupDir, destFileName);
            if (File.Exists(defaultDestinationPath))
                ClearHiddenAndReadOnly(defaultDestinationPath);
            File.Copy(userDestinationPath, defaultDestinationPath, overwrite: true);
        }
        catch { }

        if (!string.Equals(origGroup, groupName, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var root in new[] { WinXUserBasePath, WinXDefaultBasePath })
            {
                foreach (var oldCandidate in new[]
                {
                    Path.Combine(root, origGroup, baseLnkName),
                    Path.Combine(root, origGroup, baseLnkName + ".disabled")
                })
                {
                    if (File.Exists(oldCandidate) &&
                        !string.Equals(oldCandidate, userDestinationPath, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            ClearHiddenAndReadOnly(oldCandidate);
                            File.Delete(oldCandidate);
                        }
                        catch { }
                    }
                }
            }
        }

        return userDestinationPath;
    }

    private static void ClearHiddenAndReadOnly(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var attrs = File.GetAttributes(filePath);
            var cleaned = attrs & ~(FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System);
            if (cleaned != attrs)
                File.SetAttributes(filePath, cleaned == 0 ? FileAttributes.Normal : cleaned);
        }
        catch { }
    }

    private static (string groupName, string baseLnkName) ParseGroupAndBaseName(string filePath)
    {
        var groupName = Path.GetFileName(Path.GetDirectoryName(filePath) ?? "Group1");
        var fileName = Path.GetFileName(filePath);
        if (fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^".disabled".Length];
        if (!fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            fileName += ".lnk";
        return (groupName, fileName);
    }

    public static uint ComputeHash(string targetPath, string arguments)
    {
        string generalized = GeneralizePath(targetPath);
        string blob = (generalized + (arguments ?? "") + Salt).ToLowerInvariant();
        byte[] bytes = Encoding.Unicode.GetBytes(blob);
        byte[] hash = new byte[4];
        HashData(bytes, bytes.Length, hash, hash.Length);
        return BitConverter.ToUInt32(hash, 0);
    }

    private static string GeneralizePath(string originalPath)
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        if (StartsWithFolder(originalPath, system32))
            return "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}" + originalPath[system32.Length..];

        if (StartsWithFolder(originalPath, windows))
            return "{F38BF404-1D43-42F2-9305-67DE0B28FC23}" + originalPath[windows.Length..];

        if (StartsWithFolder(originalPath, programFiles))
            return "{905E63B6-C1BF-494E-B29C-65B732D3D21A}" + originalPath[programFiles.Length..];

        return originalPath;
    }

    private static bool StartsWithFolder(string path, string folder)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            return path.Length == folder.Length || path[folder.Length] == '\\';
        return false;
    }

    private static (string target, string args, string description, bool runAsAdmin, uint hash) ReadShortcutInfo(string filePath)
    {
        var link = (IShellLinkW)new CShellLink();
        var persist = (IPersistFile)link;
        persist.Load(filePath, 0);

        var targetSb = new StringBuilder(260);
        link.GetPath(targetSb, targetSb.Capacity, IntPtr.Zero, 0);
        var argsSb = new StringBuilder(1024);
        link.GetArguments(argsSb, argsSb.Capacity);
        var descSb = new StringBuilder(512);
        link.GetDescription(descSb, descSb.Capacity);

        var dataList = (IShellLinkDataList)link;
        dataList.GetFlags(out uint flags);
        bool runAsAdmin = (flags & SLDF_RUNAS_USER) != 0;

        uint hash = 0;
        try
        {
            var store = (IPropertyStore)link;
            var keyHash = PkeyWinXHash;
            var pvHash = new PropVariant();
            store.GetValue(ref keyHash, pvHash);
            if (pvHash.vt == 19) // VT_UI4
                hash = pvHash.ulVal;
        }
        catch { }

        return (targetSb.ToString(), argsSb.ToString(), descSb.ToString().Trim(), runAsAdmin, hash);
    }

    private static string ResolveDisplayName(string filePath, string description, uint winxHash, bool runAsAdmin)
    {
        if (!string.IsNullOrWhiteSpace(description))
            return description.Replace("&", "");

        var builtInTitle = GetBuiltInTitle(winxHash, runAsAdmin);
        if (!string.IsNullOrWhiteSpace(builtInTitle))
            return builtInTitle;

        try
        {
            var shinfo = new SHFILEINFO();
            const uint SHGFI_DISPLAYNAME = 0x000000200;
            var ret = SHGetFileInfo(filePath, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_DISPLAYNAME);
            if (ret != IntPtr.Zero && !string.IsNullOrWhiteSpace(shinfo.szDisplayName))
            {
                var name = shinfo.szDisplayName.Trim();
                if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    name = name[..^4];
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }
        catch { }

        var fileName = Path.GetFileName(filePath);
        if (fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^".disabled".Length];
        if (fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^4];

        fileName = Regex.Replace(fileName, @"^\d+[a-zA-Z0-9-]*\s*-\s*", "");
        return string.IsNullOrWhiteSpace(fileName) ? Path.GetFileNameWithoutExtension(filePath) : fileName;
    }

    public static void RestartExplorer()
    {
        foreach (var procName in new[] { "StartMenuExperienceHost", "explorer" })
        {
            foreach (var proc in Process.GetProcessesByName(procName))
            {
                try
                {
                    proc.Kill();
                    proc.WaitForExit(1500);
                }
                catch { }
            }
        }

        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
    }

    #region Native P/Invoke and COM Interop

    [DllImport("shlwapi.dll", EntryPoint = "HashData", SetLastError = true)]
    private static extern int HashData([In] byte[] pbData, int cbData, [Out] byte[] pbHash, int cbHash);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
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

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public PROPERTYKEY(Guid g, uint p) { fmtid = g; pid = p; }
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, [In, Out] PropVariant pv);
        void SetValue(ref PROPERTYKEY key, [In] PropVariant pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private class PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint ulVal;
    }

    #endregion
}
