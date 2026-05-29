// WindowsScreenProvider.cs
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Thio_Universal_Agent.OS_Windows;

[SupportedOSPlatform("windows")]
public class WindowsScreenProvider(AppConfig appConfig) : IScreenProvider
{
    /// <summary>
    /// Returns the zero-based monitor index from config, or null for all-monitors mode.
    /// Read fresh on every call so that per-session changes to <see cref="AgentConfig.MonitorIndex"/>
    /// take effect without a service restart.
    /// </summary>
    private int? SelectedMonitorIndex => appConfig.Agent.MonitorIndex;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        IntPtr previousDpiContext = NativeMethods.SetThreadDpiAwarenessContext(-4);
        try
        {
            List<MonitorInfo> monitors = new List<MonitorInfo>();
            int index = 0;

            NativeMethods.MonitorEnumDelegate callback = (hMonitor, _, _, _) =>
            {
                NativeMethods.MONITORINFO info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
                {
                    NativeMethods.RECT r = info.rcMonitor;
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

    public Screenshot CaptureScreen()
    {
        // Temporarily set thread to be DPI aware to get physical pixels instead of scaled logical pixels
        IntPtr previousDpiContext = NativeMethods.SetThreadDpiAwarenessContext(-4);

        try
        {
            // Single GetCaptureRect call so the screenshot bytes and origin are guaranteed
            // to correspond to the same monitor enumeration result.
            (int x, int y, int width, int height) = GetCaptureRect();

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
                    NativeMethods.CURSORINFO cursorInfo = new NativeMethods.CURSORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.CURSORINFO>() };
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
                return new Screenshot(ms.ToArray(), x, y, width, height);
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
        IntPtr previousDpiContext = NativeMethods.SetThreadDpiAwarenessContext(-4);
        try
        {
            (int x, int y, int _, int _) = GetCaptureRect();
            return (x, y);
        }
        finally
        {
            NativeMethods.SetThreadDpiAwarenessContext(previousDpiContext);
        }
    }

    private enum MarkerType
    {
        ClickPoint,
        ClickDrag,
        BoundingBox,
        MouseMove
    }

    private readonly MarkerPool _markerPool = new();

    // Created on first interactive DrawClickPointMarker call; lives for the lifetime of the provider.
    private RawInputSink? _rawInputSink;

    /// <summary>
    /// When set, each <c>Draw*</c> call stamps this value onto the marker's <c>QueueLabel</c> so the
    /// human operator can see the execution order of a queued batch. Set to <c>null</c> to suppress.
    /// </summary>
    public string? CurrentQueueLabel { get; set; }

    /// <summary>
    /// Manages the lifecycle of <see cref="MarkerWindow"/> instances: idle pool, active-marker tracking,
    /// latest-marker reference, and ID generation — all in one place.
    /// </summary>
    private sealed class MarkerPool
    {
        private readonly ConcurrentDictionary<Type, ConcurrentBag<MarkerWindow>> _idle = new();
        private readonly ConcurrentDictionary<int, MarkerWindow> _active = new();
        private volatile MarkerWindow? _latest;
        private int _idCounter;

        /// <summary>The most recently activated marker, or <c>null</c> if none has been drawn yet.</summary>
        public MarkerWindow? Latest => _latest;

        //TODO: Remove and TryDeactivate seem they could be consolidated, their descriptions suggest they do something similar
        /// <summary>Returns a hidden window to the idle pool for reuse by the next draw call.</summary>
        public void Remove(MarkerWindow window) => _idle.GetOrAdd(window.GetType(), _ => new ConcurrentBag<MarkerWindow>()).Add(window);

        /// <summary>Removes the marker with <paramref name="id"/> from the active set. Returns <c>true</c> and populates <paramref name="window"/> if found.</summary>
        public bool TryDeactivate(int id, out MarkerWindow window) => _active.TryRemove(id, out window!);

        /// <summary>A snapshot of all currently active marker IDs, suitable for iterating a clear-all operation.</summary>
        public ICollection<int> ActiveIds => _active.Keys;

        /// <summary>Hides all currently visible markers, cancels their auto-hide timers, and returns the windows to the idle pool.</summary>
        public void ClearAll(MarkerType? onlyType = null)
        {
            foreach (var (_, window) in _active.ToArray())
            {
                if (onlyType == null || window.Type == onlyType)
                    window.Clear(this);
            }
        }

        /// <summary>Hides only the most recently drawn marker and cancels its auto-hide timer. Has no effect if no markers are currently active.</summary>
        /// <param name="onlyType">If specified, only clears the latest marker of this type; otherwise, clears the latest marker of any type.</param>
        public void ClearLatest(MarkerType? onlyType = null)
        {
            if (onlyType != null)
            {
                GetLatestOfType(onlyType)?.Clear(this);
            }
            else
            {
                _latest?.Clear(this);
            }
        } 

        /// <summary>
        /// Rents an idle window of type <typeparamref name="T"/> (or creates a new one), assigns it a
        /// fresh unique ID, registers it as active, and records it as the latest marker.
        /// </summary>
        /// <returns>The activated window, with <see cref="MarkerWindow.Id"/> set to its assigned ID.</returns>
        public T Rent<T>(CancellationTokenSource cts) where T : MarkerWindow, new()
        {
            ConcurrentBag<MarkerWindow> bucket = _idle.GetOrAdd(typeof(T), _ => new ConcurrentBag<MarkerWindow>());
            T window = bucket.TryTake(out MarkerWindow? w) ? (T)w : new T();
            window.Cts = cts;
            int id = Interlocked.Increment(ref _idCounter);
            window.Id = id;
            _active[id] = window;
            _latest = window;
            return window;
        }

        /// <summary>
        /// Retrieves the most recent marker window, optionally filtered by type.
        /// </summary>
        /// <param name="type">The marker type to filter by, or <see langword="null"/> to return the overall latest window.</param>
        /// <returns>The most recent marker window of the specified type, or the overall latest if <paramref name="type"/> is <see langword="null"/>;.
        ///     <see langword="null"/> if no matching window is found.</returns>
        private MarkerWindow? GetLatestOfType(MarkerType? type)
        {
            MarkerWindow? latest = _latest;
            if (type == null)
            {
                return latest;
            }
            else
            {
                // Loop through the active marker windows and find the most recent one of the specified type
                foreach (var (_, window) in _active.OrderByDescending(kv => kv.Key))
                {
                    if (window.Type == type)
                        return window;
                }
                // If none found
                return null;
            }
        }
    }

    /// <summary>
    /// Displays a crosshair marker at the given screen coordinates.
    /// Each call shows an independent marker, so multiple markers can be visible simultaneously.
    /// The design of the marker is auto-created upon renting. 
    /// </summary>
    /// <param name="x">Absolute screen X coordinate (physical pixels) for the marker centre.</param>
    /// <param name="y">Absolute screen Y coordinate (physical pixels) for the marker centre.</param>
    /// <param name="durationMs">How long the marker stays visible in milliseconds. Pass <c>0</c> to keep it visible until <see cref="ClearAllMarkers"/> is called.</param>
    /// <param name="markerOpacity">Opacity of the marker from 0 (invisible) to 255 (fully opaque).</param>
    /// <returns><c>true</c> on success.</returns>
    public void DrawClickPointMarker(int x, int y, int durationMs, int markerOpacity = 255, string? label = null, Action? onClicked = null)
    {
        ClickPointMarker window = _markerPool.Rent<ClickPointMarker>(new CancellationTokenSource());
        window.Label = label;
        window.QueueLabel = CurrentQueueLabel;

        if (onClicked != null)
        {
            _rawInputSink ??= new RawInputSink();
            int markerId = window.Id;
            // Register before Show() so no click event is missed.
            _rawInputSink.Register(markerId, x, y, () =>
            {
                window.Clear(_markerPool);
                onClicked();
            });
            // Unregister if the marker is cleared externally (timeout / ClearMarkers) before it is clicked.
            window.OnCleared = () => _rawInputSink.Unregister(markerId);
        }

        window.Show(x, y, markerOpacity, durationMs, _markerPool);
    }

    public void DrawBoundingBox(int x1, int y1, int x2, int y2, int durationMs, int borderOpacity = 255, int fillOpacity = 0, string? label = null)
    {
        BoundingBoxMarker window = _markerPool.Rent<BoundingBoxMarker>(new CancellationTokenSource());
        window.Label = label;
        window.QueueLabel = CurrentQueueLabel;
        window.Show(x1, y1, x2, y2, borderOpacity, fillOpacity, durationMs, _markerPool);
    }

    public void DrawClickDragMarker(int x_start, int y_start, int x_end, int y_end, int durationMs, int markerOpacity = 255, string? label = null)
    {
        ClickDragMarker window = _markerPool.Rent<ClickDragMarker>(new CancellationTokenSource());
        window.Label = label;
        window.QueueLabel = CurrentQueueLabel;
        window.Show(x_start, y_start, x_end, y_end, markerOpacity, durationMs, _markerPool);
    }

    /// <summary>
    /// Displays a single-point move marker (open crosshair, no arrow) at <paramref name="x"/>, <paramref name="y"/>.
    /// Intended for human-control mode where there is no meaningful start position.
    /// </summary>
    public void DrawMouseMoveMarker(int x, int y, int durationMs, int markerOpacity = 255, string? label = null)
    {
        MouseMoveDestinationMarker window = _markerPool.Rent<MouseMoveDestinationMarker>(new CancellationTokenSource());
        window.Label = label;
        window.QueueLabel = CurrentQueueLabel;
        window.Show(x, y, x, y, markerOpacity, durationMs, _markerPool);
    }

    /// <summary>
    /// Displays a single-point move arrow marker at <paramref name="x"/>, <paramref name="y"/>.
    /// Intended for autonomous mode to visualise where the cursor just moved.
    /// </summary>
    public void DrawMouseMoveArrow(int x, int y, int durationMs, int markerOpacity = 255)
    {
        MouseMoveDestinationMarker window = _markerPool.Rent<MouseMoveDestinationMarker>(new CancellationTokenSource());
        window.Show(x, y, x, y, markerOpacity, durationMs, _markerPool);
    }

    /// <summary>
    /// Hides all currently visible markers
    /// Released marker windows are returned to the pool for reuse.
    /// </summary>
    public void ClearMarkers() => _markerPool.ClearAll();

    /// <summary>
    /// Hides only the most recently drawn marker and cancels its auto-hide timer.
    /// Has no effect if no markers are currently active.
    /// </summary>
    public void ClearLatestMarker() => _markerPool.ClearLatest();

    private abstract class MarkerWindow
    {
        private static int _instanceCounter;
        private string _wndClassName = "";

        private IntPtr _hwnd;
        private IntPtr _magentaBrush;
        private readonly Thread _messageThread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly NativeMethods.WndProcDelegate _wndProcDelegate;

        protected readonly object StateLock = new();
        private int _x, _y, _opacity;

        /// <summary>The unique ID assigned to this window by the provider for the current draw call. Reset on each reuse.</summary>
        public int Id { get; set; }
        /// <summary>The type of the marker. Implemented by each subclass as a constant value.</summary>
        public abstract MarkerType Type { get; }
        /// <summary>
        /// The window size and anchor offset for this marker.
        /// The offset is subtracted from the target coordinate so the visual centre lands on that point.
        /// Can be overridden per-instance to customise the size of an individual marker.
        /// </summary>
        public abstract (int width, int height, int offsetX, int offsetY) Geometry { get; set; }
        /// <summary>Optional text drawn near the marker's anchor point. Set before each call to <c>Show</c>; <c>null</c> suppresses the label.</summary>
        internal string? Label;
        /// <summary>Optional queue order number (e.g. "1", "2") drawn near the marker when it is part of a batch. Set before each call to <c>Show</c>; <c>null</c> suppresses it.</summary>
        internal string? QueueLabel;
        /// <summary>The cancellation source for this window's active auto-hide timer. Set on activation, disposed and nulled on clear.</summary>
        public CancellationTokenSource? Cts { get; set; }

        /// <summary>Optional callback invoked once when <see cref="Clear"/> successfully deactivates this marker (either from timeout or an explicit clear). Reset to <c>null</c> on reuse.</summary>
        internal Action? OnCleared { get; set; }

        protected MarkerWindow()
        {
            _wndProcDelegate = WndProc;
            _messageThread = new Thread(MessagePump) { IsBackground = true, Name = "MarkerWindowPump" };
            _messageThread.SetApartmentState(ApartmentState.STA);
            _messageThread.Start();
            _ready.Wait(); // Block until the HWND is created
        }

        /// <summary>Displays the marker at the specified position with the given opacity and optionally clears it after a duration. </summary>
        /// <remarks>
        ///     - The display operation is thread-safe. If <paramref name="durationMs"/> is greater than zero,
        ///     the marker is automatically cleared asynchronously after the specified duration unless cancelled externally.
        ///     - This method should be used if the marker has already been drawn but just isn't visible.
        /// </remarks>
        /// <param name="x">The horizontal coordinate for the marker position.</param>
        /// <param name="y">The vertical coordinate for the marker position.</param>
        /// <param name="opacity">The opacity level for the marker display.</param>
        /// <param name="durationMs">The duration in milliseconds before automatically clearing the marker. If zero or negative, the marker
        /// persists until manually cleared.</param>
        /// <param name="pool">The marker pool to return this marker to when clearing after the duration expires.</param>
        public void Show(int x, int y, int opacity, int durationMs, MarkerPool pool)
        {
            lock (StateLock)
            {
                _x = x; _y = y; _opacity = opacity;
            }
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);

            if (durationMs > 0)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(durationMs, Cts!.Token);
                        Clear(pool);
                    }
                    catch (OperationCanceledException)
                    {
                        // Cleared externally; window is already handled there
                        // TODO: Decide whether to put debug level trace here
                    }
                });
            }
        }

        /// <summary>Called just before the window is hidden. Override in subclasses to hide companion windows.</summary>
        protected virtual void OnBeforeHide() { }

        private void Hide()
        {
            OnBeforeHide();
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_APP_HIDE, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Removes this window from the pool's active set, cancels its auto-hide timer, hides it,
        /// and returns it to the idle pool. Safe to call concurrently; does nothing if already deactivated.
        /// </summary>
        public void Clear(MarkerPool pool)
        {
            if (pool.TryDeactivate(Id, out _))
            {
                Cts?.Cancel();
                Cts?.Dispose();
                Cts = null;
                Action? cleared = OnCleared;
                OnCleared = null;
                Hide();
                pool.Remove(this);
                cleared?.Invoke();
            }
        }

        /// <summary>Performs the GDI drawing for this marker type onto <paramref name="g"/>.</summary>
        protected abstract void Paint(Graphics g);

        private void MessagePump()
        {
            // CRITICAL: Make this thread Per-Monitor DPI Aware V2 so SetWindowPos uses raw physical pixels
            //TODO: Make enum, see: https://learn.microsoft.com/en-us/windows/win32/hidpi/dpi-awareness-context
            NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(-4));

            // Each instance uses a unique class name so its own WndProc delegate is always the one
            // registered for its HWND. Without this, all instances share the first registration's
            // WndProc (Win32 class WndProcs are per-class, not per-HWND), causing wrong paint/state
            // to be read when multiple marker types are visible simultaneously.
            _wndClassName = $"TUA_MarkerWindow_{Interlocked.Increment(ref _instanceCounter)}";

            // Use Magenta as the background brush so we can Color-Key it out to be perfectly transparent
            _magentaBrush = NativeMethods.CreateSolidBrush(0x00FF00FF);

            NativeMethods.WNDCLASSEX wc = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                lpszClassName = _wndClassName,
                hbrBackground = _magentaBrush
            };
            NativeMethods.RegisterClassExW(ref wc);

            // Layered = transparency. Transparent = mouse clicks pass right through it. Topmost = stays on top.
            uint dwExStyle = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW;

            (int w, int h, _, _) = Geometry;
            _hwnd = NativeMethods.CreateWindowExW(
                dwExStyle: dwExStyle,
                lpClassName: _wndClassName,
                lpWindowName: "TUA Marker",
                dwStyle: NativeMethods.WS_POPUP,
                x: 0, y: 0, nWidth: w, nHeight: h,
                hWndParent: IntPtr.Zero, hMenu: IntPtr.Zero, hInstance: IntPtr.Zero, lpParam: IntPtr.Zero
            );

            _ready.Set();

            while (NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }

            NativeMethods.DestroyWindow(_hwnd);
            NativeMethods.DeleteObject(_magentaBrush);
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case NativeMethods.WM_APP_SHOW:
                    int x, y, op;
                    lock (StateLock) { x = _x; y = _y; op = _opacity; }

                    // Make magenta fully transparent, and apply the master opacity to the drawings
                    NativeMethods.SetLayeredWindowAttributes(hWnd, 0x00FF00FF, (byte)op, NativeMethods.LWA_COLORKEY | NativeMethods.LWA_ALPHA);

                    // Position the window so its visual centre lands on the target coordinate
                    (int w, int h, int ox, int oy) = Geometry;
                    NativeMethods.SetWindowPos(hWnd, NativeMethods.HWND_TOPMOST, x - ox, y - oy, w, h, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

                    // Force a repaint in case it was already visible
                    NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, true);
                    return IntPtr.Zero;

                case NativeMethods.WM_APP_HIDE:
                    NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE);
                    return IntPtr.Zero;

                case NativeMethods.WM_PAINT:
                    IntPtr hdc = NativeMethods.BeginPaint(hWnd, out NativeMethods.PAINTSTRUCT ps);

                    using (Graphics g = Graphics.FromHdc(hdc))
                        Paint(g);

                    NativeMethods.EndPaint(hWnd, ref ps);
                    return IntPtr.Zero;

                case NativeMethods.WM_CLOSE:
                    NativeMethods.PostMessage(hWnd, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
                    return IntPtr.Zero;
            }
            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.PostMessage(_hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _messageThread.Join(2000);
                _hwnd = IntPtr.Zero;
            }
            _ready.Dispose();
        }
    }

    /// <param name="g">Graphics context to draw onto.</param>
    /// <param name="centerX">X coordinate of the crosshair centre within the drawing surface.</param>
    /// <param name="centerY">Y coordinate of the crosshair centre within the drawing surface.</param>
    /// <param name="circleRadius">Radius of the circle ring.</param>
    /// <param name="lineRadius">Distance from the centre to each end of the crosshair lines. Should be greater than <paramref name="circleRadius"/>.</param>
    /// <param name="thickness">Pen stroke width in pixels.</param>
    /// <param name="color">Colour of the crosshair.</param>
    /// <param name="g">Graphics context to draw onto.</param>
    /// <param name="startX">X coordinate of the arrow tail within the drawing surface.</param>
    /// <param name="startY">Y coordinate of the arrow tail within the drawing surface.</param>
    /// <param name="endX">X coordinate of the arrow tip within the drawing surface.</param>
    /// <param name="endY">Y coordinate of the arrow tip within the drawing surface.</param>
    /// <param name="thickness">Pen stroke width in pixels.</param>
    /// <param name="color">Colour of the arrow.</param>
    /// <param name="arrowHeadLength">Length of each arrowhead barb in pixels.</param>
    private static void AddArrow(Graphics g, int startX, int startY, int endX, int endY, float thickness, Color color, int arrowHeadLength = 16)
    {
        using Pen pen = new Pen(color, thickness);
        g.DrawLine(pen, startX, startY, endX, endY);

        // Skip arrowhead if the two points are coincident
        double dx = endX - startX;
        double dy = endY - startY;
        if (dx == 0 && dy == 0) return;

        double angle = Math.Atan2(dy, dx);
        const double BarbAngle = Math.PI / 6; // 30°
        g.DrawLine(pen, endX, endY,
            (int)(endX - arrowHeadLength * Math.Cos(angle - BarbAngle)),
            (int)(endY - arrowHeadLength * Math.Sin(angle - BarbAngle)));
        g.DrawLine(pen, endX, endY,
            (int)(endX - arrowHeadLength * Math.Cos(angle + BarbAngle)),
            (int)(endY - arrowHeadLength * Math.Sin(angle + BarbAngle)));
    }

    private static void AddCrosshair(Graphics g, int centerX, int centerY, int circleRadius, int lineRadius, float thickness, Color color)
    {
        using Pen pen = new Pen(color, thickness);
        g.DrawEllipse(pen, centerX - circleRadius, centerY - circleRadius, circleRadius * 2, circleRadius * 2);
        g.DrawLine(pen, centerX - lineRadius, centerY, centerX + lineRadius, centerY); // Horizontal
        g.DrawLine(pen, centerX, centerY - lineRadius, centerX, centerY + lineRadius); // Vertical
    }

    /// <summary>
    /// Draws a circle with four outward spokes but no lines through the centre — an "open" crosshair.
    /// </summary>
    private static void AddOpenCrosshair(Graphics g, int centerX, int centerY, int circleRadius, int spokeLength, float thickness, Color color)
    {
        using Pen pen = new Pen(color, thickness);
        g.DrawEllipse(pen, centerX - circleRadius, centerY - circleRadius, circleRadius * 2, circleRadius * 2);
        int outer = circleRadius + spokeLength;
        g.DrawLine(pen, centerX,         centerY - circleRadius, centerX,         centerY - outer); // Up
        g.DrawLine(pen, centerX,         centerY + circleRadius, centerX,         centerY + outer); // Down
        g.DrawLine(pen, centerX - outer, centerY,                centerX - circleRadius, centerY); // Left
        g.DrawLine(pen, centerX + circleRadius, centerY,        centerX + outer, centerY);         // Right
    }

    /// <summary>
    /// Draws <paramref name="text"/> near (<paramref name="anchorX"/>, <paramref name="anchorY"/>) with a
    /// thin dark outline so it stays readable over any background colour.
    /// </summary>
    private static void AddLabel(Graphics g, int anchorX, int anchorY, string text, Color color)
    {
        using Font font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
        using SolidBrush outline = new SolidBrush(Color.FromArgb(200, Color.Black));
        using SolidBrush fill    = new SolidBrush(color);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        // Draw outline by painting the text shifted one pixel in each of the 8 directions
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    g.DrawString(text, font, outline, anchorX + dx, anchorY + dy);

        g.DrawString(text, font, fill, anchorX, anchorY);
    }

    private sealed class ClickPointMarker : MarkerWindow
    {
        public override MarkerType Type => MarkerType.ClickPoint;
        public override (int width, int height, int offsetX, int offsetY) Geometry { get; set; } = (60, 60, 30, 30);

        // Offset of the queue-order number label relative to the crosshair centre.
        // Positive X moves right, negative Y moves up. Target: top-right corner, outside the crosshair.
        // The crosshair arms end at ±lineRadius(22) and the circle radius is 15, so x>+15 and y<-15
        // is clear of all drawn elements. The 60×60 window leaves ~14px to the right of the right arm.
        private const int QueueLabelOffsetX = +16; // lands at cx+16 = 46, just right of the circle edge
        private const int QueueLabelOffsetY = -28; // lands at cy-28 =  2, well above the top arm

        // Offset of the legacy label relative to the crosshair centre.
        private const int LabelOffsetX = 26;
        private const int LabelOffsetY = 26;

        protected override void Paint(Graphics g)
        {
            (int w, int h, int cx, int cy) = Geometry;
            AddCrosshair(g, centerX: cx, centerY: cy, circleRadius: 15, lineRadius: 22, thickness: 4, Color.Red);

            if (QueueLabel != null)
            {
                AddLabel(g, cx + QueueLabelOffsetX, cy + QueueLabelOffsetY, QueueLabel, Color.Red);
            }
            else if (Label != null)
            {
                AddLabel(g, cx + LabelOffsetX, cy + LabelOffsetY, Label, Color.Red);
            }
        }
    }

    /// <summary>
    /// A small floating read-only text window shown above a bounding box so the human operator
    /// can see and copy the text the AI wants typed into the highlighted field.
    /// Unlike <see cref="MarkerWindow"/>, this window is NOT click-through — the user can click
    /// into it, select all with Ctrl+A, and copy with Ctrl+C.
    /// </summary>
    private sealed class TypeTextTooltipWindow : IDisposable
    {
        private IntPtr _hwnd;
        private IntPtr _editHwnd;
        private IntPtr _hFont;
        private IntPtr _hbrBackground;
        private readonly Thread _messageThread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly NativeMethods.WndProcDelegate _wndProcDelegate;

        private const int WindowHeight = 30;
        private const int EditPadding  = 2;
        private const string WndClassName = "TUA_TypeTextTooltip";

        private int    _x, _y, _w;
        private string _text = "";

        public TypeTextTooltipWindow()
        {
            _wndProcDelegate = WndProc;
            _messageThread = new Thread(MessagePump) { IsBackground = true, Name = "TypeTextTooltipPump" };
            _messageThread.SetApartmentState(ApartmentState.STA);
            _messageThread.Start();
            _ready.Wait();
        }

        /// <summary>Positions and shows the tooltip above (or below if near screen top) the given bounding box.</summary>
        public void ShowAboveBox(int boxLeft, int boxTop, int boxRight, string text)
        {
            int w = Math.Max(boxRight - boxLeft, 120);
            int y = boxTop - WindowHeight - 2;
            if (y < 0) y = boxTop + 2; // Not enough room above — show just below the box top
            lock (this) { _x = boxLeft; _y = y; _w = w; _text = text; }
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Hides the tooltip window.</summary>
        public void Hide()
        {
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_APP_HIDE, IntPtr.Zero, IntPtr.Zero);
        }

        private void MessagePump()
        {
            NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(-4));

            // Light rose tint (RGB 255,235,235) ties in with the red bounding box; COLORREF is 0x00BBGGRR
            _hbrBackground = NativeMethods.CreateSolidBrush(0x00EBEBFF);

            NativeMethods.WNDCLASSEX wc = new NativeMethods.WNDCLASSEX
            {
                cbSize        = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                lpszClassName = WndClassName,
                hbrBackground = _hbrBackground,
            };
            NativeMethods.RegisterClassExW(ref wc); // Silently no-ops if already registered by another instance

            // Topmost tool window; NOT click-through (no WS_EX_TRANSPARENT) so the user can interact
            uint exStyle = NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW;
            uint style   = NativeMethods.WS_POPUP | NativeMethods.WS_BORDER;

            _hwnd = NativeMethods.CreateWindowExW(exStyle, WndClassName, "",
                style, 0, 0, 120, WindowHeight,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            // Single-line read-only EDIT child — user can click in, select all, Ctrl+C
            uint editStyle = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE
                           | NativeMethods.ES_READONLY | NativeMethods.ES_AUTOHSCROLL;
            _editHwnd = NativeMethods.CreateWindowExW(
                dwExStyle: 0,
                lpClassName: "EDIT", lpWindowName: "",
                dwStyle: editStyle,
                x: EditPadding,
                y: EditPadding,
                nWidth: 120 - (EditPadding * 2),
                nHeight: WindowHeight - (EditPadding * 2),
                hWndParent: _hwnd,
                hMenu: IntPtr.Zero,
                hInstance: IntPtr.Zero,
                lpParam: IntPtr.Zero
            );

            // Segoe UI 11pt bold — matches the label style used by other markers
            _hFont = NativeMethods.CreateFontW(-15, 0, 0, 0, 700 /* FW_BOLD */, 0, 0, 0,
                0, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, "Segoe UI");
            NativeMethods.SendMessageW(_editHwnd, NativeMethods.WM_SETFONT, _hFont, new IntPtr(1));

            _ready.Set();

            while (NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }

            NativeMethods.DestroyWindow(_hwnd);
            NativeMethods.DeleteObject(_hbrBackground);
            if (_hFont != IntPtr.Zero) NativeMethods.DeleteObject(_hFont);
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case NativeMethods.WM_APP_SHOW:
                    int x, y, w; string text;
                    lock (this) { x = _x; y = _y; w = _w; text = _text; }
                    // Update edit text and resize it to fill the (possibly new) window width
                    NativeMethods.SetWindowTextW(_editHwnd, text);
                    NativeMethods.SetWindowPos(_editHwnd, IntPtr.Zero,
                        EditPadding, EditPadding, w - EditPadding * 2, WindowHeight - EditPadding * 2,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                    NativeMethods.SetWindowPos(hWnd, NativeMethods.HWND_TOPMOST, x, y, w, WindowHeight,
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                    return IntPtr.Zero;

                case NativeMethods.WM_APP_HIDE:
                    NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE);
                    return IntPtr.Zero;

                case NativeMethods.WM_CTLCOLOREDIT:
                case NativeMethods.WM_CTLCOLORSTATIC:
                    // wParam is the HDC for the EDIT control — set colors and return our background brush
                    NativeMethods.SetBkColor(wParam, 0x00EBEBFF);   // light lavender, matches window background
                    NativeMethods.SetTextColor(wParam, 0x00000000);  // black text
                    return _hbrBackground;

                case NativeMethods.WM_CLOSE:
                    NativeMethods.PostMessage(hWnd, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
                    return IntPtr.Zero;
            }
            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.PostMessage(_hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _messageThread.Join(2000);
                _hwnd = IntPtr.Zero;
            }
            _ready.Dispose();
        }
    }

    private sealed class BoundingBoxMarker : MarkerWindow
    {
        private const int BorderThickness = 3;
        // Padding so the border stroke is never clipped at the window edge (half stroke width, rounded up).
        private const int Padding = (BorderThickness + 1) / 2 + 1;
        // Extra height added below the box when a queue-order number label is present.
        private const int QueueLabelExtraHeight = 28;
        // Offset of the queue label from the bottom-left corner of the drawn rectangle.
        private const int QueueLabelOffsetX = 0;
        private const int QueueLabelOffsetY = 6; // gap between box bottom and label top

        private int _localWidth, _localHeight, _fillOpacity;
        private TypeTextTooltipWindow? _tooltip;

        public override MarkerType Type => MarkerType.BoundingBox;
        public override (int width, int height, int offsetX, int offsetY) Geometry { get; set; } = (0, 0, 0, 0);

        /// <summary>
        /// Computes window geometry from the bounding-box screen coordinates, stores fill opacity,
        /// then shows the marker.
        /// </summary>
        public void Show(int x1, int y1, int x2, int y2, int borderOpacity, int fillOpacity, int durationMs, MarkerPool pool)
        {
            int left = Math.Min(x1, x2) - Padding;
            int top  = Math.Min(y1, y2) - Padding;
            int w    = Math.Abs(x2 - x1) + Padding * 2;
            // Expand the window downward when a queue label is present so the label isn't clipped.
            int h    = Math.Abs(y2 - y1) + Padding * 2 + (QueueLabel != null ? QueueLabelExtraHeight : 0);

            lock (StateLock)
            {
                Geometry      = (w, h, offsetX: x1 - left, offsetY: y1 - top);
                _localWidth   = Math.Abs(x2 - x1);
                _localHeight  = Math.Abs(y2 - y1);
                _fillOpacity  = fillOpacity;
            }

            if (Label is not null)
            {
                _tooltip ??= new TypeTextTooltipWindow();
                _tooltip.ShowAboveBox(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Label);
            }

            Show(x1, y1, borderOpacity, durationMs, pool);
        }

        protected override void OnBeforeHide()
        {
            _tooltip?.Hide();
        }

        protected override void Paint(Graphics g)
        {
            int localW, localH, fillAlpha;
            (_, _, int originX, int originY) = Geometry;
            lock (StateLock) { localW = _localWidth; localH = _localHeight; fillAlpha = _fillOpacity; }

            Rectangle rect = new Rectangle(originX, originY, localW, localH);

            if (fillAlpha > 0)
            {
                using SolidBrush fill = new SolidBrush(Color.FromArgb(fillAlpha, Color.Red));
                g.FillRectangle(fill, rect);
            }

            using Pen border = new Pen(Color.Red, BorderThickness);
            g.DrawRectangle(border, rect);

            if (QueueLabel != null)
                AddLabel(g, originX + QueueLabelOffsetX, originY + localH + QueueLabelOffsetY, QueueLabel, Color.Red);
        }
    }

    private sealed class MouseMoveDestinationMarker : MarkerWindow
    {
        // Extra padding so the open-crosshair arms at the destination are never clipped.
        // Should match or exceed circleRadius + spokeLength used in Paint.
        private const int Padding = 26;

        private int _localEndX, _localEndY;

        public override MarkerType Type => MarkerType.MouseMove;
        public override (int width, int height, int offsetX, int offsetY) Geometry { get; set; } = (0, 0, 0, 0);

        /// <summary>
        /// Computes window geometry from the move screen coordinates, stores the local-space endpoint,
        /// then shows the marker.
        /// </summary>
        public void Show(int x1, int y1, int x2, int y2, int opacity, int durationMs, MarkerPool pool)
        {
            int left = Math.Min(x1, x2) - Padding;
            int top  = Math.Min(y1, y2) - Padding;
            int w    = Math.Abs(x2 - x1) + Padding * 2;
            int h    = Math.Abs(y2 - y1) + Padding * 2;

            lock (StateLock)
            {
                Geometry   = (w, h, offsetX: x1 - left, offsetY: y1 - top);
                _localEndX = x2 - left;
                _localEndY = y2 - top;
            }

            Show(x1, y1, opacity, durationMs, pool);
        }

        protected override void Paint(Graphics g)
        {
            int endX, endY;
            (_, _, int startX, int startY) = Geometry;
            lock (StateLock) { endX = _localEndX; endY = _localEndY; }
            AddArrow(g, startX, startY, endX, endY, thickness: 3, Color.Blue);
            AddOpenCrosshair(g, endX, endY, circleRadius: 14, spokeLength: 10, thickness: 3, Color.Blue);

            string? displayLabel = QueueLabel ?? Label;
            if (displayLabel != null) 
            { 
                // Anchor the label above the endpoint crosshair; this keeps it within the window
                // regardless of arrow direction (the Padding guarantees room at the top).
                AddLabel(g, endX - 8, Math.Max(2, endY - Padding - 16), displayLabel, Color.Blue); 
            }
        }
    }

    private sealed class ClickDragMarker : MarkerWindow
    {
        // Extra padding around the two endpoints so crosshair arms are never clipped at the window edge.
        // Should match or exceed the lineRadius used in Paint.
        private const int Padding = 15;

        private int _localEndX, _localEndY;

        public override MarkerType Type => MarkerType.ClickDrag;
        public override (int width, int height, int offsetX, int offsetY) Geometry { get; set; } = (0, 0, 0, 0);

        /// <summary>
        /// Computes window geometry from the drag screen coordinates, stores the local-space endpoint,
        /// then shows the marker.
        /// </summary>
        public void Show(int x1, int y1, int x2, int y2, int opacity, int durationMs, MarkerPool pool)
        {
            int left = Math.Min(x1, x2) - Padding;
            int top  = Math.Min(y1, y2) - Padding;
            int w    = Math.Abs(x2 - x1) + Padding * 2;
            int h    = Math.Abs(y2 - y1) + Padding * 2;

            lock (StateLock)
            {
                Geometry   = (w, h, offsetX: x1 - left, offsetY: y1 - top);
                _localEndX = x2 - left;
                _localEndY = y2 - top;
            }

            Show(x1, y1, opacity, durationMs, pool);
        }

        protected override void Paint(Graphics g)
        {
            int endX, endY;
            (_, _, int startX, int startY) = Geometry;
            lock (StateLock) { endX = _localEndX; endY = _localEndY; }

            AddCrosshair(g, startX, startY, circleRadius: 10, lineRadius: Padding, thickness: 3, Color.Orange);
            AddCrosshair(g, endX,   endY,   circleRadius: 10, lineRadius: Padding, thickness: 3, Color.Orange);
            AddArrow(g, startX, startY, endX, endY, thickness: 3, Color.Orange);

            string? displayLabel = QueueLabel ?? Label;
            if (displayLabel != null)
            {
                // Place the label above the destination crosshair; the Padding guarantees vertical room.
                AddLabel(g, endX - 8, Math.Max(2, endY - Padding - 16), displayLabel, Color.Orange); 
            }
        }
    }

    /// <summary>
    /// A message-only window that subscribes to raw mouse input via <c>RIDEV_INPUTSINK</c>.
    /// It stays fully invisible and never steals focus. When a real mouse button-down lands within
    /// <see cref="HitRadius"/> pixels of a registered interactive marker target, it fires that
    /// marker's callback and unregisters the target. The overlay window for the marker remains
    /// fully click-through (<c>WS_EX_TRANSPARENT</c>) at all times; no event is re-injected.
    /// </summary>
    private sealed class RawInputSink : IDisposable
    {
        private const int HitRadius = 30; // pixels — generous enough for fat-finger accuracy
        private const string SinkClassName = "TUA_RawInputSink";

        private IntPtr _hwnd;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly NativeMethods.WndProcDelegate _wndProcDelegate;

        // markerId -> (targetX, targetY, callback)
        private readonly ConcurrentDictionary<int, (int x, int y, Action callback)> _targets = new();

        public RawInputSink()
        {
            _wndProcDelegate = WndProc;
            _thread = new Thread(Pump) { IsBackground = true, Name = "RawInputSinkPump" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait();
        }

        /// <summary>Registers a marker target. From this point, any button-down within <see cref="HitRadius"/> of (<paramref name="x"/>,<paramref name="y"/>) will dismiss it.</summary>
        public void Register(int markerId, int x, int y, Action callback)
            => _targets[markerId] = (x, y, callback);

        /// <summary>Removes a marker registration (called when the marker is cleared externally before being clicked).</summary>
        public void Unregister(int markerId)
            => _targets.TryRemove(markerId, out _);

        private void Pump()
        {
            NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(-4));

            NativeMethods.WNDCLASSEX wc = new NativeMethods.WNDCLASSEX
            {
                cbSize        = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                lpszClassName = SinkClassName,
                hbrBackground = IntPtr.Zero,
            };
            NativeMethods.RegisterClassExW(ref wc);

            // HWND_MESSAGE parent = message-only window; never shown, never focused.
            _hwnd = NativeMethods.CreateWindowExW(
                dwExStyle: 0, lpClassName: SinkClassName, lpWindowName: "",
                dwStyle: 0, x: 0, y: 0, nWidth: 0, nHeight: 0,
                hWndParent: new IntPtr(-3) /* HWND_MESSAGE */,
                hMenu: IntPtr.Zero, hInstance: IntPtr.Zero, lpParam: IntPtr.Zero);

            // RIDEV_INPUTSINK: receive WM_INPUT even when not in foreground.
            // Usage page 1 (Generic Desktop), usage 2 (Mouse).
            NativeMethods.RAWINPUTDEVICE[] rid =
            [
                new NativeMethods.RAWINPUTDEVICE
                {
                    usUsagePage = 0x01,
                    usUsage     = 0x02,
                    dwFlags     = NativeMethods.RIDEV_INPUTSINK,
                    hwndTarget  = _hwnd
                }
            ];
            NativeMethods.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());

            _ready.Set();

            while (NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == NativeMethods.WM_INPUT)
            {
                uint size = (uint)Marshal.SizeOf<NativeMethods.RAWINPUT>();
                uint result = NativeMethods.GetRawInputData(
                    lParam, NativeMethods.RID_INPUT,
                    out NativeMethods.RAWINPUT raw, ref size,
                    (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>());

                if (result != uint.MaxValue
                    && raw.header.dwType == NativeMethods.RIM_TYPEMOUSE
                    && (raw.mouse.usButtonFlags & (NativeMethods.RI_MOUSE_LEFT_BUTTON_DOWN
                                                 | NativeMethods.RI_MOUSE_RIGHT_BUTTON_DOWN
                                                 | NativeMethods.RI_MOUSE_MIDDLE_BUTTON_DOWN)) != 0)
                {
                    // Use GetCursorPos — raw deltas are relative in most HID drivers and
                    // may not reflect the actual DPI-scaled cursor position.
                    if (NativeMethods.GetCursorPos(out NativeMethods.POINT_RI pt))
                        CheckHit(pt.x, pt.y);
                }
            }
            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private void CheckHit(int px, int py)
        {
            foreach (var (id, (tx, ty, callback)) in _targets)
            {
                int dx = px - tx;
                int dy = py - ty;
                if (dx * dx + dy * dy <= HitRadius * HitRadius)
                {
                    if (_targets.TryRemove(id, out _))
                        callback();
                    return; // one dismiss per click
                }
            }
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.PostMessage(_hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _thread.Join(2000);
                _hwnd = IntPtr.Zero;
            }
            _ready.Dispose();
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

    // ---- Click Marker Related -----

    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_APP_SHOW = 0x8001;
    public const uint WM_APP_HIDE = 0x8002;

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_BORDER = 0x00800000;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_LAYERED = 0x00080000;

    public const uint WS_CHILD   = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint ES_READONLY    = 0x0800;
    public const uint ES_AUTOHSCROLL = 0x0080;
    public const uint WM_SETFONT = 0x0030;
    public const uint WM_CTLCOLOREDIT = 0x0133;
    public const uint WM_CTLCOLORSTATIC = 0x0138;
    public const uint SWP_NOZORDER = 0x0004;

    public const uint LWA_COLORKEY = 0x00000001;
    public const uint LWA_ALPHA = 0x00000002;

    public const int SW_HIDE = 0;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint SetBkColor(IntPtr hdc, uint crColor);

    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint SetTextColor(IntPtr hdc, uint crColor);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation,
        int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);

    // ---- Raw Input (used by RawInputSink for click-through interactive markers) ----

    public const uint WM_INPUT = 0x00FF;
    public const uint RIM_TYPEMOUSE = 0;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const ushort RI_MOUSE_LEFT_BUTTON_DOWN   = 0x0001;
    public const ushort RI_MOUSE_RIGHT_BUTTON_DOWN  = 0x0004;
    public const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint   dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint   dwType;
        public uint   dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWMOUSE
    {
        public ushort usFlags;
        public ushort usButtonFlags;
        public ushort usButtonData;
        public uint   ulRawButtons;
        public int    lLastX;
        public int    lLastY;
        public uint   ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWMOUSE       mouse;   // only mouse member needed; union sized to largest (mouse)
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT_RI
    {
        public int x, y;
    }

    [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand,
        out RAWINPUT pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool GetCursorPos(out POINT_RI lpPoint);

    public const uint RID_INPUT = 0x10000003;
}