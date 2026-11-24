// The TV's plain HTTP API on port 8001: power state, and whether a given app is on screen.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

static partial class Program
{
    const int HttpPort = 8001;
    const int HttpTimeoutMs = 1500;

    // TV power states
    const string TvOn = "on";
    const string TvStandby = "standby";   // the TV answers, screen off ("network standby")
    const string TvOffline = "offline";   // no answer at all

    // GET /api/v2/ -> device.PowerState
    static string TvState()
    {
        string text = HttpGet8001("/api/v2/");
        if (text == null) return TvOffline;
        Match m = Regex.Match(text, "\"PowerState\"\\s*:\\s*\"(\\w+)\"");
        return m.Success ? m.Groups[1].Value : TvOn;   // models without PowerState: reachable = on
    }

    // GET on port 8001; returns the body of a 200 answer, or null. Plain HTTP/1.0 over a socket:
    // loading the HttpWebRequest stack costs ~10 MB of RAM.
    static string HttpGet8001(string path)
    {
        try
        {
            using (TcpClient tcp = new TcpClient())
            {
                IAsyncResult ar = tcp.BeginConnect(tvIp, HttpPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(HttpTimeoutMs) || !tcp.Connected) return null;
                tcp.EndConnect(ar);
                tcp.ReceiveTimeout = HttpTimeoutMs;
                NetworkStream s = tcp.GetStream();
                byte[] req = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.0\r\nHost: " + tvIp + "\r\n\r\n");
                s.Write(req, 0, req.Length);
                MemoryStream body = new MemoryStream();
                byte[] buf = new byte[4096];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) body.Write(buf, 0, n);
                string text = Encoding.UTF8.GetString(body.ToArray());
                if (!text.StartsWith("HTTP/1.1 200") && !text.StartsWith("HTTP/1.0 200")) return null;
                int i = text.IndexOf("\r\n\r\n");
                return i >= 0 ? text.Substring(i + 4) : text;
            }
        }
        catch { return null; }
        finally { Trim(); }
    }

    // ------------------------------------------------------------------ apps on the TV
    // While an app (YouTube, Netflix...) is on screen, IP Control still reports the last HDMI input,
    // so "input = HDMI1" does not mean the PC is shown. The TV cannot list the foreground app, but
    // GET /api/v2/applications/<id> tells whether that one app is visible. So: check the known apps
    // installed on this TV (found once, refreshed hourly) plus any ids in config "tvApps".
    // Keep in sync with $script:TvKnownAppIds in CouchCompanion.Core.ps1.
    static readonly string[] KnownAppIds = {
        "111299001912",   // YouTube
        "3201907018807",  // Netflix
        "11101200001",    // Netflix (older)
        "3201910019365",  // Prime Video
        "3201512006785",  // Prime Video (older)
        "3201901017640",  // Disney+
        "3201606009684",  // Spotify
        "3201807016597",  // Apple TV
        "3202301029760",  // HBO Max
        "3201601007230",  // HBO Max (older)
        "3201512006963",  // Plex
        "3202010022079",  // Browser
        "org.tizen.browser"
    };
    const int AppListRefreshMinutes = 60;
    static string[] installedApps = new string[0];
    static DateTime appsFoundAt = DateTime.MinValue;

    // Name of a known app visible on the TV, "" if none, null if the TV did not answer.
    static string VisibleApp()
    {
        if (installedApps.Length == 0 || (DateTime.UtcNow - appsFoundAt).TotalMinutes > AppListRefreshMinutes) FindInstalledApps();
        bool answered = false;
        foreach (string id in installedApps)
        {
            string r = HttpGet8001("/api/v2/applications/" + id);
            if (r == null) continue;
            answered = true;
            if (Regex.IsMatch(r, "\"visible\"\\s*:\\s*true")) return AppName(r, id);
        }
        return answered ? "" : null;
    }

    static void FindInstalledApps()
    {
        List<string> found = new List<string>();
        List<string> names = new List<string>();
        List<string> candidates = new List<string>(KnownAppIds);
        foreach (string id in extraAppIds) if (!candidates.Contains(id)) candidates.Add(id);
        foreach (string id in candidates)
        {
            string r = HttpGet8001("/api/v2/applications/" + id);   // 404 = not installed
            if (r == null || found.Contains(id)) continue;
            found.Add(id);
            names.Add(AppName(r, id));
        }
        if (found.Count == 0) return;   // TV not answering: keep the old list, try again later
        installedApps = found.ToArray();
        appsFoundAt = DateTime.UtcNow;
        Log("apps checked for 'not the PC': " + string.Join(", ", names.ToArray()));
    }

    static string AppName(string appJson, string fallback)
    {
        Match m = Regex.Match(appJson, "\"name\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : fallback;
    }
}
