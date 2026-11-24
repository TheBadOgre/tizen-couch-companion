// Wake-on-LAN: the fallback for a TV that has left the network (IP Control and the remote channel
// need it reachable). Sent to the limited broadcast, the subnet broadcast and the TV's address.
// 2022+ models (e.g. QE55Q80C) only wake on UDP port 40000; 9 and 7 are the classic ports.

using System.Net.Sockets;

static partial class Program
{
    static readonly int[] WolPorts = { 9, 7, 40000 };

    static byte[] MagicPacket()
    {
        byte[] packet = new byte[102];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 6; i < 102; i++) packet[i] = tvMac[(i - 6) % 6];
        return packet;
    }

    static void SendWol(UdpClient udp, byte[] packet)
    {
        string subnetBroadcast = null;
        string[] o = tvIp.Split('.');
        if (o.Length == 4) subnetBroadcast = o[0] + "." + o[1] + "." + o[2] + ".255";
        foreach (int port in WolPorts)
        {
            SendUdp(udp, packet, "255.255.255.255", port);
            if (subnetBroadcast != null) SendUdp(udp, packet, subnetBroadcast, port);
            SendUdp(udp, packet, tvIp, port);
        }
    }

    static void SendUdp(UdpClient udp, byte[] packet, string host, int port)
    {
        try { udp.Send(packet, packet.Length, host, port); } catch { }
    }
}
