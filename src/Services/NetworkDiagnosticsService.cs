using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace xDriverUpdater.Services;

public sealed class NetworkAdapterSnapshot
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string AdapterKind { get; set; } = "Other"; // Ethernet, Wi-Fi, VPN, Other
    public string Status { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string LinkSpeed { get; set; } = "";
    public bool IsDhcpEnabled { get; set; }
    public string IpAddress { get; set; } = "";
    public string SubnetMask { get; set; } = "";
    public string Gateway { get; set; } = "";
    public List<string> DnsServers { get; set; } = new();
    public bool IsDnsDhcp { get; set; }

    public string ModeLabel => IsDhcpEnabled ? "DHCP" : "Static";
    public string DnsSummary => DnsServers.Count > 0 ? string.Join(", ", DnsServers) : "Auto";
}

public sealed class DiagnosticCheckItem
{
    public string Step { get; set; } = "";
    public string Status { get; set; } = "INFO"; // PASS, WARN, FAIL, INFO
    public string Detail { get; set; } = "";

    public string BadgeBg => Status switch
    {
        "PASS" => "#0E2918",
        "WARN" => "#2B1B0E",
        "FAIL" => "#331417",
        _ => "#222227"
    };

    public string BadgeFg => Status switch
    {
        "PASS" => "#22C55E",
        "WARN" => "#FAAD3F",
        "FAIL" => "#EF4444",
        _ => "#A1A1AA"
    };
}

public sealed class NetworkDiagnosticReport
{
    public DateTime RunAt { get; set; } = DateTime.Now;
    public bool IsOnline { get; set; }
    public string Headline { get; set; } = "";
    public string RootCause { get; set; } = "";
    public string RecoverySteps { get; set; } = "";
    public List<NetworkAdapterSnapshot> Adapters { get; set; } = new();
    public List<DiagnosticCheckItem> Checks { get; set; } = new();
}

public sealed class StaticIpProfile
{
    public string AdapterName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string SubnetMask { get; set; } = "255.255.255.0";
    public string Gateway { get; set; } = "";
    public string PrimaryDns { get; set; } = "1.1.1.1";
    public string SecondaryDns { get; set; } = "8.8.8.8";
}

public sealed class AdapterRollbackState
{
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    public List<NetworkAdapterSnapshot> SavedAdapters { get; set; } = new();
}

public sealed class NetworkDiagnosticsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public List<NetworkAdapterSnapshot> GetAdapters(bool physicalOnly = false)
    {
        var list = new List<NetworkAdapterSnapshot>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                continue;

            var desc = nic.Description ?? "";
            var name = nic.Name ?? "";

            bool isBluetooth = desc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                               name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
            bool isVirtual = desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                             desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                             desc.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                             desc.Contains("TAP-", StringComparison.OrdinalIgnoreCase) ||
                             desc.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase);
            bool isVpn = name.Contains("xPrivacyGuard", StringComparison.OrdinalIgnoreCase) ||
                         desc.Contains("xPrivacyGuard", StringComparison.OrdinalIgnoreCase) ||
                         desc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                         desc.Contains("MASQUE", StringComparison.OrdinalIgnoreCase);

            string kind;
            if (isVpn) kind = "VPN";
            else if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                     name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                     desc.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                     desc.Contains("Wireless", StringComparison.OrdinalIgnoreCase))
                kind = "Wi-Fi";
            else if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet && !isBluetooth && !isVirtual)
                kind = "Ethernet";
            else
                kind = isBluetooth ? "Bluetooth" : "Virtual";

            if (physicalOnly && kind is not ("Ethernet" or "Wi-Fi"))
                continue;

            var props = nic.GetIPProperties();
            var ipv4Props = TryGetIpv4Props(props);
            var unicast = props.UnicastAddresses
                .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            var gw = props.GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            var dnsList = props.DnsAddresses
                .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                .Select(d => d.ToString())
                .ToList();

            var snap = new NetworkAdapterSnapshot
            {
                Name = name,
                Description = desc,
                AdapterKind = kind,
                Status = nic.OperationalStatus.ToString(),
                MacAddress = FormatMac(nic.GetPhysicalAddress()),
                LinkSpeed = FormatSpeed(nic.Speed),
                IsDhcpEnabled = ipv4Props?.IsDhcpEnabled ?? true,
                IpAddress = unicast?.Address.ToString() ?? "",
                SubnetMask = unicast?.IPv4Mask?.ToString() ?? "",
                Gateway = gw?.Address.ToString() ?? "",
                DnsServers = dnsList,
                IsDnsDhcp = IsDnsDhcpConfigured(name)
            };

            list.Add(snap);
        }

        return list.OrderBy(a => a.AdapterKind switch
        {
            "Ethernet" => 0,
            "Wi-Fi" => 1,
            "VPN" => 2,
            _ => 3
        }).ThenBy(a => a.Name).ToList();
    }

    public async Task<bool> QuickInternetCheckAsync()
    {
        try
        {
            using var ping = new Ping();
            var r1 = await ping.SendPingAsync("1.1.1.1", 1500);
            if (r1.Status == IPStatus.Success) return true;
            var r2 = await ping.SendPingAsync("8.8.8.8", 1500);
            return r2.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Full offline-capable network diagnostic suite designed to pinpoint root causes without needing AI or internet access.
    /// </summary>
    public async Task<NetworkDiagnosticReport> RunOfflineDiagnosticsAsync(PrivacyGuardStatus? pgStatus = null)
    {
        return await Task.Run(async () =>
        {
            var report = new NetworkDiagnosticReport
            {
                RunAt = DateTime.Now,
                Adapters = GetAdapters(physicalOnly: false)
            };

            var phys = report.Adapters.Where(a => a.AdapterKind is "Ethernet" or "Wi-Fi").ToList();
            var upPhys = phys.Where(a => a.Status.Equals("Up", StringComparison.OrdinalIgnoreCase)).ToList();

            // 1. Physical Adapter Link Check
            if (phys.Count == 0)
            {
                report.Checks.Add(new DiagnosticCheckItem
                {
                    Step = "1. Physical Adapters",
                    Status = "FAIL",
                    Detail = "No physical Ethernet or Wi-Fi adapters detected. Check Device Manager for disabled or missing NIC drivers."
                });
            }
            else if (upPhys.Count == 0)
            {
                report.Checks.Add(new DiagnosticCheckItem
                {
                    Step = "1. Physical Adapters",
                    Status = "FAIL",
                    Detail = $"All {phys.Count} physical adapter(s) are disconnected ({string.Join(", ", phys.Select(p => $"{p.Name}={p.Status}"))})."
                });
            }
            else
            {
                bool hasApipa = upPhys.All(a => a.IpAddress.StartsWith("169.254.", StringComparison.Ordinal));
                report.Checks.Add(new DiagnosticCheckItem
                {
                    Step = "1. Physical Adapters",
                    Status = hasApipa ? "FAIL" : "PASS",
                    Detail = hasApipa
                        ? $"Link is UP on {upPhys[0].Name}, but IP is APIPA ({upPhys[0].IpAddress}) — DHCP server did not respond."
                        : string.Join(" | ", upPhys.Select(a => $"{a.Name}: {a.IpAddress} ({a.ModeLabel}, {a.LinkSpeed})"))
                });
            }

            // 2. xPrivacyGuard / Kill Switch Check
            if (pgStatus != null)
            {
                report.Checks.Add(new DiagnosticCheckItem
                {
                    Step = "2. xPrivacyGuard Status",
                    Status = pgStatus.IsProcessRunning ? (pgStatus.IsTunnelUp ? "PASS" : "WARN") : "WARN",
                    Detail = pgStatus.IsProcessRunning
                        ? (pgStatus.IsTunnelUp
                            ? $"xPrivacyGuard is running (PID {pgStatus.ProcessId}) and MASQUE tunnel is UP ({pgStatus.TunnelIp})."
                            : $"xPrivacyGuard is running (PID {pgStatus.ProcessId}) but tunnel is disconnected. If Fail-Closed Kill Switch (WFP) is enabled, it may block non-tunnel traffic.")
                        : "xPrivacyGuard is NOT running. If a stale WFP Kill Switch filter remained active, restart xPrivacyGuard or toggle its Kill Switch."
                });
            }

            // 3. Default Gateway Ping
            var gateways = upPhys.Select(a => a.Gateway).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToList();
            bool gatewayOk = false;
            if (gateways.Count == 0)
            {
                report.Checks.Add(new DiagnosticCheckItem
                {
                    Step = "3. Default Gateway",
                    Status = "FAIL",
                    Detail = "No IPv4 Default Gateway assigned. Router is unreachable without a gateway (use Static IP Troubleshooter below if DHCP failed)."
                });
            }
            else
            {
                var gw = gateways[0];
                var (ok, rtt, err) = await PingHostAsync(gw, 1800);
                gatewayOk = ok;
                report.Checks.Add(new DiagnosticCheckItem
                {
                    Step = "3. Default Gateway",
                    Status = ok ? "PASS" : "FAIL",
                    Detail = ok
                        ? $"Router/Gateway {gw} responded in {rtt} ms."
                        : $"Router/Gateway {gw} did NOT respond ({err}). Check Ethernet cable, switch/router power, or subnet mismatch."
                });
            }

            // 4. Configured DNS Server Reachability
            var dnsServers = upPhys.SelectMany(a => a.DnsServers).Distinct().ToList();
            bool dnsReachable = false;
            if (dnsServers.Count == 0)
            {
                report.Checks.Add(new DiagnosticCheckItem
                {
                    Step = "4. DNS Server Reachability",
                    Status = "WARN",
                    Detail = "No IPv4 DNS servers configured on active physical adapters."
                });
            }
            else
            {
                var dns = dnsServers[0];
                var (ok, rtt, err) = await PingHostAsync(dns, 1800);
                dnsReachable = ok;
                report.Checks.Add(new DiagnosticCheckItem
                {
                    Step = "4. DNS Server Reachability",
                    Status = ok ? "PASS" : "WARN",
                    Detail = ok
                        ? $"Primary DNS {dns} responded in {rtt} ms."
                        : $"Primary DNS {dns} did not answer ICMP ({err})."
                });
            }

            // 5. Direct Public Internet IP Ping (bypasses DNS)
            var (wan1Ok, wan1Rtt, wan1Err) = await PingHostAsync("1.1.1.1", 2000);
            var (wan2Ok, wan2Rtt, _) = wan1Ok ? (true, wan1Rtt, "") : await PingHostAsync("8.8.8.8", 2000);
            bool wanIpOk = wan1Ok || wan2Ok;
            report.Checks.Add(new DiagnosticCheckItem
            {
                Step = "5. Internet IP Routing",
                Status = wanIpOk ? "PASS" : "FAIL",
                Detail = wanIpOk
                    ? $"Direct internet IP reachable ({(wan1Ok ? $"1.1.1.1 in {wan1Rtt} ms" : $"8.8.8.8 in {wan2Rtt} ms")})."
                    : $"Cannot reach 1.1.1.1 or 8.8.8.8 ({wan1Err})."
            });

            // 6. DNS Name Resolution
            bool dnsResolveOk = false;
            try
            {
                var addrs = await Dns.GetHostAddressesAsync("one.one.one.one");
                dnsResolveOk = addrs.Length > 0;
                report.Checks.Add(new DiagnosticCheckItem
                {
                    Step = "6. DNS Name Resolution",
                    Status = dnsResolveOk ? "PASS" : "FAIL",
                    Detail = dnsResolveOk
                        ? $"Resolved one.one.one.one -> {addrs[0]}."
                        : "DNS query returned no addresses."
                });
            }
            catch (Exception ex)
            {
                report.Checks.Add(new DiagnosticCheckItem
                {
                    Step = "6. DNS Name Resolution",
                    Status = "FAIL",
                    Detail = $"DNS resolution failed: {ex.Message}"
                });
            }

            // Synthesize plain-English diagnosis & offline recovery steps
            report.IsOnline = wanIpOk && dnsResolveOk;

            if (report.IsOnline)
            {
                report.Headline = "Network Healthy — Internet & DNS Operational";
                report.RootCause = "All core layers (physical link, gateway, internet routing, and DNS resolution) passed.";
                report.RecoverySteps = "No repair needed. If a specific website or app fails, check xPrivacyGuard split-tunnel rules or local firewall rules in Live Outbound.";
            }
            else if (upPhys.Count == 0)
            {
                report.Headline = "Offline — Physical Network Disconnected";
                report.RootCause = "Neither Ethernet nor Wi-Fi has an active link.";
                report.RecoverySteps = "1) Check Ethernet cable & router LEDs or connect to Wi-Fi.\n2) Click 'Restart Network Adapters' if the NIC is stuck.\n3) Check the Drivers tab if the adapter is missing.";
            }
            else if (upPhys.All(a => a.IpAddress.StartsWith("169.254.", StringComparison.Ordinal)) || gateways.Count == 0)
            {
                report.Headline = "Offline — DHCP Failed (APIPA / No Gateway)";
                report.RootCause = "The adapter has physical link, but did not receive an IP/gateway from the router's DHCP server.";
                report.RecoverySteps = "1) Click 'Release & Renew DHCP'.\n2) If DHCP is down, click 'Apply Static IP to Both Adapters' below to regain LAN/router access.\n3) Power-cycle your router or DHCP server, then click 'Revert Both Adapters'.";
            }
            else if (!gatewayOk)
            {
                report.Headline = "Offline — Local Router / Gateway Unreachable";
                report.RootCause = $"Your PC has an IP ({upPhys[0].IpAddress}) but cannot reach gateway {gateways[0]}.";
                report.RecoverySteps = "1) If xPrivacyGuard's Fail-Closed Kill Switch is active, open xPrivacyGuard and reconnect or temporarily pause Kill Switch.\n2) Verify subnet/IP matches your router, or click 'Apply Static IP to Both Adapters' to force a known-good LAN IP.\n3) Reboot router/switch.";
            }
            else if (gatewayOk && !wanIpOk)
            {
                report.Headline = "Offline — LAN OK, Internet / WAN Unreachable";
                report.RootCause = $"Your local router ({gateways[0]}) is reachable, but traffic to public internet IPs (1.1.1.1 / 8.8.8.8) is blocked or down.";
                report.RecoverySteps = "1) Check modem/ONT WAN light (possible ISP outage).\n2) Check if xPrivacyGuard VPN tunnel is stuck connecting — reconnect or toggle it.\n3) Log into your router admin page (gateway IP) to verify WAN status.";
            }
            else if (wanIpOk && !dnsResolveOk)
            {
                report.Headline = "Degraded — Internet IP Works, DNS Resolution Failed";
                report.RootCause = "Direct IP routing works, but domain names cannot be resolved (local DNS server / AdGuard / DNS cache issue).";
                report.RecoverySteps = "1) Click 'Flush DNS Cache'.\n2) If using a local DNS server (e.g., AdGuard Home), verify it is running.\n3) Click 'Apply Static IP to Both Adapters' with 1.1.1.1 / 8.8.8.8 DNS to restore internet immediately, then revert once your DNS server is back.";
            }
            else
            {
                report.Headline = "Partial Connectivity Issue Detected";
                report.RootCause = "One or more diagnostic checks reported warnings.";
                report.RecoverySteps = "Review the failed/warning rows above and try Flush DNS or Renew DHCP.";
            }

            return report;
        });
    }

    public bool HasActiveRollback(string rollbackPath) => File.Exists(rollbackPath);

    public async Task<(bool Success, string Message)> ApplyStaticIpToBothAdaptersAsync(
        StaticIpProfile ethProfile,
        StaticIpProfile wifiProfile,
        string rollbackFilePath)
    {
        return await Task.Run(() =>
        {
            var physical = GetAdapters(physicalOnly: true);
            var eth = FindAdapter(physical, ethProfile.AdapterName, "Ethernet");
            var wifi = FindAdapter(physical, wifiProfile.AdapterName, "Wi-Fi");

            var targets = new List<(NetworkAdapterSnapshot Adapter, StaticIpProfile Profile)>();
            if (eth != null) targets.Add((eth, ethProfile));
            if (wifi != null && (eth == null || !wifi.Name.Equals(eth.Name, StringComparison.OrdinalIgnoreCase)))
                targets.Add((wifi, wifiProfile));

            if (targets.Count == 0)
                return (false, "No physical Ethernet or Wi-Fi adapters were found.");

            // Save rollback snapshot only if we don't already have an active rollback snapshot
            if (!File.Exists(rollbackFilePath))
            {
                var dir = Path.GetDirectoryName(rollbackFilePath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                var state = new AdapterRollbackState
                {
                    SavedUtc = DateTime.UtcNow,
                    SavedAdapters = targets.Select(t => t.Adapter).ToList()
                };
                File.WriteAllText(rollbackFilePath, JsonSerializer.Serialize(state, JsonOpts));
            }

            var logs = new List<string>();
            int okCount = 0;

            foreach (var (adapter, profile) in targets)
            {
                if (string.IsNullOrWhiteSpace(profile.IpAddress) || string.IsNullOrWhiteSpace(profile.SubnetMask))
                {
                    logs.Add($"{adapter.Name}: skipped (IP or subnet mask is empty).");
                    continue;
                }

                var addrArgs = string.IsNullOrWhiteSpace(profile.Gateway)
                    ? $"interface ipv4 set address name=\"{adapter.Name}\" source=static address={profile.IpAddress} mask={profile.SubnetMask} store=persistent"
                    : $"interface ipv4 set address name=\"{adapter.Name}\" source=static address={profile.IpAddress} mask={profile.SubnetMask} gateway={profile.Gateway} gwmetric=1 store=persistent";

                var (c1, o1) = RunCommand("netsh.exe", addrArgs);
                if (c1 != 0)
                {
                    logs.Add($"{adapter.Name}: failed setting IP ({o1.Trim()})");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(profile.PrimaryDns))
                {
                    RunCommand("netsh.exe", $"interface ipv4 set dnsservers name=\"{adapter.Name}\" source=static address={profile.PrimaryDns} register=primary validate=no");
                    if (!string.IsNullOrWhiteSpace(profile.SecondaryDns))
                        RunCommand("netsh.exe", $"interface ipv4 add dnsservers name=\"{adapter.Name}\" address={profile.SecondaryDns} index=2 validate=no");
                }

                okCount++;
                logs.Add($"{adapter.Name} -> {profile.IpAddress} / {profile.SubnetMask} (GW {profile.Gateway}, DNS {profile.PrimaryDns})");
            }

            return (okCount > 0, $"Applied Static IP to {okCount}/{targets.Count} adapter(s):\n" + string.Join("\n", logs));
        });
    }

    public async Task<(bool Success, string Message)> RevertBothAdaptersAsync(string rollbackFilePath)
    {
        return await Task.Run(() =>
        {
            var logs = new List<string>();
            int okCount = 0;

            if (File.Exists(rollbackFilePath))
            {
                var state = JsonSerializer.Deserialize<AdapterRollbackState>(File.ReadAllText(rollbackFilePath));
                if (state?.SavedAdapters != null && state.SavedAdapters.Count > 0)
                {
                    foreach (var saved in state.SavedAdapters)
                    {
                        if (saved.IsDhcpEnabled || string.IsNullOrWhiteSpace(saved.IpAddress) || saved.IpAddress.StartsWith("169.254.", StringComparison.Ordinal))
                        {
                            var (c1, o1) = RunCommand("netsh.exe", $"interface ipv4 set address name=\"{saved.Name}\" source=dhcp");
                            RunCommand("netsh.exe", $"interface ipv4 set dnsservers name=\"{saved.Name}\" source=dhcp");
                            if (c1 == 0)
                            {
                                okCount++;
                                logs.Add($"{saved.Name} -> Restored to DHCP (IP & DNS)");
                            }
                            else
                            {
                                logs.Add($"{saved.Name}: DHCP restore note ({o1.Trim()})");
                            }
                        }
                        else
                        {
                            var mask = string.IsNullOrWhiteSpace(saved.SubnetMask) ? "255.255.255.0" : saved.SubnetMask;
                            var addrArgs = string.IsNullOrWhiteSpace(saved.Gateway)
                                ? $"interface ipv4 set address name=\"{saved.Name}\" source=static address={saved.IpAddress} mask={mask} store=persistent"
                                : $"interface ipv4 set address name=\"{saved.Name}\" source=static address={saved.IpAddress} mask={mask} gateway={saved.Gateway} gwmetric=1 store=persistent";
                            var (c1, o1) = RunCommand("netsh.exe", addrArgs);

                            if (saved.IsDnsDhcp || saved.DnsServers.Count == 0)
                            {
                                RunCommand("netsh.exe", $"interface ipv4 set dnsservers name=\"{saved.Name}\" source=dhcp");
                            }
                            else
                            {
                                RunCommand("netsh.exe", $"interface ipv4 set dnsservers name=\"{saved.Name}\" source=static address={saved.DnsServers[0]} register=primary validate=no");
                                for (int i = 1; i < saved.DnsServers.Count; i++)
                                    RunCommand("netsh.exe", $"interface ipv4 add dnsservers name=\"{saved.Name}\" address={saved.DnsServers[i]} index={i + 1} validate=no");
                            }

                            if (c1 == 0)
                            {
                                okCount++;
                                logs.Add($"{saved.Name} -> Restored previous static IP ({saved.IpAddress})");
                            }
                            else
                            {
                                logs.Add($"{saved.Name}: restore error ({o1.Trim()})");
                            }
                        }
                    }

                    try { File.Delete(rollbackFilePath); } catch { }
                    return (okCount > 0, $"Reverted {okCount}/{state.SavedAdapters.Count} adapter(s) to saved configuration:\n" + string.Join("\n", logs));
                }
            }

            // Fallback: reset all physical adapters to DHCP
            var physical = GetAdapters(physicalOnly: true);
            foreach (var adapter in physical)
            {
                var (c1, _) = RunCommand("netsh.exe", $"interface ipv4 set address name=\"{adapter.Name}\" source=dhcp");
                RunCommand("netsh.exe", $"interface ipv4 set dnsservers name=\"{adapter.Name}\" source=dhcp");
                if (c1 == 0) okCount++;
                logs.Add($"{adapter.Name} -> Reset to DHCP");
            }
            return (okCount > 0, "Reset physical adapters to DHCP:\n" + string.Join("\n", logs));
        });
    }

    public async Task<string> FlushDnsAsync() =>
        await Task.Run(() =>
        {
            var (_, o) = RunCommand("ipconfig.exe", "/flushdns");
            return o.Trim();
        });

    public async Task<string> RenewDhcpAsync() =>
        await Task.Run(() =>
        {
            RunCommand("ipconfig.exe", "/release");
            var (_, o) = RunCommand("ipconfig.exe", "/renew", 25000);
            return string.IsNullOrWhiteSpace(o) ? "DHCP lease renewed." : o.Trim();
        });

    public async Task<string> ResetNetworkStackAsync() =>
        await Task.Run(() =>
        {
            var (_, o1) = RunCommand("netsh.exe", "winsock reset");
            var (_, o2) = RunCommand("netsh.exe", "int ip reset");
            return $"{o1.Trim()}\n{o2.Trim()}\nA reboot is recommended to complete Winsock/TCP-IP reset.".Trim();
        });

    public async Task<string> RestartPhysicalAdaptersAsync() =>
        await Task.Run(async () =>
        {
            var physical = GetAdapters(physicalOnly: true);
            var logs = new List<string>();
            foreach (var a in physical)
            {
                RunCommand("netsh.exe", $"interface set interface name=\"{a.Name}\" admin=disabled");
            }
            await Task.Delay(1500);
            foreach (var a in physical)
            {
                var (c, _) = RunCommand("netsh.exe", $"interface set interface name=\"{a.Name}\" admin=enabled");
                logs.Add($"{a.Name}: {(c == 0 ? "restarted" : "restart attempted")}");
            }
            return string.Join(" | ", logs);
        });

    private static NetworkAdapterSnapshot? FindAdapter(List<NetworkAdapterSnapshot> list, string preferredName, string kind)
    {
        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var exact = list.FirstOrDefault(a => a.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }
        return list.FirstOrDefault(a => a.AdapterKind.Equals(kind, StringComparison.OrdinalIgnoreCase));
    }

    private static IPv4InterfaceProperties? TryGetIpv4Props(IPInterfaceProperties props)
    {
        try { return props.GetIPv4Properties(); } catch { return null; }
    }

    private static bool IsDnsDhcpConfigured(string adapterName)
    {
        try
        {
            var (c, o) = RunCommand("netsh.exe", $"interface ipv4 show dnsservers name=\"{adapterName}\"", 4000);
            return c == 0 && o.Contains("DHCP", StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    private static async Task<(bool Ok, long RttMs, string Error)> PingHostAsync(string host, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs);
            return reply.Status == IPStatus.Success
                ? (true, reply.RoundtripTime, "")
                : (false, 0, reply.Status.ToString());
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    private static (int Code, string Output) RunCommand(string exe, string args, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (-1, $"Failed to start {exe}");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(timeoutMs);
            return (proc.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}");
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string FormatMac(PhysicalAddress addr)
    {
        var bytes = addr.GetAddressBytes();
        return bytes.Length == 0 ? "" : string.Join("-", bytes.Select(b => b.ToString("X2")));
    }

    private static string FormatSpeed(long bps)
    {
        if (bps <= 0) return "0 bps";
        if (bps >= 1_000_000_000) return $"{bps / 1_000_000_000.0:0.#} Gbps";
        if (bps >= 1_000_000) return $"{bps / 1_000_000.0:0.#} Mbps";
        return $"{bps / 1000.0:0.#} Kbps";
    }
}
