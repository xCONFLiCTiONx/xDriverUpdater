using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace xDriverUpdater;

public enum DarkDialogKind
{
    Info,
    Success,
    Warning,
    Error,
    Question
}

/// <summary>
/// Dark-mode themed modal dialog matching xDriverUpdater's palette (replaces white Win32 MessageBox).
/// </summary>
public sealed class DarkDialog : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private bool _result;

    private DarkDialog(string title, string message, DarkDialogKind kind, bool isConfirm, Window? owner)
    {
        Title = title;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x14));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF5));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Width = 480;
        MinWidth = 360;
        MaxWidth = 580;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        if (owner != null && owner.IsLoaded)
            Owner = owner;

        var (badgeText, badgeHex) = kind switch
        {
            DarkDialogKind.Success => ("OK", "#22C55E"),
            DarkDialogKind.Warning => ("WARN", "#F38020"),
            DarkDialogKind.Error => ("ERR", "#EF4444"),
            DarkDialogKind.Question => ("CONFIRM", "#F38020"),
            _ => ("INFO", "#FAAD3F")
        };

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header row with badge + title
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var badge = new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString(badgeHex)!,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = badgeText,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            }
        };
        header.Children.Add(badge);
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Message body inside subtle dark card with scroll support for long outputs
        var bodyScroll = new ScrollViewer
        {
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            }
        };
        var bodyCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 16),
            Child = bodyScroll
        };
        Grid.SetRow(bodyCard, 1);
        root.Children.Add(bodyCard);

        // Buttons row
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        if (isConfirm)
        {
            var cancelBtn = new Button
            {
                Content = "Cancel",
                MinWidth = 84,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };
            cancelBtn.Click += (_, _) => { _result = false; Close(); };
            btnPanel.Children.Add(cancelBtn);

            var yesBtn = new Button
            {
                Content = "Confirm",
                MinWidth = 92,
                IsDefault = true
            };
            if (Application.Current?.TryFindResource("PrimaryButton") is Style primaryStyle)
                yesBtn.Style = primaryStyle;
            yesBtn.Click += (_, _) => { _result = true; Close(); };
            btnPanel.Children.Add(yesBtn);
        }
        else
        {
            var okBtn = new Button
            {
                Content = "OK",
                MinWidth = 88,
                IsDefault = true,
                IsCancel = true
            };
            if (Application.Current?.TryFindResource("PrimaryButton") is Style primaryStyle)
                okBtn.Style = primaryStyle;
            okBtn.Click += (_, _) => { _result = true; Close(); };
            btnPanel.Children.Add(okBtn);
        }

        Grid.SetRow(btnPanel, 2);
        root.Children.Add(btnPanel);
        Content = root;
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

    public static void Show(string message, string title = "xSecurityCenter", DarkDialogKind kind = DarkDialogKind.Info, Window? owner = null)
    {
        var dlg = new DarkDialog(title, message, kind, isConfirm: false, owner ?? System.Windows.Application.Current?.MainWindow);
        dlg.ShowDialog();
    }

    public static bool Confirm(string message, string title = "Confirm", DarkDialogKind kind = DarkDialogKind.Question, Window? owner = null)
    {
        var dlg = new DarkDialog(title, message, kind, isConfirm: true, owner ?? System.Windows.Application.Current?.MainWindow);
        dlg.ShowDialog();
        return dlg._result;
    }
}
