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
        BoundingBox
    }

    private readonly MarkerPool _markerPool = new();

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
    public void DrawClickPointMarker(int x, int y, int durationMs, int markerOpacity = 255)
    {
        ClickPointMarker window = _markerPool.Rent<ClickPointMarker>(new CancellationTokenSource());
        window.Show(x, y, markerOpacity, durationMs, _markerPool);
    }

    public void DrawBoundingBox(int x1, int y1, int x2, int y2, int durationMs, int borderOpacity = 255, int fillOpacity = 0)
    {
        BoundingBoxMarker window = _markerPool.Rent<BoundingBoxMarker>(new CancellationTokenSource());
        window.Show(x1, y1, x2, y2, borderOpacity, fillOpacity, durationMs, _markerPool);
    }

    public void DrawClickDragMarker(int x_start, int y_start, int x_end, int y_end, int durationMs, int markerOpacity = 255)
    {
        ClickDragMarker window = _markerPool.Rent<ClickDragMarker>(new CancellationTokenSource());
        window.Show(x_start, y_start, x_end, y_end, markerOpacity, durationMs, _markerPool);
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
        /// <summary>The cancellation source for this window's active auto-hide timer. Set on activation, disposed and nulled on clear.</summary>
        public CancellationTokenSource? Cts { get; set; }

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

        private void Hide()
        {
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
                Hide();
                pool.Remove(this);
            }
        }

        /// <summary>Performs the GDI drawing for this marker type onto <paramref name="g"/>.</summary>
        protected abstract void Paint(Graphics g);

        private void MessagePump()
        {
            // CRITICAL: Make this thread Per-Monitor DPI Aware V2 so SetWindowPos uses raw physical pixels
            //TODO: Make enum, see: https://learn.microsoft.com/en-us/windows/win32/hidpi/dpi-awareness-context
            NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(-4));

            // Use Magenta as the background brush so we can Color-Key it out to be perfectly transparent
            _magentaBrush = NativeMethods.CreateSolidBrush(0x00FF00FF);

            NativeMethods.WNDCLASSEX wc = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                lpszClassName = "TUA_MarkerWindow",
                hbrBackground = _magentaBrush
            };
            NativeMethods.RegisterClassExW(ref wc);

            // Layered = transparency. Transparent = mouse clicks pass right through it. Topmost = stays on top.
            uint dwExStyle = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW;

            (int w, int h, _, _) = Geometry;
            _hwnd = NativeMethods.CreateWindowExW(
                dwExStyle: dwExStyle,
                lpClassName: "TUA_MarkerWindow",
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

    private sealed class ClickPointMarker : MarkerWindow
    {
        public override MarkerType Type => MarkerType.ClickPoint;
        public override (int width, int height, int offsetX, int offsetY) Geometry { get; set; } = (60, 60, 30, 30);

        protected override void Paint(Graphics g)
        {
            (int w, int h, int cx, int cy) = Geometry;
            AddCrosshair(g, centerX: cx, centerY: cy, circleRadius: 15, lineRadius: 22, thickness: 4, Color.Red);
        }
    }

    private sealed class BoundingBoxMarker : MarkerWindow
    {
        private const int BorderThickness = 3;
        // Padding so the border stroke is never clipped at the window edge (half stroke width, rounded up).
        private const int Padding = (BorderThickness + 1) / 2 + 1;

        private int _localWidth, _localHeight, _fillOpacity;

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
            int h    = Math.Abs(y2 - y1) + Padding * 2;

            lock (StateLock)
            {
                Geometry      = (w, h, offsetX: x1 - left, offsetY: y1 - top);
                _localWidth   = Math.Abs(x2 - x1);
                _localHeight  = Math.Abs(y2 - y1);
                _fillOpacity  = fillOpacity;
            }

            Show(x1, y1, borderOpacity, durationMs, pool);
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
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_LAYERED = 0x00080000;

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
}