namespace Thio_Universal_Agent;

public interface IScreenProvider
{
    byte[] CaptureScreen();

    /// <summary>
    /// Returns the top-left corner of the virtual screen in logical screen coordinates.
    /// On a single-monitor setup this is always (0, 0). On multi-monitor setups it may be
    /// negative if a secondary monitor is positioned to the left or above the primary.
    /// This offset must be added to image-pixel coordinates before calling OS input APIs.
    /// </summary>
    (int X, int Y) GetVirtualScreenOrigin() => (0, 0);
}
