// Windows power events: the PC going to sleep (TV off) and resuming (TV on, see PowerOnOff.cs).

using System;
using System.Threading;
using System.Windows.Forms;

// Hidden top-level window: the only way for a windowless app to get WM_POWERBROADCAST.
// (Message-only windows do not receive broadcasts.)
sealed class PowerWindow : NativeWindow
{
    const int WM_POWERBROADCAST = 0x0218;
    const int PBT_APMSUSPEND = 0x0004;
    const int PBT_APMRESUMESUSPEND = 0x0007;
    const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    public PowerWindow() { CreateHandle(new CreateParams()); }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_POWERBROADCAST)
        {
            long e = m.WParam.ToInt64();
            if (e == PBT_APMSUSPEND) Program.OnSuspend();
            // RESUMEAUTOMATIC comes with every resume, including timer wake-ups (Windows Update)
            // with nobody there; RESUMESUSPEND only when a person woke the PC (or once they touch it).
            else if (e == PBT_APMRESUMEAUTOMATIC) Program.OnResume(false);
            else if (e == PBT_APMRESUMESUSPEND) Program.OnResume(true);
        }
        base.WndProc(ref m);
    }
}

static partial class Program
{
    const int SuspendDeadlineMs = 1500;   // nothing may be sent later than this after the sleep notice
    const int QuietSeconds = 180;         // keep-off mode pauses this long around sleep and resume

    static PowerWindow powerWindow;       // kept referenced so it is not collected

    static void StartPowerListener()
    {
        Thread t = new Thread(delegate()
        {
            try { powerWindow = new PowerWindow(); Application.Run(); }
            catch (Exception e) { Log("sleep listener failed: " + e.Message); }
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // ------------------------------------------------------------------ resume bookkeeping

    // While the PC sleeps its own network is down, so the TV reads "offline"; after resume it reads
    // "on" again. Keep-off mode must not take that for "the TV came back on by itself".
    static readonly Stamp quietSince = new Stamp();
    static void Quiet() { quietSince.Set(); }
    static bool IsQuiet() { return quietSince.Within(QuietSeconds); }

    static volatile bool resumed;            // tells the main loop to forget the TV state history
    static int resumeGeneration;             // +1 on every resume: a sleep handler that sees it change must stop

    // Set after a resume that may have been automatic: the TV is turned on only once a person shows
    // up (RESUMESUSPEND, or the first keyboard/mouse input as a fallback).
    static volatile bool pendingUserResume;

    static void MarkResumed()
    {
        Interlocked.Increment(ref resumeGeneration);   // a sleep handler still running must not send anything now
        DisarmIpControl();
        Quiet();
        resumed = true;
    }

    internal static void OnResume(bool byUser)
    {
        MarkResumed();
        DropKeep("PC resumed - connection from before sleep is stale");   // reconnect fresh
        if (byUser)
        {
            pendingUserResume = false;
            StartResumeWake();
        }
        else if (!pendingUserResume && !ResumeWakeRecently())
        {
            pendingUserResume = true;
            Log("PC resumed - waiting for a person before turning the TV on (could be a timer wake-up)");
        }
    }

    // ------------------------------------------------------------------ PC going to sleep: TV off

    // Windows suspends ~1 s after a sleep from Steam. Anything not sent by then must NOT be sent
    // later: the handler would carry on after the resume, and a late KEY_POWER (a toggle) once
    // switched the TV OFF right after a wake. Hence: decide from what is already known (TvProbe),
    // send over connections opened in advance, and give up after a hard deadline.
    internal static void OnSuspend()
    {
        Quiet();
        pendingUserResume = false;
        int generation = Interlocked.CompareExchange(ref resumeGeneration, 0, 0);
        DateTime t0 = DateTime.UtcNow;
        Func<bool> tooLate = delegate
        {
            return (DateTime.UtcNow - t0).TotalMilliseconds > SuspendDeadlineMs ||
                   Interlocked.CompareExchange(ref resumeGeneration, 0, 0) != generation;
        };
        try
        {
            string known = lastKnownState;
            bool stateKnown = tvStateAt.Within(6);
            if (stateKnown && known != TvOn) { Log("PC going to sleep, TV already " + known + " - nothing to do"); DisarmIpControl(); return; }

            string input;
            string other = WhatElseTvShows(out input);
            if (other != null)
            {
                Log("PC going to sleep - TV shows " + other + " - left on");
                DisarmIpControl();
                return;
            }
            string inputNote = " (input " + (input ?? "unknown") + ")";

            // 1. IP Control "powerOff" on the armed connection: explicit (cannot turn a TV on), instant.
            if (FireArmedPowerOff())
            {
                Thread.Sleep(100);   // let it leave before the network goes down
                Log("PC going to sleep - TV off via IP Control (armed connection) in " + Ms(t0) + inputNote);
                return;
            }
            // 2. KEY_POWER on the kept-open remote channel - only to a TV known to be on (it toggles).
            if (stateKnown && known == TvOn && SendKeyOnKeptChannel("KEY_POWER"))
            {
                Thread.Sleep(150);
                Log("PC going to sleep - KEY_POWER sent over kept-open connection in " + Ms(t0) + inputNote);
                return;
            }
            // 3. A new IP Control connection, with short timeouts, if there is still time.
            if (!tooLate() && HasIpControl &&
                IpControl("powerControl", "\"power\":\"powerOff\"", 400, 600, tooLate) != null)
            {
                if (tooLate()) Log("PC going to sleep - IP Control power off answered late (" + Ms(t0) + ")");
                else Log("PC going to sleep - TV off via IP Control (new connection) in " + Ms(t0) + inputNote);
                return;
            }
            Log("PC going to sleep - nothing could be sent in time (" + Ms(t0) + "); the TV stays on");
        }
        catch (Exception e) { Log("sleep handler error: " + e.Message); }
    }

    // The TV is only switched off while it shows this PC. Returns what it shows instead (for the log),
    // or null. input = the input last seen, null if not known recently.
    static string WhatElseTvShows(out string input)
    {
        input = null;
        if (!InputSwitching) return null;
        if (tvInputAt.Within(10)) input = lastKnownInput;
        if (input == null) return null;
        if (input != pcInput.ToUpperInvariant()) return input + ", not the PC (" + pcInput + ")";
        // the TV reports the last HDMI input even while an app is on screen
        string app = lastKnownApp;
        if (!string.IsNullOrEmpty(app)) return "the app " + app + ", not the PC";
        return null;
    }
}
