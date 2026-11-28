using Microsoft.Win32;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed class ContextMenuScanner
{
    private static readonly string[] VerbLocations =
    [
        "*\\shell",
        "AllFilesystemObjects\\shell",
        "Directory\\shell",
        "Directory\\Background\\shell",
        "Drive\\shell",
        "Folder\\shell",
        "Folder\\Background\\shell",
        "DesktopBackground\\Shell"
    ];

    private static readonly string[] HandlerLocations =
    [
        "*\\shellex\\ContextMenuHandlers",
        "AllFilesystemObjects\\shellex\\ContextMenuHandlers",
        "Directory\\shellex\\ContextMenuHandlers",
        "Directory\\Background\\shellex\\ContextMenuHandlers",
        "Drive\\shellex\\ContextMenuHandlers",
        "Folder\\shellex\\ContextMenuHandlers"
    ];

    public List<MenuEntry> Scan()
    {
        var entries = new List<MenuEntry>();
        using var classesRoot = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
        foreach (var location in VerbLocations) ScanVerbs(classesRoot, location, entries);
        foreach (var location in HandlerLocations) ScanHandlers(classesRoot, location, entries);
        entries.AddRange(WinXService.Scan());
        entries.AddRange(StartupService.Scan());
        entries.AddRange(InstalledAppService.Scan());

        return entries.GroupBy(e => e.RegistryPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.Location)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanVerbs(RegistryKey root, string location, List<MenuEntry> entries)
    {
        try
        {
            using var parent = root.OpenSubKey(location);
            if (parent is null) return;
            foreach (var name in parent.GetSubKeyNames())
            {
                using var verb = parent.OpenSubKey(name);
                if (verb is null) continue;
                var label = verb.GetValue(null)?.ToString();
                if (string.IsNullOrWhiteSpace(label)) label = name;
                using var commandKey = verb.OpenSubKey("command");
                var command = commandKey?.GetValue(null)?.ToString() ?? "";
                var disabled = verb.GetValue("LegacyDisable") is not null;
                entries.Add(new MenuEntry
                {
                    Name = label,
                    Location = location,
                    RegistryPath = "HKEY_CLASSES_ROOT\\" + location + "\\" + name,
                    Kind = EntryKind.StaticVerb,
                    Command = command,
                    IsDisabled = disabled
                });
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }

    private static void ScanHandlers(RegistryKey root, string location, List<MenuEntry> entries)
    {
        try
        {
            using var parent = root.OpenSubKey(location);
            if (parent is null) return;
            foreach (var name in parent.GetSubKeyNames())
            {
                using var handler = parent.OpenSubKey(name);
                if (handler is null) continue;
                var clsid = handler.GetValue(null)?.ToString() ?? "";
                var disabled = clsid.StartsWith("xContextGuard.Disabled:", StringComparison.Ordinal);
                entries.Add(new MenuEntry
                {
                    Name = name,
                    Location = location,
                    RegistryPath = "HKEY_CLASSES_ROOT\\" + location + "\\" + name,
                    Kind = EntryKind.ContextMenuHandler,
                    Command = clsid,
                    IsDisabled = disabled
                });
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }
}
