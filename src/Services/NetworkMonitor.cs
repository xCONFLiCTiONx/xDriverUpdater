using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed class NetworkMonitor
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private readonly ConcurrentDictionary<string, string> _dnsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, (string Name, string Path)> _procCache = new();

    private static readonly Dictionary<string, string> FlaggedProcessReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CompatTelRunner"] = "Windows Compatibility Telemetry Runner",
        ["DiagTrack"] = "Windows Connected User Experiences & Telemetry",
        ["DeviceCensus"] = "Windows Device Census Telemetry",
        ["SearchHost"] = "Windows Search / Bing Web Telemetry",
        ["StartMenuExperienceHost"] = "Start Menu Cloud / Web Suggestions",
        ["CrossDeviceService"] = "Windows Cross-Device / Phone Link Sync",
        ["PhoneExperienceHost"] = "Windows Phone Link Background Service",
        ["Widgets"] = "Windows Widgets / MSN Web Feed",
        ["WidgetService"] = "Windows Widgets Background Service",
        ["msedgewebview2"] = "Edge WebView2 (Search / Widgets / Embedded Web)",
        ["WerFault"] = "Windows Error Reporting Upload",
        ["wermgr"] = "Windows Error Reporting Manager",
        ["AMDRSServ"] = "AMD Radeon Software Telemetry/Service",
        ["atieclxx"] = "AMD External Events Utility",
        ["HPPrintScanDoctorService"] = "HP Print Scan Doctor Telemetry",
        ["NvTelemetryContainer"] = "NVIDIA Telemetry Container",
        ["GoogleUpdate"] = "Google Background Updater",
        ["GoogleUpdater"] = "Google Background Updater",
        ["MicrosoftEdgeUpdate"] = "Microsoft Edge Background Updater",
        ["AdobeARM"] = "Adobe Background Updater",
        ["CCXProcess"] = "Adobe Creative Cloud Telemetry/Sync",
        ["RazerCentralService"] = "Razer Telemetry / Cloud Sync"
    };

    private static readonly string[] FlaggedHostKeywords =
    [
        "telemetry", "vortex", "watson", "data.microsoft", "events.data",
        "pipe.aria", "settings-win", "diagnostics", "metrics", "analytics",
        "bing.com", "msn.com", "doubleclick", "scorecardresearch", "sentry",
        "crash", "appcenter", "nvidia", "hp.com", "adobe"
    ];

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] remoteAddr;
        public uint remoteScopeId;
        public uint remotePort;
        public uint state;
        public uint owningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        int tblClass,
        uint reserved = 0);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public List<LiveConnection> CaptureOutboundConnections(HashSet<string> blockedExePaths)
    {
        _procCache.Clear();
        var raw = new List<(int Pid, IPAddress RemoteIp, int RemotePort, string State)>();

        ReadTcpV4(raw);
        ReadTcpV6(raw);

        var grouped = raw
            .Where(r => !IsLocalOrPrivate(r.RemoteIp))
            .GroupBy(r => (r.Pid, Addr: r.RemoteIp.ToString(), r.RemotePort))
            .ToList();

        var results = new List<LiveConnection>(grouped.Count);

        foreach (var g in grouped)
        {
            var (procName, procPath) = GetProcessInfo(g.Key.Pid);
            string remoteAddr = g.Key.Addr;
            string host = GetCachedOrTriggerReverseDns(remoteAddr);

            var (flagged, reason) = ClassifyConnection(procName, procPath, host, remoteAddr);
            bool isBlocked = !string.IsNullOrWhiteSpace(procPath) && blockedExePaths.Contains(procPath);

            results.Add(new LiveConnection
            {
                Key = $"{g.Key.Pid}|{remoteAddr}|{g.Key.RemotePort}",
                ProcessName = procName,
                Pid = g.Key.Pid,
                ProcessPath = procPath,
                RemoteAddress = remoteAddr,
                RemoteHost = host,
                RemotePort = g.Key.RemotePort,
                State = g.First().State,
                ConnectionCount = g.Count(),
                IsSuspectedTelemetry = flagged,
                TelemetryReason = reason,
                IsFirewallBlocked = isBlocked
            });
        }

        return results
            .OrderByDescending(c => c.IsSuspectedTelemetry)
            .ThenBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.RemoteAddress)
            .ToList();
    }

    private static void ReadTcpV4(List<(int Pid, IPAddress RemoteIp, int RemotePort, string State)> output)
    {
        int bufferSize = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL);
        if (bufferSize <= 0) return;

        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (GetExtendedTcpTable(buffer, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL) != 0)
                return;

            int count = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);

                if (row.state is not (3 or 5)) continue;

                var ip = new IPAddress(row.remoteAddr);
                int port = (ushort)IPAddress.NetworkToHostOrder((short)(ushort)row.remotePort);
                string stateStr = row.state == 5 ? "ESTABLISHED" : "SYN_SENT";
                output.Add(((int)row.owningPid, ip, port, stateStr));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ReadTcpV6(List<(int Pid, IPAddress RemoteIp, int RemotePort, string State)> output)
    {
        int bufferSize = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL);
        if (bufferSize <= 0) return;

        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (GetExtendedTcpTable(buffer, ref bufferSize, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL) != 0)
                return;

            int count = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);

                if (row.state is not (3 or 5)) continue;

                var ip = new IPAddress(row.remoteAddr);
                int port = (ushort)IPAddress.NetworkToHostOrder((short)(ushort)row.remotePort);
                string stateStr = row.state == 5 ? "ESTABLISHED" : "SYN_SENT";
                output.Add(((int)row.owningPid, ip, port, stateStr));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private (string Name, string Path) GetProcessInfo(int pid)
    {
        return _procCache.GetOrAdd(pid, id =>
        {
            if (id <= 4) return ("System", string.Empty);
            string name = $"PID {id}";
            string path = string.Empty;
            try
            {
                using var p = Process.GetProcessById(id);
                name = p.ProcessName;
            }
            catch { }

            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, id);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    int len = sb.Capacity;
                    if (QueryFullProcessImageName(handle, 0, sb, ref len))
                        path = sb.ToString();
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            return (name, path);
        });
    }

    private string GetCachedOrTriggerReverseDns(string ip)
    {
        if (_dnsCache.TryGetValue(ip, out var cached))
            return cached;

        _dnsCache[ip] = "...";
        _ = Task.Run(async () =>
        {
            try
            {
                var entry = await Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromSeconds(2));
                _dnsCache[ip] = string.IsNullOrWhiteSpace(entry.HostName) ? ip : entry.HostName;
            }
            catch
            {
                _dnsCache[ip] = "";
            }
        });
        return "...";
    }

    private static (bool Flagged, string Reason) ClassifyConnection(string procName, string procPath, string host, string ip)
    {
        if (FlaggedProcessReasons.TryGetValue(procName, out var reason))
            return (true, reason);

        if (procName.Equals("svchost", StringComparison.OrdinalIgnoreCase))
            return (true, "Windows System Service (svchost) Outbound");

        if (!string.IsNullOrWhiteSpace(host) && host != "...")
        {
            foreach (var kw in FlaggedHostKeywords)
            {
                if (host.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return (true, $"Host matches '{kw}'");
            }
        }

        if (ip.StartsWith("204.79.197.", StringComparison.Ordinal) ||
            ip.StartsWith("13.89.179.", StringComparison.Ordinal) ||
            ip.StartsWith("20.190.", StringComparison.Ordinal))
            return (true, "Microsoft Telemetry / Bing Endpoint");

        return (false, string.Empty);
    }

    private static bool IsLocalOrPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (bytes[0] == 10) return true;
            if (bytes[0] == 127) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            return false;
        }
        else
        {
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            return false;
        }
    }
}
