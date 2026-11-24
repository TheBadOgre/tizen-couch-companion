// Samsung "IP Control": JSON-RPC 2.0 over HTTPS on port 1516 ("IP Remote" enabled on the TV).
// Explicit power on/off (KEY_POWER only toggles), and it can read and set the input, which the
// remote-control keys cannot on newer models (KEY_HDMI1..4 are ignored).
// Quirks found on a QE55Q80C (2023):
//   - headers and body must go out in ONE write, and there must be NO "Connection: close" header,
//     otherwise the TV answers {"code": -32700, "message": "Parse error"} to a valid request;
//   - that same "Parse error" is the answer to a missing or rejected token;
//   - the TV keeps ONE token: pairing again (createAccessToken) invalidates the previous one;
//   - one request per connection, but an idle connection is kept open for at least 60 s.

using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

static partial class Program
{
    const int IpControlPort = 1516;

    static byte[] IpControlRequest(string method, string extraParams)
    {
        string body = "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"" + method + "\",\"params\":{\"AccessToken\":\"" +
                      ipControlToken + "\"" + (string.IsNullOrEmpty(extraParams) ? "" : "," + extraParams) + "}}";
        byte[] bb = Encoding.UTF8.GetBytes(body);
        byte[] hb = Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nHost: " + tvIp + ":" + IpControlPort +
                                            "\r\nAccept: application/json\r\nContent-Type: application/json\r\nContent-Length: " + bb.Length + "\r\n\r\n");
        byte[] all = new byte[hb.Length + bb.Length];
        Buffer.BlockCopy(hb, 0, all, 0, hb.Length);
        Buffer.BlockCopy(bb, 0, all, hb.Length, bb.Length);
        return all;
    }

    // Sends one request on a new connection; returns the response body, or null.
    // abortIf is checked right before the request is written (e.g. "too late, the PC slept meanwhile").
    static string IpControl(string method, string extraParams, int connectMs = 3000, int receiveMs = 6000, Func<bool> abortIf = null)
    {
        byte[] request = IpControlRequest(method, extraParams);
        try
        {
            using (TcpClient tcp = new TcpClient())
            {
                IAsyncResult ar = tcp.BeginConnect(tvIp, IpControlPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(connectMs) || !tcp.Connected) return null;
                tcp.EndConnect(ar);
                tcp.ReceiveTimeout = receiveMs;
                using (SslStream ssl = new SslStream(tcp.GetStream(), false, AcceptAnyCertificate))
                {
                    ssl.AuthenticateAsClient(tvIp, null, SslProtocols.Tls12, false);
                    if (abortIf != null && abortIf()) return null;
                    ssl.Write(request, 0, request.Length);
                    ssl.Flush();
                    return ReadHttpBody(ssl);
                }
            }
        }
        catch { return null; }
        finally { Trim(); }
    }

    // Reads an HTTP response until Content-Length is satisfied; returns the body.
    static string ReadHttpBody(Stream s)
    {
        MemoryStream ms = new MemoryStream();
        byte[] buf = new byte[4096];
        while (true)
        {
            int n = s.Read(buf, 0, buf.Length);
            if (n <= 0) break;
            ms.Write(buf, 0, n);
            string t = Encoding.UTF8.GetString(ms.ToArray());
            int i = t.IndexOf("\r\n\r\n");
            if (i < 0) continue;
            Match m = Regex.Match(t.Substring(0, i), "(?i)content-length:\\s*(\\d+)");
            if (m.Success && Encoding.UTF8.GetByteCount(t.Substring(i + 4)) >= int.Parse(m.Groups[1].Value)) break;
        }
        string resp = Encoding.UTF8.GetString(ms.ToArray());
        int h = resp.IndexOf("\r\n\r\n");
        return h >= 0 ? resp.Substring(h + 4) : null;
    }

    // Explicit power on/off. True if the TV answered.
    static bool IpPower(bool on)
    {
        if (!HasIpControl) return false;
        string r = IpControl("powerControl", "\"power\":\"" + (on ? "powerOn" : "powerOff") + "\"");
        return r != null && r.Contains("\"power\"");
    }

    // Current input ("HDMI1"...), or null. Also updates the TV picture (TvProbe.cs).
    static string ReadInput(out bool tokenRejected)
    {
        tokenRejected = false;
        string r = IpControl("inputSourceControl", null);
        if (r == null) return null;
        if (r.Contains("-32700")) { tokenRejected = true; return null; }
        Match m = Regex.Match(r, "\"inputSource\"\\s*:\\s*\"([^\"]+)\"");
        if (!m.Success) return null;
        string input = m.Groups[1].Value.ToUpperInvariant();
        lastKnownInput = input;
        tvInputAt.Set();
        return input;
    }

    // ------------------------------------------------------------------ armed connection for the sleep handler
    // Windows suspends ~1 s after a sleep from Steam; opening a TLS connection then is a race, and a
    // request still in flight when the PC sleeps finishes only after the resume. So keep a connection
    // open and ready (renewed every 30 s while the TV is on); at sleep only the request is written.

    const int ArmRenewSeconds = 30;
    const int ArmMaxAgeSeconds = 45;

    static readonly object armedLock = new object();
    static TcpClient armedTcp;
    static SslStream armedSsl;
    static DateTime armedAt;

    // Called by the TV probe every 3 s.
    static void KeepIpControlArmed(string tvState)
    {
        if (!HasIpControl) return;
        if (tvState != TvOn) { DisarmIpControl(); return; }
        bool needArm;
        lock (armedLock) needArm = armedSsl == null || (DateTime.UtcNow - armedAt).TotalSeconds >= ArmRenewSeconds;
        if (needArm) ArmIpControl();
    }

    static void ArmIpControl()
    {
        TcpClient tcp = new TcpClient();
        try
        {
            IAsyncResult ar = tcp.BeginConnect(tvIp, IpControlPort, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(2000) || !tcp.Connected) { tcp.Close(); return; }
            tcp.EndConnect(ar);
            tcp.ReceiveTimeout = 3000;
            SslStream ssl = new SslStream(tcp.GetStream(), false, AcceptAnyCertificate);
            ssl.AuthenticateAsClient(tvIp, null, SslProtocols.Tls12, false);
            DisarmIpControl();
            lock (armedLock) { armedTcp = tcp; armedSsl = ssl; armedAt = DateTime.UtcNow; }
        }
        catch { try { tcp.Close(); } catch { } }
    }

    static void DisarmIpControl()
    {
        lock (armedLock)
        {
            try { if (armedSsl != null) armedSsl.Close(); } catch { }
            try { if (armedTcp != null) armedTcp.Close(); } catch { }
            armedSsl = null; armedTcp = null;
        }
    }

    // Writes "powerOff" on the armed connection. True if the request left the PC.
    static bool FireArmedPowerOff()
    {
        lock (armedLock)
        {
            if (armedSsl == null || (DateTime.UtcNow - armedAt).TotalSeconds > ArmMaxAgeSeconds) return false;
            try
            {
                byte[] req = IpControlRequest("powerControl", "\"power\":\"powerOff\"");
                armedSsl.Write(req, 0, req.Length);
                armedSsl.Flush();
                return true;
            }
            catch { return false; }
            finally
            {
                // one request per connection; give it a moment to leave before closing
                SslStream s = armedSsl; TcpClient t = armedTcp;
                armedSsl = null; armedTcp = null;
                ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(500); try { s.Close(); } catch { } try { t.Close(); } catch { } });
            }
        }
    }
}
