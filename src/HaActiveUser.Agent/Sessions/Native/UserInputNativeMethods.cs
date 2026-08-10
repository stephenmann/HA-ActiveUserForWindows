using System.Runtime.InteropServices;

namespace HaActiveUser.Agent.Sessions.Native;

internal static class UserInputNativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    internal static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    internal const int SwHide = 0;
}
