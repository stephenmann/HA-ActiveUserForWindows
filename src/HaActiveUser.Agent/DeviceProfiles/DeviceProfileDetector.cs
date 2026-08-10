using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HaActiveUser.Agent.Configuration;
using Microsoft.Extensions.Logging;

namespace HaActiveUser.Agent.DeviceProfiles;

public enum DeviceProfile
{
    Desktop,
    Laptop
}

public interface IDeviceProfileDetector
{
    DeviceProfile Detect();
}

[SupportedOSPlatform("windows")]
public sealed class DeviceProfileDetector : IDeviceProfileDetector
{
    // Win32_SystemEnclosure.ChassisTypes values that mean "this thing moves".
    private static readonly HashSet<int> PortableChassisTypes = [8, 9, 10, 11, 12, 14, 30, 31, 32];

    private readonly DeviceProfileSetting _setting;
    private readonly ILogger<DeviceProfileDetector> _logger;

    public DeviceProfileDetector(DeviceProfileSetting setting, ILogger<DeviceProfileDetector> logger)
    {
        _setting = setting;
        _logger = logger;
    }

    public DeviceProfile Detect()
    {
        if (_setting == DeviceProfileSetting.Desktop)
        {
            return DeviceProfile.Desktop;
        }

        if (_setting == DeviceProfileSetting.Laptop)
        {
            return DeviceProfile.Laptop;
        }

        var byChassis = DetectByChassis();
        if (byChassis is not null)
        {
            _logger.LogInformation("Device profile auto-detected as {Profile} from chassis type", byChassis);
            return byChassis.Value;
        }

        var byBattery = HasBattery() ? DeviceProfile.Laptop : DeviceProfile.Desktop;
        _logger.LogInformation("Device profile auto-detected as {Profile} from battery presence", byBattery);
        return byBattery;
    }

    private DeviceProfile? DetectByChassis()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ChassisTypes FROM Win32_SystemEnclosure");

            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    if (item["ChassisTypes"] is not ushort[] types)
                    {
                        continue;
                    }

                    if (types.Any(t => PortableChassisTypes.Contains(t)))
                    {
                        return DeviceProfile.Laptop;
                    }

                    if (types.Length > 0)
                    {
                        return DeviceProfile.Desktop;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Chassis lookup failed; falling back to battery detection");
        }

        return null;
    }

    private static bool HasBattery()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            return false;
        }

        const byte noSystemBattery = 128;
        const byte unknownStatus = 255;
        return status.BatteryFlag != noSystemBattery && status.BatteryFlag != unknownStatus;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
#pragma warning disable CS0649 // populated by GetSystemPowerStatus
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
#pragma warning restore CS0649
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
