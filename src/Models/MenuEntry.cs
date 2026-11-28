namespace xDriverUpdater.Models;

public enum EntryKind
{
    StaticVerb,
    ContextMenuHandler,
    WinXShortcut,
    StartupRegistry,
    StartupFolder,
    InstalledApp
}

public sealed class MenuEntry
{
    public required string Name { get; set; }
    public required string Location { get; set; }
    public required string RegistryPath { get; set; }
    public required EntryKind Kind { get; init; }
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Group { get; set; } = "";
    public string ParentKeyPath { get; set; } = "";
    public string ValueName { get; set; } = "";
    public string StartupApprovedPath { get; set; } = "";
    public bool RunAsAdmin { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsMissingFiles { get; set; }
    public bool IsHiddenApp { get; set; }

    public string Status => Kind == EntryKind.InstalledApp
        ? (IsHiddenApp ? (IsMissingFiles ? "Hidden (Missing)" : "Hidden") : (IsMissingFiles ? "Missing files" : "Installed"))
        : (IsDisabled ? "Disabled" : "Enabled");

    public string StatusBadgeBg => IsDisabled ? "#2B2314" : "#132F23";
    public string StatusBadgeFg => IsDisabled ? "#FBBF24" : "#4ADE80";

    public string TypeLabel => Kind switch
    {
        EntryKind.StaticVerb => "Command",
        EntryKind.ContextMenuHandler => "Shell extension",
        EntryKind.WinXShortcut => "Win+X shortcut",
        EntryKind.StartupRegistry => "Startup (Registry)",
        EntryKind.StartupFolder => "Startup (Folder)",
        EntryKind.InstalledApp => "Installed app",
        _ => "Unknown"
    };

    public bool IsWinX => Kind == EntryKind.WinXShortcut;
    public bool IsStartup => Kind is EntryKind.StartupRegistry or EntryKind.StartupFolder;
    public bool IsInstalledApp => Kind == EntryKind.InstalledApp;
}
