// WindowsScreenProvider.cs
using Microsoft.Extensions.Configuration;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Thio_Universal_Agent.OS_Windows;

[SupportedOSPlatform("windows")]
public class WindowsScreenProvider(IConfiguration configuration) : IScreenProvider
{
    /// <summary>
    /// Returns the zero-based monitor index from config, or null for all-monitors mode.
    /// Read fresh on every call so that per-session changes via <c>configuration["Agent:MonitorIndex"]</c>
    /// take effect without a service restart.
    /// </summary>
    private int? SelectedMonitorIndex =>
        configuration.GetValue<int?>("Agent:MonitorIndex");

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        IntPtr previousDpiContext = NativeMethods.SetThreadDpiAwarenessContext((IntPtr)(-4));
        try
        {
            var monitors = new List<MonitorInfo>();
            int index = 0;

            NativeMethods.MonitorEnumDelegate callback = (hMonitor, _, _, _) =>
            {
                var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
                {
                    var r = info.rcMonitor;
                    bool isPrimary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
                    monitors.Add(new MonitorInfo(index++, r.left, r.top, r.right - r.left, r.bottom - r.top, isPrimary));
                }
                return true;
            };

            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            return monitors;
        }
        finally
        {
            NativeMethods.SetThreadDpiAwarenessContext(previousDpiContext);
        }
    }

    /// <summary>
    /// Resolves the capture rectangle: the specific monitor when <c>Agent:MonitorIndex</c> is set,
    /// or the full virtual screen otherwise.
    /// </summary>
    private (int x, int y, int width, int height) GetCaptureRect()
    {
        int? monitorIndex = SelectedMonitorIndex;
        if (monitorIndex.HasValue)
        {
            IReadOnlyList<MonitorInfo> monitors = GetMonitors();
            if (monitorIndex.Value >= 0 && monitorIndex.Value < monitors.Count)
            {
                MonitorInfo m = monitors[monitorIndex.Value];
                return (m.X, m.Y, m.Width, m.Height);
            }
            // Index out of range — fall through to full virtual screen
        }

        return (
            NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN)
        );
    }

    public byte[] CaptureScreen()
    {
        // Temporarily set thread to be DPI aware to get physical pixels instead of scaled logical pixels
        IntPtr previousDpiContext = NativeMethods.SetThreadDpiAwarenessContext((IntPtr)(-4));

        try
        {
            var (x, y, width, height) = GetCaptureRect();

            IntPtr hdc = NativeMethods.GetDC(IntPtr.Zero);
            try
            {
                using Bitmap bmp = new Bitmap(width, height);
                using Graphics g = Graphics.FromImage(bmp);
                IntPtr destHdc = g.GetHdc();
                try
                {
                    // Source starts at (x, y) in virtual-desktop coordinates.
                    // For full virtual screen this includes secondary monitors to the left/above.
                    // For a single monitor this is the monitor's top-left corner.
                    NativeMethods.BitBlt(destHdc, 0, 0, width, height, hdc, x, y, NativeMethods.SRCCOPY);

                    // Draw the cursor on top of the captured image
                    var cursorInfo = new NativeMethods.CURSORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.CURSORINFO>() };
                    if (NativeMethods.GetCursorInfo(ref cursorInfo) &&
                        cursorInfo.flags == NativeMethods.CURSOR_SHOWING &&
                        cursorInfo.hCursor != IntPtr.Zero)
                    {
                        // Translate from virtual-desktop coordinates to image-local coordinates
                        int cx = cursorInfo.ptScreenPos.x - x;
                        int cy = cursorInfo.ptScreenPos.y - y;
                        NativeMethods.DrawIconEx(destHdc, cx, cy, cursorInfo.hCursor,
                            0, 0, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
                    }
                }
                finally
                {
                    g.ReleaseHdc(destHdc);
                }

                using MemoryStream ms = new MemoryStream();
                // Saving as JPEG for faster web transmission, though PNG could be used for lossless
                bmp.Save(ms, ImageFormat.Jpeg);
                return ms.ToArray();
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
            }
        }
        finally
        {
            NativeMethods.SetThreadDpiAwarenessContext(previousDpiContext);
        }
    }

    /// <summary>
    /// Returns the virtual-desktop origin of the captured area so that image-pixel coordinates
    /// can be converted to absolute screen coordinates for OS input APIs.
    /// For a single-monitor capture this is the monitor's top-left position in the virtual desktop.
    /// For full virtual-screen capture this is SM_XVIRTUALSCREEN / SM_YVIRTUALSCREEN.
    /// </summary>
    public (int X, int Y) GetVirtualScreenOrigin()
    {
        IntPtr previousDpiContext = NativeMethods.SetThreadDpiAwarenessContext((IntPtr)(-4));
        try
        {
            var (x, y, _, _) = GetCaptureRect();
            return (x, y);
        }
        finally
        {
            NativeMethods.SetThreadDpiAwarenessContext(previousDpiContext);
        }
    }
}

internal static class NativeMethods
{
    internal const int SM_XVIRTUALSCREEN  = 76;
    internal const int SM_YVIRTUALSCREEN  = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;
    internal const int SRCCOPY = 0x00CC0020;
    internal const uint MONITORINFOF_PRIMARY = 0x00000001;

    internal delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(IntPtr hdc, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
        MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    internal const uint CURSOR_SHOWING = 0x00000001;
    internal const uint DI_NORMAL      = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CURSORINFO
    {
        public uint   cbSize;
        public uint   flags;
        public IntPtr hCursor;
        public POINT  ptScreenPos;
    }

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);
}