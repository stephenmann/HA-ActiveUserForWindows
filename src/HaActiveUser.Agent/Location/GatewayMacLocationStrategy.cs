using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace HaActiveUser.Agent.Location;

/// <summary>
/// ARPs the default gateway and compares its MAC. Cheap, wired-friendly, and unlike the gateway IP
/// it cannot be spoofed by another network handing out the same RFC1918 range.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GatewayMacLocationStrategy : ILocationStrategy
{
    private readonly IReadOnlyList<string> _expectedMacs;
    private readonly string _room;
    private readonly ILogger<GatewayMacLocationStrategy> _logger;

    public GatewayMacLocationStrategy(
        IReadOnlyList<string> expectedMacs, string room, ILogger<GatewayMacLocationStrategy> logger)
    {
        _expectedMacs = expectedMacs;
        _room = room;
        _logger = logger;
    }

    public string Name => "gateway";

    public LocationProbe Probe()
    {
        if (_expectedMacs.Count == 0)
        {
            return LocationProbe.Indeterminate("not configured");
        }

        var observed = GetGatewayMacs().ToList();
        if (observed.Count == 0)
        {
            return LocationProbe.Indeterminate("no gateway reachable");
        }

        var detail = string.Join(",", observed);
        return observed.Any(mac => _expectedMacs.Any(expected => MacFormat.Equal(expected, mac)))
            ? new LocationProbe(true, detail, _room)
            : new LocationProbe(false, detail);
    }

    public IEnumerable<string> GetGatewayMacs()
    {
        foreach (var gateway in GetGatewayAddresses())
        {
            string? mac = null;
            try
            {
                mac = ResolveMac(gateway);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ARP for {Gateway} failed", gateway);
            }

            if (mac is not null)
            {
                yield return mac;
            }
        }
    }

    /// <summary>
    /// VPN and hypervisor adapters advertise gateways too; including them would make the agent
    /// think it was home whenever a tunnel came up.
    /// </summary>
    public static IEnumerable<IPAddress> GetGatewayAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
            {
                continue;
            }

            if (IsVirtual(nic))
            {
                continue;
            }

            foreach (var gateway in nic.GetIPProperties().GatewayAddresses)
            {
                if (gateway.Address.AddressFamily == AddressFamily.InterNetwork
                    && !gateway.Address.Equals(IPAddress.Any))
                {
                    yield return gateway.Address;
                }
            }
        }
    }

    private static bool IsVirtual(NetworkInterface nic)
    {
        string[] markers = ["virtual", "vmware", "hyper-v", "vbox", "tap-", "tunnel", "loopback", "wan miniport", "zerotier", "tailscale", "wireguard", "openvpn"];
        var description = nic.Description.ToLowerInvariant();
        var name = nic.Name.ToLowerInvariant();
        return markers.Any(m => description.Contains(m, StringComparison.Ordinal) || name.Contains(m, StringComparison.Ordinal));
    }

    private static string? ResolveMac(IPAddress gateway)
    {
        var destination = BitConverter.ToUInt32(gateway.GetAddressBytes(), 0);
        var mac = new byte[6];
        var length = (uint)mac.Length;

        return SendARP(destination, 0, mac, ref length) == 0 && length >= 6
            ? MacFormat.Normalise(mac)
            : null;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physicalAddrLength);
}
