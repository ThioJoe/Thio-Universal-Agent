using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Thio_Universal_Agent.OS_Windows;

/// <summary>
/// Provides Windows-specific system information for use in agent prompts.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsSystemProvider : ISystemProvider
{
    /// <inheritdoc/>
    public string GetOSName()
    {
        Version v = Environment.OSVersion.Version;

        // Note, these aren't all necessarily supported, but have them anyway
        return (v.Major, v.Minor, v.Build) switch
        {
            (10, 0, >= 22000) => "Windows 11",
            (10, 0, _) => "Windows 10",
            (6, 3, _) => "Windows 8.1",
            (6, 2, _) => "Windows 8",
            (6, 1, _) => "Windows 7",
            (6, 0, _) => "Windows Vista",
            (5, 2, _) => "Windows Server 2003",
            (5, 1, _) => "Windows XP",
            _ => $"Windows (NT {v.Major}.{v.Minor})"
        };
    }

    /// <inheritdoc/>
    public bool TaskFinishedNotifier(string notificationText, string notificationTitle = "")
    {
        try
        {
            // Fire and forget so we don't block the agent loop
            Task.Run(() =>
            {
                // Create a hidden, message-only STATIC window to anchor the notification
                IntPtr hwnd = NativeMethods.CreateWindowExW(
                    0, "STATIC", "TUA_Notifier", 0, 0, 0, 0, 0,
                    new IntPtr(-3), // HWND_MESSAGE
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                if (hwnd == IntPtr.Zero) return;

                // Load the default application icon
                IntPtr hIcon = NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(NativeMethods.IDI_APPLICATION));

                NativeMethods.NOTIFYICONDATA nid = new NativeMethods.NOTIFYICONDATA
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                    hWnd = hwnd,
                    uID = 1001,
                    uFlags = NativeMethods.NIF_ICON | NativeMethods.NIF_INFO | NativeMethods.NIF_TIP,
                    hIcon = hIcon,
                    szTip = "Thio Universal Agent",
                    szInfo = notificationText ?? "",
                    szInfoTitle = notificationTitle ?? "",
                    dwInfoFlags = NativeMethods.NIIF_INFO
                };

                // Add the icon to the tray and trigger the notification
                NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref nid);

                // Keep the background thread alive long enough for the toast to appear and move to the Action Center
                Thread.Sleep(7000);

                // Clean up
                NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref nid);
                NativeMethods.DestroyWindow(hwnd);
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static class NativeMethods
    {
        public const uint NIM_ADD = 0x00000000;
        public const uint NIM_DELETE = 0x00000002;

        public const uint NIF_ICON = 0x00000002;
        public const uint NIF_TIP = 0x00000004;
        public const uint NIF_INFO = 0x00000010;

        public const uint NIIF_INFO = 0x00000001;

        public const int IDI_APPLICATION = 32512;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern bool Shell_NotifyIcon(uint dwMessage, [In] ref NOTIFYICONDATA pnid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr CreateWindowExW(
            uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
    }
}