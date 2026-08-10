using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace HaActiveUser.Agent.Location;

public sealed record PresentDevice(string InstanceId, string Description);

/// <summary>
/// Enumerates present PnP devices. A docking station or a fixed monitor is a strong signal that a
/// laptop is physically at its desk, and it survives the Wi-Fi being off.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DeviceInstanceScanner
{
    private const int DigcfPresent = 0x02;
    private const int DigcfAllClasses = 0x04;
    private const int SpdrpDeviceDesc = 0x00;
    private const int SpdrpFriendlyName = 0x0C;

    public static IReadOnlyList<PresentDevice> Enumerate()
    {
        var devices = new List<PresentDevice>();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DigcfAllClasses | DigcfPresent);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            return devices;
        }

        try
        {
            var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                var instanceId = GetInstanceId(set, ref data);
                if (instanceId is null)
                {
                    continue;
                }

                var description = GetProperty(set, ref data, SpdrpFriendlyName)
                    ?? GetProperty(set, ref data, SpdrpDeviceDesc)
                    ?? instanceId;

                devices.Add(new PresentDevice(instanceId, description));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return devices;
    }

    private static string? GetInstanceId(IntPtr set, ref SpDevInfoData data)
    {
        var buffer = new StringBuilder(1024);
        return SetupDiGetDeviceInstanceIdW(set, ref data, buffer, buffer.Capacity, out _)
            ? buffer.ToString()
            : null;
    }

    private static string? GetProperty(IntPtr set, ref SpDevInfoData data, int property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(
                set, ref data, property, out _, buffer, (uint)buffer.Length, out var required)
            || required == 0)
        {
            return null;
        }

        var value = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(required, buffer.Length)).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, StringBuilder deviceInstanceId, int size, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        int property,
        out uint propertyRegDataType,
        byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
