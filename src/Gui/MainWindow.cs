// Main window (WPF). The layout is MainWindow.xaml (embedded resource, loaded at runtime); this class
// fills in the texts, wires the events and runs the actions. Long operations run on a worker
// thread; the UI is only touched through the Dispatcher.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

sealed class MainWindow : Window
{
    static readonly Brush Green = Hex("#16A34A"), Red = Hex("#DC2626"), Amber = Hex("#D97706"), Grey = Hex("#9CA3AF");

    readonly FrameworkElement ui;
    readonly DetailsWindow details = new DetailsWindow();
    readonly string sessionLog = System.IO.Path.Combine(Backend.HelperLogDir, "setup-app.log");
    volatile bool busy;
    bool installed;

    public MainWindow()
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
            ui = (FrameworkElement)XamlReader.Load(s);
        Content = ui;
        Title = S.AppTitle + " " + Program.Version;
        Width = 1040; Height = 700; MinWidth = 920; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Hex("#F3F4F6");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        ApplyTexts();
        WireEvents();
        Loaded += delegate { LoadInstalledConfig(); RefreshStatus(); SetStatus(S.Ready); };
        Closing += OnClosing;
    }

    T Get<T>(string name) where T : class { return (T)ui.FindName(name); }
    TextBlock Text(string name) { return Get<TextBlock>(name); }
    static SolidColorBrush Hex(string hex) { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }

    // ------------------------------------------------------------------ setup

    void ApplyTexts()
    {
        Text("TitleText").Text = S.AppTitle;
        Text("SubtitleText").Text = S.Subtitle;
        Text("TileTvLabel").Text = S.StTv;
        Text("TileHelperLabel").Text = S.StHelperRunning;
        Text("TileTasksLabel").Text = S.StTasks;
        Text("TileShutdownLabel").Text = S.StShutdown;

        Text("TvCardTitle").Text = S.GroupTv;
        Get<Button>("RefreshButton").Content = S.Refresh;
        Text("IpLabel").Text = S.TvAddress;
        Get<Button>("FindButton").Content = S.Find;
        Text("FoundLabel").Text = S.FoundTvs;
        Text("MacLabel").Text = S.TvMac;
        Text("InputLabel").Text = S.PcInput;
        Text("InputHint").Text = S.PcInputHint;
        Get<Run>("PrereqText").Text = S.TvPrereq;
        ComboBox input = Get<ComboBox>("InputBox");
        foreach (string item in new string[] { S.PcInputNone, "HDMI1", "HDMI2", "HDMI3", "HDMI4" }) input.Items.Add(item);
        input.SelectedIndex = 1;

        Text("SettingsCardTitle").Text = S.GroupSettings;
        Text("KeepOnRestartText").Text = S.KeepOnRestart;
        Text("WakeOnInputText").Text = S.WakeOnInput;
        Text("GraceText").Text = S.GraceSeconds;
        Text("KeepOffText").Text = S.KeepOff;
        Text("KeepOffHint").Text = S.KeepOffHint;
        Text("PowerTestText").Text = S.PowerTest;
        Get<CheckBox>("KeepOnRestartSwitch").IsChecked = true;
        Get<CheckBox>("WakeOnInputSwitch").IsChecked = true;
        Get<TextBox>("GraceBox").Text = "15";

        Get<Button>("DetailsButton").Content = S.ShowDetails;
        Get<Button>("UninstallButton").Content = S.Uninstall;
        Get<Button>("CloseButton").Content = S.Close;
        Get<Button>("InstallButton").Content = S.Install;
    }

    void WireEvents()
    {
        Get<Button>("FindButton").Click += delegate { FindTvs(); };
        Get<Button>("RefreshButton").Click += delegate { RefreshStatus(); };
        Get<Button>("InstallButton").Click += delegate { StartInstall(); };
        Get<Button>("UninstallButton").Click += delegate { StartUninstall(); };
        Get<Button>("CloseButton").Click += delegate { Close(); };
        Get<Button>("DetailsButton").Click += delegate { ShowDetails(); };
        Get<Hyperlink>("PrereqLink").Click += delegate
        {
            MessageBox.Show(this, S.TvPrereqText, S.TvPrereq.TrimEnd('.'), MessageBoxButton.OK, MessageBoxImage.Information);
        };
        Get<TextBox>("IpBox").LostFocus += delegate { ValidateIp(); };
        Get<ComboBox>("FoundBox").SelectionChanged += delegate
        {
            TvInfo tv = Get<ComboBox>("FoundBox").SelectedItem as TvInfo;
            if (tv == null) return;
            Get<TextBox>("IpBox").Text = tv.Ip;
            Get<TextBox>("MacBox").Text = tv.Mac;
            ShowIpError(null);
        };
        CheckBox wake = Get<CheckBox>("WakeOnInputSwitch");
        RoutedEventHandler graceEnabled = delegate { Get<TextBox>("GraceBox").IsEnabled = wake.IsChecked == true && !busy; };
        wake.Checked += graceEnabled;
        wake.Unchecked += graceEnabled;
    }

    // ------------------------------------------------------------------ state

    void LoadInstalledConfig()
    {
        InstalledConfig c = Backend.LoadConfig();
        if (c == null) return;
        Get<TextBox>("IpBox").Text = c.Ip;
        Get<TextBox>("MacBox").Text = c.Mac;
        ComboBox input = Get<ComboBox>("InputBox");
        int i = input.Items.IndexOf(c.PcInput.ToUpperInvariant());
        input.SelectedIndex = c.PcInput.Length == 0 ? 0 : (i >= 0 ? i : 1);
        Get<CheckBox>("KeepOnRestartSwitch").IsChecked = c.KeepOnRestart;
        Get<CheckBox>("WakeOnInputSwitch").IsChecked = c.WakeOnInput;
        Get<TextBox>("GraceBox").Text = c.GraceSeconds.ToString();
        Get<TextBox>("GraceBox").IsEnabled = c.WakeOnInput;
        Get<CheckBox>("KeepOffSwitch").IsChecked = c.KeepOff;
    }

    void RefreshStatus()
    {
        string ip = Get<TextBox>("IpBox").Text.Trim();
        Get<Button>("RefreshButton").IsEnabled = false;
        RunInBackground(delegate
        {
            StatusInfo s = Backend.GetStatus(IsValidIp(ip) ? ip : null);
            OnUi(delegate
            {
                installed = s.Installed;
                Tile("TileTv", s.TvState ?? S.TvOffline, s.TvState == "on" ? Green : s.TvState == null ? Red : Amber);
                Tile("TileHelper", s.HelperRunning ? S.Yes : S.No, s.HelperRunning ? Green : Red);
                int tasks = (s.PowerTask ? 1 : 0) + (s.HelperTask ? 1 : 0);
                Tile("TileTasks", tasks + " / 2", tasks == 2 ? Green : tasks == 0 ? Red : Amber);
                Tile("TileShutdown", s.ShutdownScript ? S.Yes : S.No, s.ShutdownScript ? Green : Red);

                Text("InstalledText").Text = installed ? S.StInstalled : S.StNotInstalled;
                Get<Ellipse>("InstalledDot").Fill = installed ? Green : Grey;
                Get<Border>("InstalledPill").Background = installed ? Hex("#DCFCE7") : Hex("#F3F4F6");
                Get<Button>("InstallButton").Content = installed ? S.Update : S.Install;
                Get<Button>("UninstallButton").IsEnabled = installed && !busy;
                Get<Button>("RefreshButton").IsEnabled = true;
            });
        });
    }

    void Tile(string name, string value, Brush color)
    {
        Text(name + "Value").Text = value;
        Get<Ellipse>(name + "Dot").Fill = color;
    }

    void SetBusy(bool on)
    {
        busy = on;
        foreach (string name in new string[] { "InstallButton", "UninstallButton", "FindButton", "RefreshButton", "IpBox", "FoundBox",
                                                "InputBox", "KeepOnRestartSwitch", "WakeOnInputSwitch", "GraceBox", "KeepOffSwitch", "PowerTestSwitch" })
            ((UIElement)ui.FindName(name)).IsEnabled = !on;
        if (!on)
        {
            Get<Button>("UninstallButton").IsEnabled = installed;
            Get<TextBox>("GraceBox").IsEnabled = Get<CheckBox>("WakeOnInputSwitch").IsChecked == true;
            Get<Border>("Banner").Visibility = Visibility.Collapsed;
        }
        Get<ProgressBar>("Busy").Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Cursor = on ? System.Windows.Input.Cursors.AppStarting : null;
    }

    void SetStatus(string text) { Text("StatusText").Text = text; }

    // ------------------------------------------------------------------ validation

    bool ValidateIp()
    {
        bool ok = IsValidIp(Get<TextBox>("IpBox").Text.Trim());
        ShowIpError(ok ? null : S.ErrIp);
        return ok;
    }

    void ShowIpError(string message)
    {
        TextBlock e = Text("IpError");
        e.Text = message ?? "";
        e.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    static bool IsValidIp(string s)
    {
        IPAddress a;
        return s.Split('.').Length == 4 && IPAddress.TryParse(s, out a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    bool TryGetGrace(out int seconds)
    {
        if (int.TryParse(Get<TextBox>("GraceBox").Text.Trim(), out seconds) && seconds >= 5 && seconds <= 600) return true;
        MessageBox.Show(this, S.ErrGrace, S.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        Get<TextBox>("GraceBox").Focus();
        return false;
    }

    // ------------------------------------------------------------------ actions

    void FindTvs()
    {
        SetBusy(true);
        SetStatus(S.Finding);
        AppendLine(">> " + S.Finding);
        RunInBackground(delegate
        {
            List<TvInfo> tvs = Backend.FindTvs();
            OnUi(delegate
            {
                ComboBox found = Get<ComboBox>("FoundBox");
                found.Items.Clear();
                foreach (TvInfo tv in tvs) { found.Items.Add(tv); AppendLine("   " + tv); }
                SetBusy(false);
                if (tvs.Count == 0) { AppendLine("   !!  " + S.FoundNone); SetStatus(S.FoundNone); return; }
                found.SelectedIndex = 0;
                SetStatus(S.Ready);
                RefreshStatus();
            });
        });
    }

    void StartInstall()
    {
        if (!ValidateIp()) { Get<TextBox>("IpBox").Focus(); return; }
        int grace;
        if (!TryGetGrace(out grace)) return;
        ComboBox input = Get<ComboBox>("InputBox");
        Backend.InstallOptions o = new Backend.InstallOptions();
        o.TvIp = Get<TextBox>("IpBox").Text.Trim();
        o.PcInput = input.SelectedIndex <= 0 ? "" : (string)input.SelectedItem;
        o.KeepOnRestart = Get<CheckBox>("KeepOnRestartSwitch").IsChecked == true;
        o.WakeOnInput = Get<CheckBox>("WakeOnInputSwitch").IsChecked == true;
        o.KeepOff = Get<CheckBox>("KeepOffSwitch").IsChecked == true;
        o.PowerTest = Get<CheckBox>("PowerTestSwitch").IsChecked == true;
        o.GraceSeconds = grace;

        ClearOutput();
        SetBusy(true);
        SetStatus(S.Working);
        SessionLog("=== install " + DateTime.Now + " (tv " + o.TvIp + ", input " + (o.PcInput.Length > 0 ? o.PcInput : "-") + ")");
        RunInBackground(delegate
        {
            int code = Backend.Install(o, OnScriptLine);
            OnUi(delegate
            {
                SetBusy(false);
                SessionLog("=== install exit code " + code);
                string msg = code == 0 ? S.InstallOk : code == 3 ? S.InstallWarn : S.InstallFailed;
                SetStatus(msg);
                LoadInstalledConfig();
                RefreshStatus();
                MessageBox.Show(this, msg, S.AppTitle, MessageBoxButton.OK,
                                code == 0 ? MessageBoxImage.Information : code == 3 ? MessageBoxImage.Warning : MessageBoxImage.Error);
            });
        });
    }

    void StartUninstall()
    {
        if (MessageBox.Show(this, S.UninstallConfirm, S.AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        ClearOutput();
        SetBusy(true);
        SetStatus(S.Working);
        SessionLog("=== uninstall " + DateTime.Now);
        RunInBackground(delegate
        {
            int code = Backend.Uninstall(OnScriptLine);
            OnUi(delegate
            {
                SetBusy(false);
                SessionLog("=== uninstall exit code " + code);
                SetStatus(code == 0 ? S.Ready : S.UninstallFailed);
                RefreshStatus();
                MessageBox.Show(this, code == 0 ? S.UninstallOk : S.UninstallFailed, S.AppTitle, MessageBoxButton.OK,
                                code == 0 ? MessageBoxImage.Information : MessageBoxImage.Error);
            });
        });
    }

    void ShowDetails()
    {
        if (details.Owner == null) details.Owner = this;
        details.Show();
        details.Activate();
    }

    void OnClosing(object sender, CancelEventArgs e)
    {
        if (busy && MessageBox.Show(this, S.BusyClose, S.AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    // ------------------------------------------------------------------ output console

    // Called on the worker thread for every line the script writes.
    void OnScriptLine(string line)
    {
        SessionLog(line);
        OnUi(delegate
        {
            AppendLine(line);
            if (line.StartsWith(">>")) SetStatus(line.Substring(2).Trim());   // current step, in the footer
            // the installer is waiting for the user to accept a pairing prompt on the TV
            Border banner = Get<Border>("Banner");
            if (line.IndexOf("accept the prompt", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Text("BannerText").Text = S.AcceptOnTv;
                banner.Visibility = Visibility.Visible;
            }
            else if (line.TrimStart().StartsWith("OK") || line.StartsWith(">>")) banner.Visibility = Visibility.Collapsed;
        });
    }

    void AppendLine(string line) { details.Append(line); }

    void ClearOutput() { details.Clear(); }
    void SessionLog(string line)
    {
        try
        {
            Directory.CreateDirectory(Backend.HelperLogDir);
            lock (sessionLog) File.AppendAllText(sessionLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine);
        }
        catch { }
    }

    // ------------------------------------------------------------------ threading

    void OnUi(Action a) { Dispatcher.BeginInvoke(a); }

    void RunInBackground(ThreadStart work)
    {
        Thread t = new Thread(delegate()
        {
            try { work(); }
            catch (Exception e)
            {
                SessionLog("UNEXPECTED: " + e);
                OnUi(delegate
                {
                    SetBusy(false);
                    MessageBox.Show(this, e.Message, S.Error, MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        });
        t.IsBackground = true;
        t.Start();
    }
}
