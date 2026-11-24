// Everything the setup app does to the system goes through the existing PowerShell scripts
// (Install.ps1 -Unattended, Uninstall.ps1, CouchCompanion.Core.ps1), so the app and the scripts always
// install exactly the same way. This class runs them and reads the installed state.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

sealed class TvInfo
{
    public string Ip, Mac, Name, Model, Network, Power;
    public override string ToString() { return Name + "  " + Model + "  " + Ip + "  " + Mac + "  (" + Network + ", " + Power + ")"; }
}

sealed class InstalledConfig
{
    public string Ip = "", Mac = "", PcInput = "";
    public bool KeepOnRestart = true, WakeOnInput, KeepOff, IpControlPaired;
    public int GraceSeconds = 15;
}

sealed class StatusInfo
{
    public bool Installed, PowerTask, HelperTask, ShutdownScript, HelperRunning;
    public string TvState;   // "on", "standby", null = not reachable / unknown
}

static class Backend
{
    public const string PowerTaskName = "Couch Companion - Power On";
    public const string HelperTaskName = "Couch Companion - Helper";

    public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CouchCompanion");
    public static readonly string HelperLogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchCompanion");
    static readonly string PowerShellExe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    public static string SrcDir;   // ...\CouchCompanion\src

    // src is next to the build folder (Setup.cmd builds into <root>\build), or given with --src.
    public static bool FindSources(string[] args)
    {
        List<string> candidates = new List<string>();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--src") candidates.Add(args[i + 1]);
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        candidates.Add(Path.Combine(exeDir, @"..\src"));
        candidates.Add(Path.Combine(exeDir, "src"));
        foreach (string c in candidates)
        {
            string full = Path.GetFullPath(c);
            if (File.Exists(Path.Combine(full, "Install.ps1")) && File.Exists(Path.Combine(full, "CouchCompanion.Core.ps1"))) { SrcDir = full; return true; }
        }
        return false;
    }

    // ------------------------------------------------------------------ running PowerShell

    // Runs a script; every output line goes to onLine (on a worker thread); returns the exit code.
    public static int RunScript(string scriptFile, string arguments, Action<string> onLine)
    {
        return RunPowerShell("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptFile + "\" " + arguments, onLine);
    }

    // Runs a command; -EncodedCommand avoids every quoting problem.
    public static int RunCommand(string command, Action<string> onLine)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        return RunPowerShell("-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, onLine);
    }

    static int RunPowerShell(string arguments, Action<string> onLine)
    {
        // A redirected console program writes in the OEM code page.
        Encoding oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        ProcessStartInfo psi = new ProcessStartInfo(PowerShellExe, arguments);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;    // closed right away: a prompt gets end-of-input instead of hanging
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = oem;
        psi.StandardErrorEncoding = oem;
        psi.WorkingDirectory = SrcDir;
        using (Process p = new Process())
        {
            p.StartInfo = psi;
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) onLine(e.Data); };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) onLine("ERROR: " + e.Data); };
            p.Start();
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    static string Quote(string s) { return "'" + s.Replace("'", "''") + "'"; }

    // ------------------------------------------------------------------ actions

    public static List<TvInfo> FindTvs()
    {
        List<TvInfo> tvs = new List<TvInfo>();
        string cmd = ". " + Quote(Path.Combine(SrcDir, "CouchCompanion.Core.ps1")) + "; " +
                     "foreach ($t in @(Find-TvDevices)) { 'TV|{0}|{1}|{2}|{3}|{4}|{5}' -f $t.Ip, $t.Mac, $t.Name, $t.Model, $t.Network, $t.PowerState }";
        RunCommand(cmd, delegate(string line)
        {
            if (!line.StartsWith("TV|")) return;
            string[] f = line.Split('|');
            if (f.Length < 7) return;
            TvInfo t = new TvInfo();
            t.Ip = f[1]; t.Mac = f[2]; t.Name = WebUtility.HtmlDecode(f[3]); t.Model = f[4]; t.Network = f[5]; t.Power = f[6];
            lock (tvs) tvs.Add(t);
        });
        return tvs;
    }

    public sealed class InstallOptions
    {
        public string TvIp, PcInput;
        public bool KeepOnRestart, WakeOnInput, KeepOff, PowerTest;
        public int GraceSeconds;
    }

    public static int Install(InstallOptions o, Action<string> onLine)
    {
        string args = "-Unattended -TvAddress " + o.TvIp +
                      " -OnRestart " + (o.KeepOnRestart ? "Keep" : "TurnOff") +
                      " -WakeOnInput " + (o.WakeOnInput ? "Yes" : "No") +
                      " -KeepOff " + (o.KeepOff ? "Yes" : "No") +
                      " -PcInput " + (string.IsNullOrEmpty(o.PcInput) ? "-" : o.PcInput) +
                      " -WakeGraceSeconds " + o.GraceSeconds +
                      (o.PowerTest ? "" : " -SkipPowerTest");
        return RunScript(Path.Combine(SrcDir, "Install.ps1"), args, onLine);
    }

    public static int Uninstall(Action<string> onLine)
    {
        return RunScript(Path.Combine(SrcDir, "Uninstall.ps1"), "", onLine);
    }

    // ------------------------------------------------------------------ installed state

    static string ConfigPath { get { return Path.Combine(InstallDir, "config.json"); } }

    public static InstalledConfig LoadConfig()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            string json = File.ReadAllText(ConfigPath);
            InstalledConfig c = new InstalledConfig();
            c.Ip = Str(json, "ip");
            c.Mac = Str(json, "mac");
            c.PcInput = Str(json, "pcInput");
            c.IpControlPaired = Str(json, "ipControlToken").Length > 0;
            c.KeepOnRestart = Bool(json, "keepOnRestart", true);
            c.WakeOnInput = Bool(json, "wakeOnInput", false);
            c.KeepOff = Bool(json, "keepOffAfterRemote", false);
            Match g = Regex.Match(json, "\"wakeOnInputGraceSeconds\"\\s*:\\s*(\\d+)");
            if (g.Success) c.GraceSeconds = int.Parse(g.Groups[1].Value);
            return c;
        }
        catch { return null; }
    }

    static string Str(string json, string key)
    {
        Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    static bool Bool(string json, string key, bool fallback)
    {
        Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)");
        return m.Success ? m.Groups[1].Value == "true" : fallback;
    }

    public static StatusInfo GetStatus(string tvIp)
    {
        StatusInfo s = new StatusInfo();
        s.Installed = File.Exists(ConfigPath) && File.Exists(Path.Combine(InstallDir, "CouchCompanion.ps1"));
        s.PowerTask = TaskExists(PowerTaskName);
        s.HelperTask = TaskExists(HelperTaskName);
        s.ShutdownScript = ShutdownScriptRegistered();
        s.HelperRunning = Process.GetProcessesByName("CouchCompanionHelper").Length > 0;
        s.TvState = string.IsNullOrEmpty(tvIp) ? null : TvPowerState(tvIp);
        return s;
    }

    static bool TaskExists(string name)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", "/query /tn \"" + name + "\"");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return p.ExitCode == 0;
            }
        }
        catch { return false; }
    }

    static bool ShutdownScriptRegistered()
    {
        using (RegistryKey root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\Scripts\Shutdown"))
            return root != null && KeyMentions(root, "CouchCompanion");
    }

    static bool KeyMentions(RegistryKey key, string text)
    {
        object v = key.GetValue("Parameters");
        if (v != null && v.ToString().IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        foreach (string sub in key.GetSubKeyNames())
            using (RegistryKey k = key.OpenSubKey(sub))
                if (k != null && KeyMentions(k, text)) return true;
        return false;
    }

    static string TvPowerState(string ip)
    {
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://" + ip + ":8001/api/v2/");
            req.Timeout = 2500; req.ReadWriteTimeout = 2500;
            using (WebResponse resp = req.GetResponse())
            using (StreamReader r = new StreamReader(resp.GetResponseStream()))
            {
                Match m = Regex.Match(r.ReadToEnd(), "\"PowerState\"\\s*:\\s*\"(\\w+)\"");
                return m.Success ? m.Groups[1].Value : "on";
            }
        }
        catch { return null; }
    }
}
