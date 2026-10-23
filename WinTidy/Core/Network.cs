using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinTidy.Core;

public record TcpConn(IPAddress LocalAddress, int LocalPort, IPAddress RemoteAddress, int RemotePort, string State, int Pid);

public class ConnRow : ObservableObject
{
    public required string Key { get; init; }
    public required string Process { get; init; }
    public required int Pid { get; init; }
    public string? ProcessPath { get; init; }
    public required string Local { get; init; }
    public required string Remote { get; init; }
    public required string RemoteIp { get; init; }
    public required string State { get; init; }
    public bool IsPublic { get; init; }
    public string Signed { get; init; } = "";

    string _host = "";
    public string RemoteHost { get => _host; set => Set(ref _host, value); }
}

public static class Network
{
    const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_ALL = 5;

    static readonly string[] States =
        ["", "Closed", "Listening", "SYN sent", "SYN received", "Established", "FIN wait 1", "FIN wait 2",
         "Close wait", "Closing", "Last ACK", "Time wait", "Delete TCB"];

    public static List<TcpConn> GetTcpConnections()
    {
        var list = new List<TcpConn>();
        Read(AF_INET, list);
        Read(AF_INET6, list);
        return list;
    }

    static void Read(int af, List<TcpConn> list)
    {
        int size = 0;
        Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size == 0) return;
        size += 4096; // the table can grow between calls
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (Native.GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return;
            int count = Marshal.ReadInt32(buf);
            IntPtr row = buf + 4;
            for (int i = 0; i < count; i++)
            {
                if (af == AF_INET)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 x uint32)
                    uint state = (uint)Marshal.ReadInt32(row);
                    var local = new IPAddress((uint)Marshal.ReadInt32(row + 4));
                    int lport = Port(Marshal.ReadInt32(row + 8));
                    var remote = new IPAddress((uint)Marshal.ReadInt32(row + 12));
                    int rport = Port(Marshal.ReadInt32(row + 16));
                    int pid = Marshal.ReadInt32(row + 20);
                    list.Add(new TcpConn(local, lport, remote, rport, StateName(state), pid));
                    row += 24;
                }
                else
                {
                    // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
                    var la = new byte[16]; Marshal.Copy(row, la, 0, 16);
                    int lport = Port(Marshal.ReadInt32(row + 20));
                    var ra = new byte[16]; Marshal.Copy(row + 24, ra, 0, 16);
                    int rport = Port(Marshal.ReadInt32(row + 44));
                    uint state = (uint)Marshal.ReadInt32(row + 48);
                    int pid = Marshal.ReadInt32(row + 52);
                    list.Add(new TcpConn(new IPAddress(la), lport, new IPAddress(ra), rport, StateName(state), pid));
                    row += 56;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
    static string StateName(uint s) => s < States.Length ? States[s] : s.ToString();

    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 127 || b[0] == 0
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // CGNAT
                     || b[0] >= 224);
        }
        var v6 = ip.GetAddressBytes();
        return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (v6[0] & 0xFE) == 0xFC);
    }

    public static string Format(IPAddress ip, int port)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
    }

    public static Dictionary<int, (string Name, string? Path)> ProcessNames()
    {
        var map = new Dictionary<int, (string, string?)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p) map[p.Id] = (p.ProcessName, Native.GetProcessPath(p.Id));
        }
        return map;
    }
}
