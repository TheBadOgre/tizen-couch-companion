// Main loop (every 0.5 s): polls the TV state, and runs the two optional features
//   keep off       - switched off with the REMOTE, a Samsung TV drops the HDMI hot-plug line 20-35 s
//                    later; Windows and the graphics driver react, and that wakes the TV up again.
//                    If the TV comes back on by itself shortly after going off, and nobody touched
//                    the PC, switch it off again over the network (that standby does not do this).
//   wake on input  - mouse/keyboard used while the TV is off: switch it on.
// It also notices a resume whose broadcast was missed (sleep clock), and the first input after an
// automatic resume (see PowerEvents.cs).

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

static partial class Program
{
    const int LoopMs = 500;
    const int PollSeconds = 10;               // TV state check interval
    const int FastPollSeconds = 2;            // ...right after the TV went off, to catch a self-wake quickly
    const int SelfWakeWindowSeconds = 90;     // keep off: "came back on by itself" only within this time
    const int WakeTimeoutSeconds = 60;        // wake on input: how long to keep trying
    const int RetryPauseMinutes = 5;          // wake on input: pause after a failed wake
    const int DeliberateWakeSeconds = 120;    // last-wake.txt younger than this = woken on purpose

    static void RunMainLoop()
    {
        MainLoop loop = new MainLoop();
        while (true)
        {
            Thread.Sleep(LoopMs);
            loop.Tick();
        }
    }

    sealed class MainLoop
    {
        DateTime nextPoll = DateTime.MinValue;
        DateTime offSince = DateTime.MinValue;   // MinValue = TV is on (or unknown)
        DateTime pausedUntil = DateTime.MinValue;
        string lastState = "";
        bool graceLogged;
        uint lastInput = LastInputTick();
        uint inputWhenOff;                       // input tick when the TV went off
        bool keptOff;                            // already switched it off again in this cycle
        long prevSleepClock = SleepClockMs();

        bool TvIsOff { get { return offSince != DateTime.MinValue; } }

        public void Tick()
        {
            DateTime now = DateTime.UtcNow;

            DetectMissedResume();
            if (resumed)
            {
                resumed = false;
                ResetAfterResume(now);
                return;
            }

            if (now >= nextPoll) Poll(now);

            uint input = LastInputTick();
            bool touched = input != lastInput;
            lastInput = input;
            if (!touched) return;

            // after an automatic (possibly timer) resume: the first keyboard/mouse input means a
            // person is here now
            if (pendingUserResume)
            {
                Log("input after an automatic resume - a person is here, turning the TV on");
                StartResumeWake();
                return;
            }
            if (wakeOnInput) WakeOnInput(now);
        }

        // Fallback in case the resume broadcast is missed: the sleep clock moved, so the PC really
        // was asleep (a wall-clock jump or a stalled process does not move it). Whether a person is
        // there comes from RESUMESUSPEND or from input.
        void DetectMissedResume()
        {
            long sleepClock = SleepClockMs();
            long sleptMs = sleepClock - prevSleepClock;
            prevSleepClock = sleepClock;
            if (sleptMs <= 3000) return;

            Log("PC was asleep for " + (sleptMs / 1000) + " s");
            MarkResumed();
            if (!ResumeWakeRecently()) pendingUserResume = true;
        }

        // While the PC slept its own network was down, so the TV read "offline": forget that.
        void ResetAfterResume(DateTime now)
        {
            Log("PC resumed - TV state history reset");
            offSince = DateTime.MinValue;
            keptOff = false;
            graceLogged = false;
            lastInput = LastInputTick();
            lastState = "";
            nextPoll = now.AddSeconds(5);   // give the network a moment to come back
        }

        void Poll(DateTime now)
        {
            if ((now - configReadAt).TotalSeconds > ConfigRereadSeconds) ReadConfig();
            string state = TvState();
            if (state != lastState) Log("TV state: " + state);

            if (state == TvOn)
            {
                if (IsSelfWake(now))
                {
                    Log("TV came back on by itself " + (int)(now - offSince).TotalSeconds + " s after going off - switching it off over the network");
                    keptOff = true;
                    Log(PowerOff() ? "TV off (network standby, it should stay off now)" : "could not switch the TV off");
                    state = TvState();
                }
                else
                {
                    offSince = DateTime.MinValue;
                    graceLogged = false;
                    keptOff = false;
                }
            }
            else if (!TvIsOff)
            {
                offSince = now;
                inputWhenOff = LastInputTick();
            }

            lastState = state;
            SetTvState(state);
            bool recentlyOff = TvIsOff && (now - offSince).TotalSeconds < SelfWakeWindowSeconds;
            nextPoll = DateTime.UtcNow.AddSeconds(recentlyOff && keepOff && !keptOff ? FastPollSeconds : PollSeconds);
        }

        bool IsSelfWake(DateTime now)
        {
            return keepOff && !keptOff && TvIsOff &&
                   (now - offSince).TotalSeconds < SelfWakeWindowSeconds &&
                   LastInputTick() == inputWhenOff && !DeliberateWakeRecently() && !IsQuiet();
        }

        void WakeOnInput(DateTime now)
        {
            if (!TvIsOff || now < pausedUntil) return;
            if (ResumeWakeRunning) return;   // the resume wake is on it
            if ((now - offSince).TotalSeconds < graceSeconds)
            {
                // just turned off: let the user leave
                if (!graceLogged) { Log("input ignored, TV off for less than " + graceSeconds + " s"); graceLogged = true; }
                return;
            }
            if (TvState() == TvOn) { offSince = DateTime.MinValue; return; }

            Log("input while the TV is off - sending Wake-on-LAN");
            DateTime started = DateTime.UtcNow;
            int bursts;
            bool woke = WakeTv(out bursts);
            string took = ((int)(DateTime.UtcNow - started).TotalSeconds) + " s, " + bursts + " packet bursts";
            if (woke)
            {
                Log("TV on after " + took);
                lastState = TvOn; offSince = DateTime.MinValue; graceLogged = false; keptOff = false;
                EnsurePcInput("wake on input", started);
            }
            else
            {
                Log("TV did not turn on after " + took + ", pausing " + RetryPauseMinutes + " min");
                pausedUntil = DateTime.UtcNow.AddMinutes(RetryPauseMinutes);
            }
            nextPoll = DateTime.UtcNow.AddSeconds(PollSeconds);
            lastInput = LastInputTick();
        }
    }

    // CouchCompanion.ps1 / the installer touch last-wake.txt whenever they turn the TV on on purpose
    // (boot, logon, resume, power test), so that is not mistaken for the TV waking up by itself.
    static bool DeliberateWakeRecently()
    {
        try
        {
            string marker = Path.Combine(Path.GetDirectoryName(configPath), "last-wake.txt");
            return File.Exists(marker) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(marker)).TotalSeconds < DeliberateWakeSeconds;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ Windows clocks / input

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    // Keyboard/mouse only: game controllers do not count here.
    static uint LastInputTick()
    {
        LASTINPUTINFO info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        return GetLastInputInfo(ref info) ? info.dwTime : 0;
    }

    // Two clocks that differ only by the time the PC spent asleep: GetTickCount64 counts sleep,
    // QueryUnbiasedInterruptTime does not. Their difference cannot be fooled by wall-clock changes
    // (time sync, DST) or by the process being stalled, unlike DateTime gaps.
    [DllImport("kernel32.dll")]
    static extern ulong GetTickCount64();

    [DllImport("kernel32.dll")]
    static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime100ns);

    static long SleepClockMs()
    {
        ulong unbiased;
        if (!QueryUnbiasedInterruptTime(out unbiased)) return 0;
        return (long)GetTickCount64() - (long)(unbiased / 10000);   // grows only while asleep
    }
}
