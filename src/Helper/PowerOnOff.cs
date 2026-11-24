// Turning the TV on (after a resume, on input) and off (keep-off mode), and bringing it to the PC's
// input. Methods in order of preference:
//   on : IP Control "powerOn" (explicit, ~1.5 s from network standby) > KEY_POWER while the TV
//        reports standby (only if IP Control did not answer) > Wake-on-LAN (TV left the network)
//   off: IP Control "powerOff" > KEY_POWER while the TV reports on
// KEY_POWER toggles, so it is never sent next to an IP Control command that worked, and the TV state
// is checked again right before it goes out.

using System;
using System.Net.Sockets;
using System.Threading;

static partial class Program
{
    // ------------------------------------------------------------------ TV on after a resume

    // The scheduled task does this too, but starts PowerShell first and polls every 2 s; it leaves
    // the resume to the watcher while the watcher runs.
    const int ResumeWakeSeconds = 60;
    static int resumeWakeRunning;
    static readonly Stamp resumeWakeStarted = new Stamp();

    static bool ResumeWakeRunning { get { return Interlocked.CompareExchange(ref resumeWakeRunning, 0, 0) != 0; } }

    // running now, or started within the last minute (the resume signals arrive in any order)
    static bool ResumeWakeRecently() { return ResumeWakeRunning || resumeWakeStarted.Within(60); }

    static void StartResumeWake()
    {
        if (Interlocked.CompareExchange(ref resumeWakeRunning, 1, 0) != 0) return;
        resumeWakeStarted.Set();
        pendingUserResume = false;
        Thread t = new Thread(ResumeWake);
        t.IsBackground = true;
        t.Start();
    }

    static void ResumeWake()
    {
        try
        {
            DateTime t0 = DateTime.UtcNow;
            DateTime lastKey = DateTime.MinValue, lastWol = DateTime.MinValue, lastIp = DateTime.MinValue;
            int keys = 0, ipOns = 0;
            bool reached = false, ipWorks = false, wasOn = false;
            byte[] packet = MagicPacket();
            using (UdpClient udp = new UdpClient())
            {
                udp.EnableBroadcast = true;
                while ((DateTime.UtcNow - t0).TotalSeconds < ResumeWakeSeconds)
                {
                    string st = TvState();
                    if (st != TvOffline && !reached)
                    {
                        reached = true;
                        wasOn = st == TvOn;   // already on when first reached: someone was using the TV
                        Log("resume: TV reachable after " + Secs(t0) + " s (" + st + ")");
                    }
                    if (st == TvOn)
                    {
                        SetTvState(TvOn);
                        Log("resume: TV on " + Secs(t0) + " s after resume (" +
                            (wasOn ? "it was already on" : ipOns > 0 ? ipOns + "x IP Control power on" : keys + " key press" + (keys == 1 ? "" : "es")) + ")");
                        // Bring it to the PC. If it was already on, someone used it meanwhile (another
                        // input, or an app we may not recognise): switch even if it reports the PC input.
                        EnsurePcInput("resume", t0, wasOn);
                        return;
                    }
                    if (HasIpControl && (DateTime.UtcNow - lastIp).TotalSeconds >= 2)
                    {
                        lastIp = DateTime.UtcNow;
                        ipWorks = IpPower(true);
                        if (ipWorks) ipOns++;
                    }
                    // the TV reports "on" 1-2 s after an accepted press; 4 s without that = ignored
                    if (st == TvStandby && !ipWorks && (DateTime.UtcNow - lastKey).TotalSeconds >= 4)
                    {
                        lastKey = DateTime.UtcNow;
                        if (PressPowerToWake()) keys++;
                        continue;
                    }
                    if ((DateTime.UtcNow - lastWol).TotalSeconds >= 1)
                    {
                        lastWol = DateTime.UtcNow;
                        SendWol(udp, packet);
                    }
                    Thread.Sleep(250);
                }
            }
            Log("resume: TV did not turn on within 60 s (" + keys + " key presses)");
        }
        catch (Exception e) { Log("resume wake error: " + e.Message); }
        finally { Interlocked.Exchange(ref resumeWakeRunning, 0); }
    }

    // ------------------------------------------------------------------ TV on for wake on input

    static bool WakeTv(out int bursts)
    {
        bursts = 0;
        byte[] packet = MagicPacket();
        DateTime until = DateTime.UtcNow.AddSeconds(WakeTimeoutSeconds);
        DateTime lastKey = DateTime.MinValue;
        using (UdpClient udp = new UdpClient())
        {
            udp.EnableBroadcast = true;
            while (DateTime.UtcNow < until)
            {
                bool ipWorks = IpPower(true);
                if (!ipWorks && (DateTime.UtcNow - lastKey).TotalSeconds >= 10 && TvState() == TvStandby)
                {
                    lastKey = DateTime.UtcNow;
                    if (PressPowerToWake()) Log("TV in network standby - sent KEY_POWER");
                }
                SendWol(udp, packet);
                bursts++;
                Thread.Sleep(2000);
                if (TvState() == TvOn) return true;
            }
        }
        return false;
    }

    // All KEY_POWER presses meant to turn the TV ON go through here: at most one every 4 s from
    // this process, and only while the TV still reports standby (two quick presses = on and off).
    static readonly object pressLock = new object();
    static DateTime lastWakePress = DateTime.MinValue;

    static bool PressPowerToWake()
    {
        lock (pressLock)
        {
            if ((DateTime.UtcNow - lastWakePress).TotalSeconds < 4) return false;
            lastWakePress = DateTime.UtcNow;
        }
        return SendKey("KEY_POWER", 300, 300, delegate { return TvState() == TvStandby; });
    }

    // ------------------------------------------------------------------ TV off (keep off, --off)

    static bool PowerOff()
    {
        // Right after waking up the TV may take a few seconds to accept remote connections.
        DateTime until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            if (TvState() != TvOn) return true;
            bool sent = IpPower(false) ||
                        SendKey("KEY_POWER", 1000, 1500, delegate { return TvState() == TvOn; });
            if (!sent) { Thread.Sleep(2000); continue; }
            for (int i = 0; i < 6; i++)
            {
                Thread.Sleep(1000);
                if (TvState() != TvOn) return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ input

    // Switch the TV to the PC's input (config pcInput), checking first and verifying after. Right after
    // power-on the IP Control service needs a few seconds, so keep trying for a while.
    // The TV reports the last HDMI input even while an app (YouTube...) is on screen, so a known app
    // being visible also means "switch"; force = switch once anyway (the TV was in use by someone,
    // maybe in an app we do not know). Setting the input the TV already shows only flashes a banner.
    static void EnsurePcInput(string why, DateTime since, bool force = false)
    {
        if (!InputSwitching) return;
        string target = pcInput.ToUpperInvariant();
        DateTime until = DateTime.UtcNow.AddSeconds(25);
        bool switched = false;
        string last = "no answer";
        while (DateTime.UtcNow < until)
        {
            bool rejected;
            string cur = ReadInput(out rejected);
            if (rejected) { Log(why + ": IP Control token rejected - re-run the installer to pair again"); return; }
            string app = cur != null ? VisibleApp() : null;
            if (cur == target && app == "" && !(force && !switched))
            {
                lastKnownApp = "";
                if (switched) Log(why + ": input switched to " + target + " (" + Secs(since) + " s after start)");
                return;
            }
            if (cur != null && app != null)
            {
                string shows = app != "" ? "app " + app : cur;
                last = "TV on " + shows;
                Log(why + ": TV shows " + shows + (cur == target && app == "" ? " (TV was in use - making sure)" : "") + " - switching to " + target);
                IpControl("inputSourceControl", "\"inputSource\":\"" + target + "\"");
                switched = true;
                Thread.Sleep(1500);
            }
            else Thread.Sleep(1000);
        }
        Log(why + ": could not switch the input to " + target + " (" + last + ")");
    }
}
