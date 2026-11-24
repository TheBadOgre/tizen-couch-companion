// Keeps a fresh picture of the TV - power state, input, visible app - measured every 3 s, so the
// sleep handler can decide at once (it has ~1 s before Windows suspends). The same 1 s loop keeps
// the IP Control connection armed and sends the remote channel's heartbeat.

using System;
using System.Threading;

static partial class Program
{
    const int ProbeSeconds = 3;

    // the latest picture
    static volatile string lastKnownState = "";
    static readonly Stamp tvStateAt = new Stamp();
    static volatile string lastKnownInput = "";
    static readonly Stamp tvInputAt = new Stamp();   // input and app measured together
    static volatile string lastKnownApp = "";        // "" = no known app on screen

    static bool tokenRejectLogged;

    static void SetTvState(string state)
    {
        lastKnownState = state;
        tvStateAt.Set();
    }

    static void StartTvProbe()
    {
        Thread t = new Thread(TvProbeLoop);
        t.IsBackground = true;
        t.Start();
    }

    static void TvProbeLoop()
    {
        DateTime lastProbe = DateTime.MinValue;
        while (true)
        {
            Thread.Sleep(1000);
            try
            {
                if ((KeptChannelOpen || InputSwitching) && (DateTime.UtcNow - lastProbe).TotalSeconds >= ProbeSeconds)
                {
                    lastProbe = DateTime.UtcNow;
                    ProbeTv();
                }
                HeartbeatKeptChannel();
            }
            catch (Exception e) { DropKeep("ping failed: " + e.Message); }
        }
    }

    static void ProbeTv()
    {
        // The kept remote channel survives the TV going to standby, so it does not prove the TV is on.
        string st = TvState();
        SetTvState(st);
        KeepIpControlArmed(st);
        if (!InputSwitching || st != TvOn) return;

        string inputBefore = lastKnownInput, appBefore = lastKnownApp;
        // app first, input second: ReadInput stamps the time both are valid from
        string app = VisibleApp();
        if (app != null) lastKnownApp = app;
        bool rejected;
        string input = ReadInput(out rejected);
        if (input != null && input != inputBefore) Log("TV input: " + input);
        if (app != null && app != appBefore) Log(app != "" ? "TV app on screen: " + app : "TV app closed");
        if (app == null) tvInputAt.Clear();   // app state unknown: do not trust the pair
        if (rejected && !tokenRejectLogged) { tokenRejectLogged = true; Log("IP Control token rejected - re-run the installer to pair again"); }
    }
}
