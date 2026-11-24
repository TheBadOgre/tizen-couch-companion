// CouchCompanionHelper - the Couch Companion background helper. Runs in the user's session, without a window.
//
// What it does (boot/logon are covered by the scheduled task "Couch Companion - Power On",
// shutdown by the machine shutdown script; both use CouchCompanion.ps1):
//   PC goes to sleep        TV off - but only while it shows this PC          PowerEvents.cs
//   PC woken by a person    TV on, and switched to the PC's input             PowerOnOff.cs
//   wake on input (option)  mouse/keyboard used while the TV is off -> on     MainLoop.cs
//   keep off (option)       TV came back on by itself after the remote        MainLoop.cs
//                           switched it off -> off again
//
// How it talks to the TV (LAN only):
//   :8001 HTTP        power state, which app is visible                       TvHttp.cs
//   :1516 HTTPS       "IP Control" JSON-RPC: power on/off, read/set input     IpControl.cs
//   :8002 WSS         remote-control channel: KEY_POWER (fallback)            RemoteChannel.cs
//   UDP 9/7/40000     Wake-on-LAN (fallback when the TV left the network)     WakeOnLan.cs
// TvProbe.cs keeps a fresh picture of the TV (state, input, app) for the sleep handler, which has
// no time to ask (Windows suspends ~1 s after a sleep from Steam).
//
// Compiled at install time with the C# compiler that ships with Windows (.NET Framework 4.x),
// so it is C# 5: no string interpolation, no expression-bodied members.
//
// Usage: CouchCompanionHelper.exe [path\to\config.json]          (default: config.json next to the exe)
//        CouchCompanionHelper.exe --off [path\to\config.json]    one-shot: switch the TV off and exit

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;

static partial class Program
{
    // ------------------------------------------------------------------ config (config.json)

    static string configPath;
    static DateTime configReadAt = DateTime.MinValue;
    const int ConfigRereadSeconds = 60;    // picks up a new IP / token saved by CouchCompanion.ps1

    static string tvIp = "";
    static byte[] tvMac;
    static string clientName = "Couch Companion";   // name shown on the TV for the remote channel
    static string token = "";            // remote-channel pairing token
    static string pcInput = "";          // e.g. "HDMI1": the TV input this PC is on ("" = never switch)
    static string ipControlToken = "";   // IP Control (port 1516) pairing token
    static string[] extraAppIds = new string[0];   // "tvApps": more app ids that count as "not the PC"
    static int graceSeconds = 15;        // wake on input: the TV must be off this long first
    static bool wakeOnInput = true;
    static bool keepOff = true;

    static bool InputSwitching { get { return !string.IsNullOrEmpty(pcInput) && HasIpControl; } }
    static bool HasIpControl { get { return !string.IsNullOrEmpty(ipControlToken); } }

    // ------------------------------------------------------------------ entry point

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--off")
        {
            // One-shot: switch the TV off over the network and exit (also handy for a hotkey).
            configPath = args.Length > 1 ? args[1] : DefaultConfigPath();
            logPath = Path.Combine(Path.GetTempPath(), "CouchCompanionHelper-off.log");
            if (!ReadConfig()) return 1;
            return PowerOff() ? 0 : 2;
        }

        bool first;
        using (Mutex mutex = new Mutex(true, @"Local\CouchCompanion-Helper", out first))
        {
            // One per session. The logon task also re-runs every 5 min: a second copy exits here,
            // and a watcher that died comes back.
            if (!first) return 0;

            configPath = args.Length > 0 ? args[0] : DefaultConfigPath();
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchCompanion");
            Directory.CreateDirectory(logDir);
            logPath = Path.Combine(logDir, "watcher.log");

            // An unhandled error in a background thread would otherwise end the process silently.
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log("CRASH: " + e.ExceptionObject);
            };

            if (!ReadConfig()) { Log("cannot read " + configPath); return 1; }
            Log("started, TV " + tvIp + " (wake on input: " + wakeOnInput + ", keep off: " + keepOff + ", off on sleep: True)");
            StartPowerListener();
            StartRemoteKeeper();
            StartTvProbe();
            while (true)
            {
                try { RunMainLoop(); }
                catch (Exception e) { Log("main loop error, restarting it: " + e.Message); Thread.Sleep(2000); }
            }
        }
    }

    static string DefaultConfigPath() { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"); }

    // config.json is written by CouchCompanion.ps1 (PowerShell's ConvertTo-Json); a few regexes are enough
    // and keep the watcher free of a JSON library.
    static bool ReadConfig()
    {
        configReadAt = DateTime.UtcNow;
        try
        {
            string json = File.ReadAllText(configPath);
            Match ip = Regex.Match(json, "\"ip\"\\s*:\\s*\"([^\"]*)\"");
            Match mac = Regex.Match(json, "\"mac\"\\s*:\\s*\"([0-9A-Fa-f:-]{17})\"");
            if (!ip.Success || !mac.Success) return false;
            tvIp = ip.Groups[1].Value;
            string[] parts = mac.Groups[1].Value.Split(':', '-');
            byte[] m = new byte[6];
            for (int i = 0; i < 6; i++) m[i] = Convert.ToByte(parts[i], 16);
            tvMac = m;

            Match name = Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]*)\"");
            Match tok = Regex.Match(json, "\"token\"\\s*:\\s*\"([^\"]*)\"");
            Match grace = Regex.Match(json, "\"wakeOnInputGraceSeconds\"\\s*:\\s*(\\d+)");
            Match wake = Regex.Match(json, "\"wakeOnInput\"\\s*:\\s*(true|false)");
            Match keep = Regex.Match(json, "\"keepOffAfterRemote\"\\s*:\\s*(true|false)");
            if (name.Success) clientName = name.Groups[1].Value;
            if (tok.Success) token = tok.Groups[1].Value;
            if (grace.Success) graceSeconds = int.Parse(grace.Groups[1].Value);
            if (wake.Success) wakeOnInput = wake.Groups[1].Value == "true";
            if (keep.Success) keepOff = keep.Groups[1].Value == "true";

            Match inp = Regex.Match(json, "\"pcInput\"\\s*:\\s*\"([A-Za-z0-9]*)\"");
            Match ipt = Regex.Match(json, "\"ipControlToken\"\\s*:\\s*\"([A-Za-z0-9]*)\"");
            pcInput = inp.Success ? inp.Groups[1].Value : "";
            ipControlToken = ipt.Success ? ipt.Groups[1].Value : "";

            Match apps = Regex.Match(json, "\"tvApps\"\\s*:\\s*\\[([^\\]]*)\\]");
            if (apps.Success)
            {
                List<string> ids = new List<string>();
                foreach (Match a in Regex.Matches(apps.Groups[1].Value, "\"([A-Za-z0-9._]+)\"")) ids.Add(a.Groups[1].Value);
                extraAppIds = ids.ToArray();
            }
            return true;
        }
        catch { return tvMac != null; }
    }

    // ------------------------------------------------------------------ shared helpers

    // The TV uses self-signed certificates (ports 8002 and 1516).
    static bool AcceptAnyCertificate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
    {
        return true;
    }

    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    // Give idle pages back to Windows after network work; the watcher sleeps 99.9% of the time.
    static void Trim()
    {
        SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
    }

    static string Secs(DateTime since) { return ((DateTime.UtcNow - since).TotalSeconds).ToString("0.0"); }
    static string Ms(DateTime since) { return (int)(DateTime.UtcNow - since).TotalMilliseconds + " ms"; }

    // ------------------------------------------------------------------ log

    static string logPath;
    const long LogMaxBytes = 500000;

    static void Log(string message)
    {
        try
        {
            FileInfo f = new FileInfo(logPath);
            if (f.Exists && f.Length > LogMaxBytes)
            {
                // keep one previous log instead of losing the history
                string old = Path.ChangeExtension(logPath, ".old.log");
                if (File.Exists(old)) File.Delete(old);
                File.Move(logPath, old);
            }
            File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
        }
        catch { }
    }
}

// A point in time shared between threads ("when did X last happen?"). Never set = long ago.
sealed class Stamp
{
    long ticks;   // UTC ticks, 0 = never

    public void Set() { Interlocked.Exchange(ref ticks, DateTime.UtcNow.Ticks); }
    public void Clear() { Interlocked.Exchange(ref ticks, 0); }
    public bool Within(double seconds) { return DateTime.UtcNow.Ticks - Interlocked.Read(ref ticks) < TimeSpan.FromSeconds(seconds).Ticks; }
}
