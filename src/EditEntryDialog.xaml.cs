using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using xDriverUpdater.Models;
using xDriverUpdater.Services;

namespace xDriverUpdater;

public partial class EditEntryDialog : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public MenuEntry? OriginalEntry { get; }
    public string EntryName => NameBox.Text.Trim();
    public string TargetPath => TargetBox.Text.Trim();
    public string Arguments => ArgumentsBox.Text.Trim();
    public string SelectedGroup
    {
        get
        {
            if (GroupComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag;
            return "Group3";
        }
    }
    public bool RunAsAdmin => RunAsAdminCheckBox.IsChecked == true;
    public bool IsWinXMode { get; }

    public EditEntryDialog(MenuEntry? entry = null)
    {
        InitializeComponent();
        OriginalEntry = entry;

        if (entry is not null && entry.Kind == EntryKind.WinXShortcut)
        {
            IsWinXMode = true;
            Title = "Edit Win+X Entry";
            DialogTitleText.Text = "Edit Win+X Entry";
            DialogSubtitleText.Text = $"Rename or move built-in Win+X shortcut: {entry.Name}";
            NameBox.Text = entry.Name;
            TargetBox.Text = entry.Command;
            ArgumentsBox.Text = entry.Arguments;
            RunAsAdminCheckBox.IsChecked = entry.RunAsAdmin;

            TargetLabelText.Text = "Built-in Target (read-only on Windows 11 24H2)";
            TargetBox.IsReadOnly = true;
            TargetBox.Opacity = 0.75;
            BrowseTargetButton.Visibility = Visibility.Collapsed;

            ArgumentsLabelText.Text = "Command Arguments (read-only for built-in Win+X)";
            ArgumentsBox.IsReadOnly = true;
            ArgumentsBox.Opacity = 0.75;
            RunAsAdminCheckBox.IsEnabled = false;

            var grp = !string.IsNullOrEmpty(entry.Group) ? entry.Group : ParseGroupFromPath(entry.RegistryPath);
            SelectGroup(grp);
        }
        else if (entry is not null && entry.Kind == EntryKind.StartupFolder)
        {
            IsWinXMode = false;
            Title = "Edit Startup Folder Item";
            DialogTitleText.Text = "Edit Startup Folder Item";
            DialogSubtitleText.Text = $"Editing startup shortcut: {entry.Name}";
            NameBox.Text = entry.Name;

            string rawTarget = entry.Command;
            string rawArgs = entry.Arguments;
            bool admin = entry.RunAsAdmin;
            try
            {
                var (t, a, adm) = StartupService.ReadShortcut(entry.RegistryPath);
                if (!string.IsNullOrWhiteSpace(t))
                {
                    rawTarget = t;
                    rawArgs = a;
                    admin = adm;
                }
            }
            catch { }

            TargetBox.Text = rawTarget;
            ArgumentsBox.Text = rawArgs;
            RunAsAdminCheckBox.IsChecked = admin;

            GroupPanel.Visibility = Visibility.Collapsed;
            WinXNoticeBorder.Visibility = Visibility.Collapsed;
        }
        else if (entry is not null && entry.Kind == EntryKind.StartupRegistry)
        {
            IsWinXMode = false;
            Title = "Edit Startup Registry Entry";
            DialogTitleText.Text = "Edit Startup Registry Entry";
            DialogSubtitleText.Text = $"Editing {entry.Location}: {entry.Name}";
            NameBox.Text = entry.Name;
            TargetLabelText.Text = "Startup Command Line";
            TargetBox.Text = entry.Command;

            GroupPanel.Visibility = Visibility.Collapsed;
            ArgumentsPanel.Visibility = Visibility.Collapsed;
            AdminPanel.Visibility = Visibility.Collapsed;
            WinXNoticeBorder.Visibility = Visibility.Collapsed;
        }
        else if (entry is not null && entry.Kind == EntryKind.InstalledApp)
        {
            IsWinXMode = false;
            Title = "Edit Installed Application Entry";
            DialogTitleText.Text = "Edit Installed Application Entry";
            DialogSubtitleText.Text = entry.RegistryPath;
            NameBox.Text = !string.IsNullOrWhiteSpace(entry.ValueName) ? entry.ValueName : entry.Name;
            TargetLabelText.Text = "Uninstall Command (UninstallString)";
            TargetBox.Text = !string.IsNullOrWhiteSpace(entry.Arguments) ? entry.Arguments : entry.Command;

            GroupPanel.Visibility = Visibility.Collapsed;
            ArgumentsPanel.Visibility = Visibility.Collapsed;
            AdminPanel.Visibility = Visibility.Collapsed;
            WinXNoticeBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            IsWinXMode = false;
            Title = "Edit Context Menu Command";
            DialogTitleText.Text = "Edit Context Menu Command";
            DialogSubtitleText.Text = entry is not null ? $"Editing: {entry.Name}" : "Edit command";
            NameBox.Text = entry?.Name ?? "";
            TargetBox.Text = entry?.Command ?? "";

            GroupPanel.Visibility = Visibility.Collapsed;
            ArgumentsPanel.Visibility = Visibility.Collapsed;
            AdminPanel.Visibility = Visibility.Collapsed;
            WinXNoticeBorder.Visibility = Visibility.Collapsed;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch { }
    }

    private void SelectGroup(string groupName)
    {
        foreach (ComboBoxItem item in GroupComboBox.Items)
        {
            if (string.Equals(item.Tag as string, groupName, StringComparison.OrdinalIgnoreCase))
            {
                GroupComboBox.SelectedItem = item;
                return;
            }
        }
        if (GroupComboBox.Items.Count > 0)
            GroupComboBox.SelectedIndex = 2;
    }

    private static string ParseGroupFromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "Group3";
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return "Group3";
        var name = Path.GetFileName(dir);
        return name.StartsWith("Group", StringComparison.OrdinalIgnoreCase) ? name : "Group3";
    }

    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        if (IsWinXMode) return;

        var dialog = new OpenFileDialog
        {
            Title = "Select Target Executable or File",
            Filter = "Executables and Scripts (*.exe;*.msc;*.cpl;*.bat;*.cmd;*.ps1)|*.exe;*.msc;*.cpl;*.bat;*.cmd;*.ps1|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };

        if (dialog.ShowDialog(this) == true)
        {
            TargetBox.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            DarkDialog.Show("Please enter a display name for this entry.", "Name Required", DarkDialogKind.Warning, this);
            NameBox.Focus();
            return;
        }

        if (!IsWinXMode && string.IsNullOrWhiteSpace(TargetBox.Text))
        {
            DarkDialog.Show("Please enter or browse to a target file path.", "Target Required", DarkDialogKind.Warning, this);
            TargetBox.Focus();
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
