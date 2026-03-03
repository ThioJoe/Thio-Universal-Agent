// WindowsScreenProvider.cs
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Thio_Universal_Agent.OS_Windows;

[SupportedOSPlatform("windows")]
public class WindowsScreenProvider : IScreenProvider
{
    public byte[] CaptureScreen()
    {
        // GetDeviceCaps(DESKTOPHORZRES/DESKTOPVERTRES) returns physical pixel dimensions,
        // bypassing Windows DPI scaling so a 4K monitor always yields 3840x2160.
        IntPtr hdc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            int width = NativeMethods.GetDeviceCaps(hdc, NativeMethods.DESKTOPHORZRES);
            int height = NativeMethods.GetDeviceCaps(hdc, NativeMethods.DESKTOPVERTRES);

            using Bitmap bmp = new Bitmap(width, height);
            using Graphics g = Graphics.FromImage(bmp);
            IntPtr destHdc = g.GetHdc();
            try
            {
                NativeMethods.BitBlt(destHdc, 0, 0, width, height, hdc, 0, 0, NativeMethods.SRCCOPY);
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
}

internal static class NativeMethods
{
    internal const int DESKTOPHORZRES = 118;
    internal const int DESKTOPVERTRES = 117;
    internal const int SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(IntPtr hdc, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);
}