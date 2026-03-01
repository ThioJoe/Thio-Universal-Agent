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
        // Use safe P/Invoke to get the primary monitor's raw resolution
        int width = NativeMethods.GetSystemMetrics(0); // SM_CXSCREEN
        int height = NativeMethods.GetSystemMetrics(1); // SM_CYSCREEN

        using Bitmap bmp = new Bitmap(width, height);
        using Graphics g = Graphics.FromImage(bmp);

        // Capture the screen pixels
        g.CopyFromScreen(0, 0, 0, 0, bmp.Size);

        using MemoryStream ms = new MemoryStream();
        // Saving as JPEG for faster web transmission, though PNG could be used for lossless
        bmp.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int GetSystemMetrics(int nIndex);
}