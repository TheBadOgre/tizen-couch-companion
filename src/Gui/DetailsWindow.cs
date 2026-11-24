// "Details" window: the full output of the installer / uninstaller, kept out of the main window.
// It collects lines all the time (also while closed) and can be opened from the footer button.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

sealed class DetailsWindow : Window
{
    readonly StackPanel lines = new StackPanel();
    readonly ScrollViewer scroll = new ScrollViewer();
    readonly List<string> text = new List<string>();

    public DetailsWindow()
    {
        Title = S.AppTitle + " - " + S.Details;
        Width = 820; Height = 520; MinWidth = 500; MinHeight = 300;
        Background = Brush("#0F172A");
        ShowInTaskbar = false;
        UseLayoutRounding = true;

        DockPanel root = new DockPanel();

        StackPanel bar = new StackPanel();
        bar.Orientation = Orientation.Horizontal;
        bar.HorizontalAlignment = HorizontalAlignment.Right;
        bar.Margin = new Thickness(12, 10, 12, 10);
        bar.Children.Add(BarButton(S.CopyOutput, delegate { Copy(); }));
        bar.Children.Add(BarButton(S.OpenLogs, delegate { OpenLogFolders(); }));
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        lines.Margin = new Thickness(18, 4, 18, 14);
        TextElement.SetFontFamily(lines, new FontFamily("Cascadia Mono, Consolas"));
        TextElement.SetFontSize(lines, 12);
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        scroll.Content = lines;
        root.Children.Add(scroll);
        Content = root;
    }

    // Closing only hides it, so the output keeps being collected.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current != null && Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    public void Append(string line)
    {
        string t = line.TrimStart();
        Brush color = Brush("#CBD5E1");
        FontWeight weight = FontWeights.Normal;
        if (t.StartsWith(">>")) { color = Brush("#93C5FD"); weight = FontWeights.SemiBold; }
        else if (t.StartsWith("OK")) color = Brush("#86EFAC");
        else if (t.StartsWith("!!")) color = Brush("#FCD34D");
        else if (t.StartsWith("ERROR") || t.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0) color = Brush("#FCA5A5");

        TextBlock tb = new TextBlock();
        tb.Text = line.Length == 0 ? " " : line;
        tb.Foreground = color;
        tb.FontWeight = weight;
        lines.Children.Add(tb);
        text.Add(line);
        scroll.ScrollToEnd();
    }

    public void Clear()
    {
        lines.Children.Clear();
        text.Clear();
    }

    void Copy()
    {
        if (text.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, text.ToArray())); } catch { }
    }

    static void OpenLogFolders()
    {
        foreach (string dir in new string[] { Backend.InstallDir, Backend.HelperLogDir })
            if (Directory.Exists(dir))
                try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
    }

    static Button BarButton(string text, RoutedEventHandler click)
    {
        Button b = new Button();
        b.Content = text;
        b.Margin = new Thickness(8, 0, 0, 0);
        b.Padding = new Thickness(14, 5, 14, 5);
        b.Background = Brush("#1E293B");
        b.Foreground = Brush("#E2E8F0");
        b.BorderBrush = Brush("#334155");
        b.Cursor = System.Windows.Input.Cursors.Hand;
        b.Click += click;
        return b;
    }

    static SolidColorBrush Brush(string hex) { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }
}
