using System.Runtime.InteropServices;

namespace HaActiveUser.Agent.Sessions.Native;

internal static class WtsNativeMethods
{
#pragma warning disable CS0649 // marshalled structs are populated by the runtime, never by C# code
    internal const int WinStationNameLength = 32;
    internal const int UserNameLength = 20;
    internal const int DomainLength = 17;

    internal static readonly IntPtr CurrentServerHandle = IntPtr.Zero;

    internal const int WtsSessionStateLock = 0;
    internal const int WtsSessionStateUnlock = 1;

    internal enum WtsInfoClass
    {
        WtsUserName = 5,
        WtsDomainName = 7,
        WtsConnectState = 8,
        WtsSessionInfoEx = 25,
        WtsIsRemoteSession = 29
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WtsInfoExLevel1
    {
        public uint SessionId;
        public int SessionState;
        public int SessionFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = WinStationNameLength + 1)]
        public string WinStationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = UserNameLength + 1)]
        public string UserName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DomainLength + 1)]
        public string DomainName;

        public long LogonTime;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long CurrentTime;
        public uint IncomingBytes;
        public uint OutgoingBytes;
        public uint IncomingFrames;
        public uint OutgoingFrames;
        public uint IncomingCompressedBytes;
        public uint OutgoingCompressedBytes;
    }

    /// <summary>
    /// Offset of the union inside WTSINFOEXW. The DWORD Level is followed by padding because the
    /// union contains LARGE_INTEGERs and therefore aligns to 8.
    /// </summary>
    internal const int InfoExLevel1Offset = 8;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern int WTSEnumerateSessionsW(
        IntPtr hServer,
        int reserved,
        int version,
        out IntPtr ppSessionInfo,
        out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern int WTSQuerySessionInformationW(
        IntPtr hServer,
        int sessionId,
        WtsInfoClass infoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(IntPtr pMemory);
#pragma warning restore CS0649
}
