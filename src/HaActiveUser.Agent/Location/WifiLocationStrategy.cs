using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using HaActiveUser.Agent.Configuration;
using Microsoft.Extensions.Logging;

namespace HaActiveUser.Agent.Location;

/// <summary>
/// Matches the currently associated access point. Works from a LocalSystem service, unlike the
/// WinRT geolocation API which requires interactive per-user consent and is far too coarse anyway.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WifiLocationStrategy : ILocationStrategy
{
    private readonly WifiMatchOptions _options;
    private readonly string _room;
    private readonly ILogger<WifiLocationStrategy> _logger;

    public WifiLocationStrategy(WifiMatchOptions options, string room, ILogger<WifiLocationStrategy> logger)
    {
        _options = options;
        _room = room;
        _logger = logger;
    }

    public string Name => "wifi";

    public LocationProbe Probe()
    {
        if (_options.Bssids.Count == 0 && _options.Ssids.Count == 0)
        {
            return LocationProbe.Indeterminate("not configured");
        }

        WifiConnection? connection;
        try
        {
            connection = WlanInterop.GetCurrentConnection();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wi-Fi query failed");
            return LocationProbe.Indeterminate("query failed");
        }

        if (connection is null)
        {
            return LocationProbe.Indeterminate("no wireless association");
        }

        var bssidMatch = _options.Bssids.Any(b => MacFormat.Equal(b, connection.Bssid));
        var ssidMatch = _options.Ssids.Any(s => string.Equals(s.Trim(), connection.Ssid, StringComparison.OrdinalIgnoreCase));

        var detail = $"ssid={connection.Ssid} bssid={connection.Bssid}";
        return bssidMatch || ssidMatch
            ? new LocationProbe(true, detail, _room)
            : new LocationProbe(false, detail);
    }
}

internal sealed record WifiConnection(string Ssid, string Bssid);

[SupportedOSPlatform("windows")]
internal static class WlanInterop
{
#pragma warning disable CS0649 // marshalled structs are populated by the runtime, never by C# code
    private const int ClientVersion = 2;
    private const int OpcodeCurrentConnection = 7;
    private const int InterfaceStateConnected = 6;
    private const int InterfaceInfoListHeaderSize = 8;

    public static WifiConnection? GetCurrentConnection()
    {
        if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var client) != 0)
        {
            return null;
        }

        var interfaceList = IntPtr.Zero;
        try
        {
            if (WlanEnumInterfaces(client, IntPtr.Zero, out interfaceList) != 0)
            {
                return null;
            }

            var count = Marshal.ReadInt32(interfaceList);
            var entrySize = Marshal.SizeOf<WlanInterfaceInfo>();

            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfo>(
                    interfaceList + InterfaceInfoListHeaderSize + (i * entrySize));

                if (info.State != InterfaceStateConnected)
                {
                    continue;
                }

                var connection = QueryConnection(client, info.InterfaceGuid);
                if (connection is not null)
                {
                    return connection;
                }
            }
        }
        finally
        {
            if (interfaceList != IntPtr.Zero)
            {
                WlanFreeMemory(interfaceList);
            }

            WlanCloseHandle(client, IntPtr.Zero);
        }

        return null;
    }

    private static WifiConnection? QueryConnection(IntPtr client, Guid interfaceGuid)
    {
        var data = IntPtr.Zero;
        try
        {
            if (WlanQueryInterface(
                    client, ref interfaceGuid, OpcodeCurrentConnection, IntPtr.Zero,
                    out _, out data, IntPtr.Zero) != 0)
            {
                return null;
            }

            var attributes = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
            var association = attributes.Association;

            var ssidLength = (int)Math.Min(association.SsidLength, 32);
            var ssid = Encoding.UTF8.GetString(association.Ssid, 0, ssidLength);

            return new WifiConnection(ssid, MacFormat.Normalise(association.Bssid));
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                WlanFreeMemory(data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;

        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAssociationAttributes
    {
        public uint SsidLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Ssid;

        public int BssType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] Bssid;

        public int PhyType;
        public uint PhyIndex;
        public uint SignalQuality;
        public uint RxRate;
        public uint TxRate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionAttributes
    {
        public int State;
        public int ConnectionMode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;

        public WlanAssociationAttributes Association;
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(
        int clientVersion, IntPtr reserved, out int negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        int opCode,
        IntPtr reserved,
        out int dataSize,
        out IntPtr data,
        IntPtr opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
#pragma warning restore CS0649
}
