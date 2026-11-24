// The TV's remote-control channel: WebSocket over TLS on port 8002, used for KEY_POWER (fallback).
// Minimal RFC 6455 client: .NET Framework's ClientWebSocket loses the TV's first frame (it arrives
// in the same TCP segment as the HTTP 101 response).
//
// One connection is kept open while the TV is on ("kept channel"): opening one takes the TV 1-4 s,
// longer than Windows waits for a sleep handler. Frames are read with NO timeout (on .NET Framework
// a timed-out SslStream read loses data and the stream goes out of sync); the heartbeat in the TV
// probe loop pings every 5 s and closes the connection when the TV stops answering.

using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;

static partial class Program
{
    const int RemotePort = 8002;
    const int HeartbeatSeconds = 5;      // we ping; the TV also pings us about every 6 s
    const int KeptChannelDeadSeconds = 20;

    // ------------------------------------------------------------------ one-off key press

    static byte[] KeyCommand(string key)
    {
        return Encoding.UTF8.GetBytes("{\"method\":\"ms.remote.control\",\"params\":{\"Cmd\":\"Click\",\"DataOfCmd\":\"" + key +
                                      "\",\"Option\":\"false\",\"TypeOfRemote\":\"SendRemoteKey\"}}");
    }

    // Opens a channel, presses the key, closes. KEY_POWER toggles, so callers pass onlyIf: the TV
    // state is checked again right before the frame goes out (connecting can take seconds, and the
    // TV may have changed state meanwhile, e.g. someone used the remote). False if not sent.
    static bool SendKey(string key, int delayBeforeMs, int delayAfterMs, Func<bool> onlyIf = null)
    {
        TcpClient tcp;
        SslStream ssl;
        if (!OpenRemote(out tcp, out ssl)) { Trim(); return false; }
        try
        {
            // A key sent right after "connect" is sometimes ignored; give the TV a moment,
            // and keep the connection open a little after sending.
            Thread.Sleep(delayBeforeMs);
            if (onlyIf != null && !onlyIf()) { Log(key + " not sent: TV state changed meanwhile"); return false; }
            WriteFrame(ssl, KeyCommand(key), 1);
            Thread.Sleep(delayAfterMs);
            try { WriteFrame(ssl, new byte[] { 0x03, 0xE8 }, 8); } catch { }   // close 1000
            return true;
        }
        catch { return false; }
        finally
        {
            try { ssl.Close(); } catch { }
            tcp.Close();
            Trim();
        }
    }

    // Connects and waits for ms.channel.connect (the TV takes 1-4 s for that last step).
    static bool OpenRemote(out TcpClient tcpOut, out SslStream sslOut)
    {
        tcpOut = null;
        sslOut = null;
        TcpClient tcp = new TcpClient();
        try
        {
            IAsyncResult ar = tcp.BeginConnect(tvIp, RemotePort, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(3000) || !tcp.Connected) return false;
            tcp.EndConnect(ar);
            tcp.ReceiveTimeout = 5000;
            SslStream ssl = new SslStream(tcp.GetStream(), false, AcceptAnyCertificate);
            ssl.AuthenticateAsClient(tvIp, null, SslProtocols.Tls12, false);

            string path = "/api/v2/channels/samsung.remote.control?name=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(clientName));
            if (token.Length > 0) path += "&token=" + token;
            string req = "GET " + path + " HTTP/1.1\r\nHost: " + tvIp + ":" + RemotePort + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                         "Sec-WebSocket-Key: " + Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + "\r\nSec-WebSocket-Version: 13\r\n\r\n";
            byte[] b = Encoding.ASCII.GetBytes(req);
            ssl.Write(b, 0, b.Length);

            // HTTP headers byte by byte, so the first WebSocket frame is not swallowed
            StringBuilder hdr = new StringBuilder();
            while (!hdr.ToString().EndsWith("\r\n\r\n"))
            {
                int c = ssl.ReadByte();
                if (c < 0) return false;
                hdr.Append((char)c);
            }
            if (!hdr.ToString().StartsWith("HTTP/1.1 101")) return false;

            for (int i = 0; i < 5; i++)
            {
                int op;
                byte[] data = ReadFrame(ssl, out op);
                if (data == null) return false;
                string msg = Encoding.UTF8.GetString(data);
                if (msg.Contains("ms.channel.unauthorized")) { Log("TV refused the connection (pairing token?)"); return false; }
                if (msg.Contains("ms.channel.connect")) break;
                if (i == 4) return false;
            }

            tcpOut = tcp;
            sslOut = ssl;
            return true;
        }
        catch { return false; }
        finally { if (tcpOut == null) tcp.Close(); }
    }

    // ------------------------------------------------------------------ kept channel

    static readonly object keepLock = new object();
    static TcpClient keepTcp;
    static SslStream keepSsl;
    static DateTime keepOpenedAt;
    static readonly Stamp keepLastFrame = new Stamp();   // last frame received from the TV
    static DateTime lastPingSent;
    static int keepAttempts;

    static bool KeptChannelOpen { get { lock (keepLock) return keepSsl != null; } }

    // Nothing received for 20 s = dead (e.g. after a resume), even though writing to it would still
    // "succeed" locally.
    static bool KeepIsFresh() { return keepLastFrame.Within(KeptChannelDeadSeconds); }

    // For the sleep handler: writes the key at once on a live kept channel. False if there is none.
    static bool SendKeyOnKeptChannel(string key)
    {
        lock (keepLock)
        {
            if (keepSsl == null || !KeepIsFresh()) return false;
            try { WriteFrame(keepSsl, KeyCommand(key), 1); return true; } catch { return false; }
        }
    }

    static void StartRemoteKeeper()
    {
        Thread t = new Thread(KeepRemote);
        t.IsBackground = true;
        t.Start();
    }

    // Opens the kept channel whenever the TV is on, and reads (and answers) its frames.
    static void KeepRemote()
    {
        DateTime nextTry = DateTime.MinValue;
        while (true)
        {
            try
            {
                SslStream ssl;
                lock (keepLock) ssl = keepSsl;
                if (ssl == null)
                {
                    if (DateTime.UtcNow < nextTry) { Thread.Sleep(250); continue; }
                    nextTry = TryOpenKeptChannel();
                    continue;
                }
                ReadKeptChannelFrame(ssl);
            }
            catch (Exception e) { DropKeep("error: " + e.Message); nextTry = DateTime.UtcNow.AddSeconds(2); }
        }
    }

    // Returns when to try again.
    static DateTime TryOpenKeptChannel()
    {
        // Check the TV ourselves every 2 s (not the main 10 s poll), and only connect once it is
        // really on: in standby it accepts the socket but never answers.
        string st = TvState();
        SetTvState(st);
        if (st != TvOn) return DateTime.UtcNow.AddSeconds(2);

        TcpClient t;
        SslStream s;
        DateTime c0 = DateTime.UtcNow;
        DateTime next = DateTime.MinValue;
        if (OpenRemote(out t, out s))
        {
            t.ReceiveTimeout = 0;   // infinite: see the header comment
            keepLastFrame.Set();
            lock (keepLock) { keepTcp = t; keepSsl = s; keepOpenedAt = DateTime.UtcNow; }
            lastPingSent = DateTime.UtcNow;
            Log("remote connection open, ready for sleep (" + (int)(DateTime.UtcNow - c0).TotalMilliseconds + " ms" +
                (keepAttempts > 0 ? ", after " + (keepAttempts + 1) + " attempts)" : ")"));
            keepAttempts = 0;
        }
        else
        {
            // Right after waking up the TV refuses remote connections for a while, and the PC may be
            // put to sleep again soon (e.g. from Steam), so retry quickly.
            keepAttempts++;
            if (keepAttempts == 1 || keepAttempts % 12 == 0) Log("remote connection not ready yet (attempt " + keepAttempts + ")");
            next = DateTime.UtcNow.AddSeconds(3);
        }
        Trim();
        return next;
    }

    static void ReadKeptChannelFrame(SslStream ssl)
    {
        int op;
        byte[] data;
        try { data = ReadFrame(ssl, out op); }
        catch (Exception e)
        {
            bool stillOurs;
            lock (keepLock) stillOurs = keepSsl == ssl;
            if (stillOurs) DropKeep("read error: " + e.Message);
            return;
        }
        if (data == null) { DropKeep("closed by the TV"); return; }
        if (op == 8) { DropKeep("TV sent close (TV turning off?)"); return; }
        keepLastFrame.Set();
        if (op == 9) { lock (keepLock) { if (keepSsl == ssl) WriteFrame(ssl, data, 10); } }   // answer the TV's ping
        else if (op > 2 && op != 10) DropKeep("unexpected frame type " + op + " (stream out of sync)");
        // 0/1/2 = data (events from the TV), 10 = pong: nothing to do
    }

    // Called every second from the TV probe loop.
    static void HeartbeatKeptChannel()
    {
        lock (keepLock)
        {
            if (keepSsl == null) return;
            if (!KeepIsFresh()) { DropKeep("no answer from the TV for " + KeptChannelDeadSeconds + " s"); return; }
            if ((DateTime.UtcNow - lastPingSent).TotalSeconds >= HeartbeatSeconds)
            {
                lastPingSent = DateTime.UtcNow;
                WriteFrame(keepSsl, new byte[] { 0x6B }, 9);
            }
        }
    }

    static void DropKeep(string reason)
    {
        lock (keepLock)
        {
            if (keepSsl != null)
                Log("remote connection dropped after " + (int)(DateTime.UtcNow - keepOpenedAt).TotalSeconds + " s: " + reason);
            try { if (keepSsl != null) keepSsl.Close(); } catch { }
            try { if (keepTcp != null) keepTcp.Close(); } catch { }
            keepSsl = null;
            keepTcp = null;
        }
    }

    // ------------------------------------------------------------------ WebSocket frames

    // Returns the payload and opcode; null = connection closed (or a garbage length).
    static byte[] ReadFrame(Stream s, out int opcode)
    {
        opcode = -1;
        byte[] h = ReadExact(s, 2);
        if (h == null) return null;
        opcode = h[0] & 0x0F;
        long len = h[1] & 0x7F;
        if (len == 126) { byte[] e = ReadExact(s, 2); if (e == null) return null; len = (e[0] << 8) | e[1]; }
        else if (len == 127) { byte[] e = ReadExact(s, 8); if (e == null) return null; len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | e[i]; }
        if (len > 1048576) return null;
        return len > 0 ? ReadExact(s, (int)len) : new byte[0];
    }

    static byte[] ReadExact(Stream s, int count)
    {
        byte[] buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n <= 0) return null;
            read += n;
        }
        return buf;
    }

    // Client frames must be masked.
    static void WriteFrame(Stream s, byte[] data, int opcode)
    {
        MemoryStream f = new MemoryStream();
        f.WriteByte((byte)(0x80 | opcode));
        if (data.Length < 126) f.WriteByte((byte)(0x80 | data.Length));
        else { f.WriteByte(0x80 | 126); f.WriteByte((byte)(data.Length >> 8)); f.WriteByte((byte)(data.Length & 0xFF)); }
        byte[] mask = Guid.NewGuid().ToByteArray();
        f.Write(mask, 0, 4);
        for (int i = 0; i < data.Length; i++) f.WriteByte((byte)(data[i] ^ mask[i % 4]));
        byte[] a = f.ToArray();
        s.Write(a, 0, a.Length);
        s.Flush();
    }
}
