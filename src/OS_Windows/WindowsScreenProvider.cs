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
    /// Optional callback surfaced by human-control overlays that let the operator explicitly continue.
    /// </summary>
    public Action? CurrentHumanAdvanceCallback { get; set; }

    public void RegisterHumanKeyComboWatcher(int? virtualKey, ModifierKeys modifiers, Action onPressed)
    {
        ArgumentNullException.ThrowIfNull(onPressed);

        if (virtualKey is null && modifiers == ModifierKeys.None)
            return;

        _rawInputSink ??= new RawInputSink();
        _rawInputSink.RegisterKeyCombo(virtualKey, modifiers, onPressed);
    }

    public void ClearHumanKeyComboWatchers()
        => _rawInputSink?.ClearKeyCombos();

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
        window.AdvanceCallback = CurrentHumanAdvanceCallback;
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

            NativeMethods.WNDCLASSEX wc = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                lpszClassName = _wndClassName,
                hbrBackground = IntPtr.Zero
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
        }

        private void RenderLayeredWindow(IntPtr hWnd, int x, int y, int opacity)
        {
            (int width, int height, int offsetX, int offsetY) geometry;
            lock (StateLock)
            {
                geometry = Geometry;
            }

            using Bitmap bitmap = new Bitmap(geometry.width, geometry.height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                Paint(g);
            }

            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr previousBitmap = NativeMethods.SelectObject(memoryDc, hBitmap);

            try
            {
                NativeMethods.POINT topLeft = new NativeMethods.POINT { x = x - geometry.offsetX, y = y - geometry.offsetY };
                NativeMethods.SIZE size = new NativeMethods.SIZE { cx = geometry.width, cy = geometry.height };
                NativeMethods.POINT sourcePoint = new NativeMethods.POINT { x = 0, y = 0 };
                NativeMethods.BLENDFUNCTION blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = NativeMethods.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = (byte)opacity,
                    AlphaFormat = NativeMethods.AC_SRC_ALPHA,
                };

                if (!NativeMethods.UpdateLayeredWindow(hWnd, screenDc, ref topLeft, ref size, memoryDc, ref sourcePoint, 0, ref blend, NativeMethods.ULW_ALPHA))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOW);
            }
            finally
            {
                NativeMethods.SelectObject(memoryDc, previousBitmap);
                NativeMethods.DeleteObject(hBitmap);
                NativeMethods.DeleteDC(memoryDc);
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case NativeMethods.WM_APP_SHOW:
                    int x, y, op;
                    lock (StateLock) { x = _x; y = _y; op = _opacity; }
                    RenderLayeredWindow(hWnd, x, y, op);
                    return IntPtr.Zero;

                case NativeMethods.WM_APP_HIDE:
                    NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE);
                    return IntPtr.Zero;

                case NativeMethods.WM_PAINT:
                    NativeMethods.BeginPaint(hWnd, out NativeMethods.PAINTSTRUCT ps);
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
    /// thin light outline so it stays readable over any background colour.
    /// </summary>
    private static void AddLabel(Graphics g, int anchorX, int anchorY, string text, Color color)
    {
        using Font font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
        using SolidBrush outline = new SolidBrush(Color.FromArgb(235, Color.White));
        using SolidBrush fill    = new SolidBrush(color);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        // Draw outline by painting the text shifted one pixel in each of the 8 directions
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    g.DrawString(text, font, outline, anchorX + dx, anchorY + dy);

        g.DrawString(text, font, fill, anchorX, anchorY);
    }

    /// <summary>
    /// Measures <paramref name="text"/> using the same font as <see cref="AddLabel"/> and the real
    /// screen DPI (via the desktop DC) so the result matches what <see cref="AddLabel"/> will paint.
    /// </summary>
    private static SizeF MeasureLabelSize(string text)
    {
        using Font font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
        using Graphics g = Graphics.FromHwnd(IntPtr.Zero);
        return g.MeasureString(text, font, 0, StringFormat.GenericTypographic);
    }

    private sealed class ClickPointMarker : MarkerWindow
    {
        public override MarkerType Type => MarkerType.ClickPoint;
        public override (int width, int height, int offsetX, int offsetY) Geometry { get; set; } = (74, 60, 30, 30);

        // Offset of the queue-order number label relative to the crosshair centre.
        // Positive X moves right, negative Y moves up. Target: top-right corner, outside the crosshair.
        // The crosshair arms end at ±lineRadius(22) and the circle radius is 15, so x>+15 and y<-15
        // is clear of all drawn elements. The 74×60 window leaves ~28px to the right of the right arm.
        private const int QueueLabelOffsetX = +16; // lands at cx+16 = 46, just right of the circle edge
        private const int QueueLabelOffsetY = -28; // lands at cy-28 =  2, well above the top arm

        // Offset of the legacy label relative to the crosshair centre.
        private const int LabelOffsetX = 26;
        private const int LabelOffsetY = 26;

        private readonly Color color = Color.Red;

        protected override void Paint(Graphics g)
        {
            (int w, int h, int cx, int cy) = Geometry;
            AddCrosshair(g, centerX: cx, centerY: cy, circleRadius: 15, lineRadius: 22, thickness: 4, color);

            if (QueueLabel != null)
            {
                AddLabel(g, cx + QueueLabelOffsetX, cy + QueueLabelOffsetY, QueueLabel, color);
            }
            else if (Label != null)
            {
                AddLabel(g, cx + LabelOffsetX, cy + LabelOffsetY, Label, color);
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
        private IntPtr _buttonHwnd;
        private IntPtr _hFont;
        private IntPtr _hbrBackground;
        private readonly Thread _messageThread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly NativeMethods.WndProcDelegate _wndProcDelegate;

        private const int WindowHeight = 32;
        private const int EditPadding  = 2;
        private const int ControlGap   = 2;
        private const int ButtonWidth  = 54;
        private const int MinEditWidth = 80;
        private const int EditControlId = 1001;
        private const int AdvanceButtonControlId = 1002;
        private const string WndClassName = "TUA_TypeTextTooltip";

        private int    _x, _y, _w;
        private string _text = "";
        private Action? _advanceCallback;

        public TypeTextTooltipWindow()
        {
            _wndProcDelegate = WndProc;
            _messageThread = new Thread(MessagePump) { IsBackground = true, Name = "TypeTextTooltipPump" };
            _messageThread.SetApartmentState(ApartmentState.STA);
            _messageThread.Start();
            _ready.Wait();
        }

        /// <summary>Positions and shows the tooltip above (or below if near screen top) the given bounding box.</summary>
        public void ShowAboveBox(int boxLeft, int boxTop, int boxRight, string text, Action? onAdvance = null)
        {
            int minWidth = onAdvance is null
                ? 120
                : (EditPadding * 2) + ControlGap + ButtonWidth + MinEditWidth;
            int w = Math.Max(boxRight - boxLeft, minWidth);
            int y = boxTop - WindowHeight - 2;
            if (y < 0) y = boxTop + 2; // Not enough room above — show just below the box top
            lock (this)
            {
                _x = boxLeft;
                _y = y;
                _w = w;
                _text = text;
                _advanceCallback = onAdvance;
            }
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
                hMenu: new IntPtr(EditControlId),
                hInstance: IntPtr.Zero,
                lpParam: IntPtr.Zero
            );

            uint buttonStyle = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP;
            _buttonHwnd = NativeMethods.CreateWindowExW(
                dwExStyle: 0,
                lpClassName: "BUTTON",
                lpWindowName: "Next",
                dwStyle: buttonStyle,
                x: 0,
                y: EditPadding,
                nWidth: ButtonWidth,
                nHeight: WindowHeight - (EditPadding * 2),
                hWndParent: _hwnd,
                hMenu: new IntPtr(AdvanceButtonControlId),
                hInstance: IntPtr.Zero,
                lpParam: IntPtr.Zero);

            // Segoe UI 11pt bold — matches the label style used by other markers
            _hFont = NativeMethods.CreateFontW(-15, 0, 0, 0, 700 /* FW_BOLD */, 0, 0, 0,
                0, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, "Segoe UI");
            NativeMethods.SendMessageW(_editHwnd, NativeMethods.WM_SETFONT, _hFont, new IntPtr(1));
            NativeMethods.SendMessageW(_buttonHwnd, NativeMethods.WM_SETFONT, _hFont, new IntPtr(1));
            NativeMethods.ShowWindow(_buttonHwnd, NativeMethods.SW_HIDE);

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
                    int x, y, w;
                    string text;
                    Action? advanceCallback;
                    lock (this)
                    {
                        x = _x;
                        y = _y;
                        w = _w;
                        text = _text;
                        advanceCallback = _advanceCallback;
                    }
                    bool showAdvanceButton = advanceCallback is not null;
                    int buttonX = w - EditPadding - ButtonWidth;
                    int editWidth = showAdvanceButton
                        ? Math.Max(MinEditWidth, buttonX - ControlGap - EditPadding)
                        : w - (EditPadding * 2);
                    // Update edit text and resize it to fill the (possibly new) window width
                    NativeMethods.SetWindowTextW(_editHwnd, text);
                    NativeMethods.SetWindowPos(_editHwnd, IntPtr.Zero,
                        EditPadding, EditPadding, editWidth, WindowHeight - EditPadding * 2,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                    SelectAllText();

                    if (showAdvanceButton)
                    {
                        NativeMethods.SetWindowPos(_buttonHwnd, IntPtr.Zero,
                            buttonX, EditPadding, ButtonWidth, WindowHeight - EditPadding * 2,
                            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                        NativeMethods.ShowWindow(_buttonHwnd, NativeMethods.SW_SHOW);
                    }
                    else
                    {
                        NativeMethods.ShowWindow(_buttonHwnd, NativeMethods.SW_HIDE);
                    }

                    NativeMethods.SetWindowPos(hWnd, NativeMethods.HWND_TOPMOST, x, y, w, WindowHeight,
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                    return IntPtr.Zero;

                case NativeMethods.WM_APP_HIDE:
                    NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE);
                    return IntPtr.Zero;

                case NativeMethods.WM_COMMAND:
                    nuint wp = (nuint)wParam;
                    int controlId = (int)(wp & 0xFFFF);
                    int notificationCode = (int)((wp >> 16) & 0xFFFF);

                    if (controlId == EditControlId && notificationCode == NativeMethods.EN_SETFOCUS)
                    {
                        SelectAllText();
                        return IntPtr.Zero;
                    }

                    if (controlId == AdvanceButtonControlId && notificationCode == NativeMethods.BN_CLICKED)
                    {
                        Action? advance;
                        lock (this)
                            advance = _advanceCallback;
                        advance?.Invoke();
                        return IntPtr.Zero;
                    }
                    break;

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

        private void SelectAllText()
        {
            if (_editHwnd != IntPtr.Zero)
                NativeMethods.SendMessageW(_editHwnd, NativeMethods.EM_SETSEL, IntPtr.Zero, new IntPtr(-1));
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
        // Offset of the queue label from the bottom-left corner of the drawn rectangle.
        private const int QueueLabelOffsetX  =  0;
        private const int QueueLabelOffsetY  =  6;  // gap between box bottom and label top
        private const int LabelRightMargin   =  6;  // extra right buffer against measurement rounding

        private readonly Color color = Color.Red;

        private int _localWidth, _localHeight, _fillOpacity;
        private TypeTextTooltipWindow? _tooltip;
        internal Action? AdvanceCallback;

        public override MarkerType Type => MarkerType.BoundingBox;
        public override (int width, int height, int offsetX, int offsetY) Geometry { get; set; } = (0, 0, 0, 0);

        /// <summary>
        /// Computes window geometry from the bounding-box screen coordinates, stores fill opacity,
        /// then shows the marker.
        /// </summary>
        public void Show(int x1, int y1, int x2, int y2, int borderOpacity, int fillOpacity, int durationMs, MarkerPool pool)
        {
            int left   = Math.Min(x1, x2) - Padding;
            int top    = Math.Min(y1, y2) - Padding;
            int w      = Math.Abs(x2 - x1) + Padding * 2;
            int h      = Math.Abs(y2 - y1) + Padding * 2;
            int localW = Math.Abs(x2 - x1);
            int localH = Math.Abs(y2 - y1);

            if (QueueLabel != null)
            {
                SizeF labelSize = MeasureLabelSize(QueueLabel);
                int labelH = (int)Math.Ceiling(labelSize.Height);
                int labelW = (int)Math.Ceiling(labelSize.Width);

                // Expand downward to fit the label below the box.
                h = Math.Max(h, localH + Padding * 2 + QueueLabelOffsetY + labelH);

                // Expand right if the label is wider than the box.
                int requiredW = Padding + QueueLabelOffsetX + labelW + LabelRightMargin;
                if (requiredW > w)
                    w = requiredW;
            }

            lock (StateLock)
            {
                Geometry      = (w, h, offsetX: x1 - left, offsetY: y1 - top);
                _localWidth   = localW;
                _localHeight  = localH;
                _fillOpacity  = fillOpacity;
            }

            if (Label is not null)
            {
                _tooltip ??= new TypeTextTooltipWindow();
                _tooltip.ShowAboveBox(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Label, AdvanceCallback);
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
                using SolidBrush fill = new SolidBrush(Color.FromArgb(fillAlpha, color));
                g.FillRectangle(fill, rect);
            }

            using Pen border = new Pen(color, BorderThickness);
            g.DrawRectangle(border, rect);

            if (QueueLabel != null)
                AddLabel(g, originX + QueueLabelOffsetX, originY + localH + QueueLabelOffsetY, QueueLabel, color);
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

        // Label placement constants shared by Show (sizing) and Paint (drawing).
        private const int LabelOffsetX  = 16;  // gap right of the crosshair centre
        private const int LabelOffsetY  = 28;  // gap above the crosshair centre
        private const int LabelRightMargin = 6; // extra right margin so the window edge never clips the glyphs

        private readonly Color color = Color.DarkOrange;

        /// <summary>
        /// Computes window geometry from the move screen coordinates, stores the local-space endpoint, then shows the marker.
        /// The window is expanded as needed so the label always fits without clipping.
        /// </summary>
        public void Show(int x1, int y1, int x2, int y2, int opacity, int durationMs, MarkerPool pool)
        {
            int left = Math.Min(x1, x2) - Padding;
            int top  = Math.Min(y1, y2) - Padding;
            int w    = Math.Abs(x2 - x1) + Padding * 2;
            int h    = Math.Abs(y2 - y1) + Padding * 2;

            int localEndX = x2 - left;
            int localEndY = y2 - top;

            string? displayLabel = QueueLabel ?? Label;
            if (displayLabel != null)
            {
                SizeF labelSize = MeasureLabelSize(displayLabel);

                // Expand right if the label extends past the window's right edge.
                int requiredW = localEndX + LabelOffsetX + (int)Math.Ceiling(labelSize.Width) + LabelRightMargin;
                if (requiredW > w)
                    w = requiredW;

                // Expand upward if the label extends past the window's top edge.
                int labelTopInWindow = localEndY - LabelOffsetY;
                if (labelTopInWindow < 0)
                {
                    int expand = -labelTopInWindow;
                    top      -= expand;
                    h        += expand;
                    localEndY += expand;
                }
            }

            lock (StateLock)
            {
                Geometry   = (w, h, offsetX: x1 - left, offsetY: y1 - top);
                _localEndX = localEndX;
                _localEndY = localEndY;
            }

            Show(x1, y1, opacity, durationMs, pool);
        }

        protected override void Paint(Graphics g)
        {
            int endX, endY;
            (_, _, int startX, int startY) = Geometry;
            lock (StateLock) { endX = _localEndX; endY = _localEndY; }
            AddArrow(g, startX, startY, endX, endY, thickness: 3, color);
            AddOpenCrosshair(g, endX, endY, circleRadius: 14, spokeLength: 10, thickness: 3, color);

            string? displayLabel = QueueLabel ?? Label;
            if (displayLabel != null)
                // Show() already expanded the window to fit the label here, so no clamping needed.
                AddLabel(g, endX + LabelOffsetX, endY - LabelOffsetY, displayLabel, color);
        }
    }

    private sealed class ClickDragMarker : MarkerWindow
    {
        // Extra padding around the two endpoints so crosshair arms are never clipped at the window edge.
        // Should match or exceed the lineRadius used in Paint.
        private const int Padding = 15;

        // Label placement constants shared by Show (sizing) and Paint (drawing).
        private const int LabelOffsetX    = 16;  // gap right of the endpoint crosshair centre
        private const int LabelOffsetY    = 28;  // gap above the endpoint crosshair centre
        private const int LabelRightMargin =  6;  // extra right buffer against measurement rounding

        private readonly Color color = Color.DarkOrange;

        private int _localEndX, _localEndY;

        public override MarkerType Type => MarkerType.ClickDrag;
        public override (int width, int height, int offsetX, int offsetY) Geometry { get; set; } = (0, 0, 0, 0);

        /// <summary>
        /// Computes window geometry from the drag screen coordinates, stores the local-space endpoint,
        /// then shows the marker. The window is expanded as needed so the label always fits without clipping.
        /// </summary>
        public void Show(int x1, int y1, int x2, int y2, int opacity, int durationMs, MarkerPool pool)
        {
            int left = Math.Min(x1, x2) - Padding;
            int top  = Math.Min(y1, y2) - Padding;
            int w    = Math.Abs(x2 - x1) + Padding * 2;
            int h    = Math.Abs(y2 - y1) + Padding * 2;

            int localEndX = x2 - left;
            int localEndY = y2 - top;

            string? displayLabel = QueueLabel ?? Label;
            if (displayLabel != null)
            {
                SizeF labelSize = MeasureLabelSize(displayLabel);

                // Expand right if the label extends past the window's right edge.
                int requiredW = localEndX + LabelOffsetX + (int)Math.Ceiling(labelSize.Width) + LabelRightMargin;
                if (requiredW > w)
                    w = requiredW;

                // Expand upward if the label extends past the window's top edge.
                int labelTopInWindow = localEndY - LabelOffsetY;
                if (labelTopInWindow < 0)
                {
                    int expand = -labelTopInWindow;
                    top       -= expand;
                    h         += expand;
                    localEndY += expand;
                }
            }

            lock (StateLock)
            {
                Geometry   = (w, h, offsetX: x1 - left, offsetY: y1 - top);
                _localEndX = localEndX;
                _localEndY = localEndY;
            }

            Show(x1, y1, opacity, durationMs, pool);
        }

        private int circleRadius = 10;
        // Calculate new start x and y. Shorten the line on each end by the circle radius
        private (int newXStart, int newYStart, int newXEnd, int newYEnd) CalcShortenedLine(int startX, int startY, int endX, int endY)
        {
            double dx = endX - startX;
            double dy = endY - startY;
            double length = Math.Sqrt(dx * dx + dy * dy);

            if (length == 0)
                return (startX, startY, endX, endY);

            double shortenBy = Math.Min(circleRadius, length / 2d);
            double unitX = dx / length;
            double unitY = dy / length;

            return (
                newXStart: (int)Math.Round(startX + unitX * shortenBy),
                newYStart: (int)Math.Round(startY + unitY * shortenBy),
                newXEnd: (int)Math.Round(endX - unitX * shortenBy),
                newYEnd: (int)Math.Round(endY - unitY * shortenBy));
        }

        protected override void Paint(Graphics g)
        {
            int endX, endY;
            (_, _, int startX, int startY) = Geometry;
            lock (StateLock) { endX = _localEndX; endY = _localEndY; }

            (int shortenedStartX, int shortenedStartY, int shortenedEndX, int shortenedEndY) = CalcShortenedLine(startX, startY, endX, endY);

            AddCrosshair(g, startX, startY, circleRadius: circleRadius, lineRadius: Padding, thickness: 2, color);
            AddCrosshair(g, endX,   endY,   circleRadius: circleRadius, lineRadius: Padding, thickness: 2, color);
            AddArrow(g, shortenedStartX, shortenedStartY, shortenedEndX, shortenedEndY, thickness: 3, color);

            string? displayLabel = QueueLabel ?? Label;
            if (displayLabel != null)
                // Show() already expanded the window to fit the label here, so no clamping needed.
                AddLabel(g, endX + LabelOffsetX, endY - LabelOffsetY, displayLabel, color);
        }
    }

    /// <summary>
    /// An off-screen tool window that subscribes to raw mouse input via <c>RIDEV_INPUTSINK</c>.
    /// It never takes focus or shows up in normal window chrome. When a real mouse button-down lands
    /// within <see cref="HitRadius"/> pixels of a registered interactive marker target, it fires that
    /// marker's callback and unregisters the target. The overlay window for the marker remains fully
    /// click-through (<c>WS_EX_TRANSPARENT</c>) at all times; no event is re-injected.
    /// </summary>
    private sealed class RawInputSink : IDisposable
    {
        private const int HitRadius = 30; // pixels — generous enough for fat-finger accuracy
        private const int HiddenWindowX = -32000;
        private const int HiddenWindowY = -32000;
        private const int HiddenWindowSize = 1;

        private static int _instanceCounter;
        private string _windowClassName = "";

        private IntPtr _hwnd;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly NativeMethods.WndProcDelegate _wndProcDelegate;
        private Exception? _startupException;

        // markerId -> (targetX, targetY, callback)
        private readonly ConcurrentDictionary<int, (int x, int y, Action callback)> _targets = new();
        // keyComboId -> (primaryVirtualKey, modifiers, callback)
        private readonly ConcurrentDictionary<int, (int? virtualKey, ModifierKeys modifiers, Action callback)> _keyCombos = new();
        private int _keyComboCounter;

        public RawInputSink()
        {
            _wndProcDelegate = WndProc;
            _thread = new Thread(Pump) { IsBackground = true, Name = "RawInputSinkPump" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait();

            if (_startupException != null)
                throw new InvalidOperationException("Failed to initialize the raw input sink window.", _startupException);
        }

        /// <summary>Registers a marker target. From this point, any button-down within <see cref="HitRadius"/> of (<paramref name="x"/>,<paramref name="y"/>) will dismiss it.</summary>
        public void Register(int markerId, int x, int y, Action callback)
            => _targets[markerId] = (x, y, callback);

        /// <summary>Removes a marker registration (called when the marker is cleared externally before being clicked).</summary>
        public void Unregister(int markerId)
            => _targets.TryRemove(markerId, out _);

        /// <summary>Registers a key combo that should auto-advance once physically pressed.</summary>
        public void RegisterKeyCombo(int? virtualKey, ModifierKeys modifiers, Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);

            if (virtualKey is null && modifiers == ModifierKeys.None)
                return;

            int comboId = Interlocked.Increment(ref _keyComboCounter);
            _keyCombos[comboId] = (virtualKey, modifiers, callback);
        }

        /// <summary>Clears any pending key-combo registrations.</summary>
        public void ClearKeyCombos() => _keyCombos.Clear();

        private void Pump()
        {
            try
            {
                NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(-4));

                // Use a unique class name so this instance's WndProc is always the one bound to the HWND.
                _windowClassName = $"TUA_RawInputSink_{Interlocked.Increment(ref _instanceCounter)}";

                NativeMethods.WNDCLASSEX wc = new NativeMethods.WNDCLASSEX
                {
                    cbSize        = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                    lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                    lpszClassName = _windowClassName,
                    hbrBackground = IntPtr.Zero,
                };
                if (NativeMethods.RegisterClassExW(ref wc) == 0)
                    throw CreateWin32Failure("RegisterClassExW for raw input sink failed");

                // Use an off-screen top-level tool window instead of a message-only window.
                // This matches the user's working sample more closely and avoids relying on
                // message-only-window delivery semantics for WM_INPUT.
                _hwnd = NativeMethods.CreateWindowExW(
                    dwExStyle: NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE,
                    lpClassName: _windowClassName,
                    lpWindowName: "TUA Raw Input Sink",
                    dwStyle: NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE,
                    x: HiddenWindowX,
                    y: HiddenWindowY,
                    nWidth: HiddenWindowSize,
                    nHeight: HiddenWindowSize,
                    hWndParent: IntPtr.Zero,
                    hMenu: IntPtr.Zero,
                    hInstance: IntPtr.Zero,
                    lpParam: IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                    throw CreateWin32Failure("CreateWindowExW for raw input sink failed");

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
                    },
                    new NativeMethods.RAWINPUTDEVICE
                    {
                        usUsagePage = 0x01,
                        usUsage     = 0x06,
                        dwFlags     = NativeMethods.RIDEV_INPUTSINK,
                        hwndTarget  = _hwnd
                    }
                ];
                if (!NativeMethods.RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
                    throw CreateWin32Failure("RegisterRawInputDevices for mouse/keyboard input failed");
            }
            catch (Exception ex)
            {
                _startupException = ex;
                return;
            }
            finally
            {
                _ready.Set();
            }

            while (NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }

            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
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
                    && (raw.data.mouse.usButtonFlags & (NativeMethods.RI_MOUSE_LEFT_BUTTON_DOWN
                                                      | NativeMethods.RI_MOUSE_RIGHT_BUTTON_DOWN
                                                      | NativeMethods.RI_MOUSE_MIDDLE_BUTTON_DOWN)) != 0)
                {
                    // Use GetCursorPos — raw deltas are relative in most HID drivers and
                    // may not reflect the actual DPI-scaled cursor position.
                    if (NativeMethods.GetCursorPos(out NativeMethods.POINT_RI pt))
                        CheckHit(pt.x, pt.y);
                }
                else if (result != uint.MaxValue
                      && raw.header.dwType == NativeMethods.RIM_TYPEKEYBOARD
                      && (raw.data.keyboard.Message == NativeMethods.WM_KEYDOWN
                       || raw.data.keyboard.Message == NativeMethods.WM_SYSKEYDOWN
                       || raw.data.keyboard.Message == NativeMethods.WM_KEYUP
                       || raw.data.keyboard.Message == NativeMethods.WM_SYSKEYUP))
                {
                    bool isKeyDown = raw.data.keyboard.Message == NativeMethods.WM_KEYDOWN
                                  || raw.data.keyboard.Message == NativeMethods.WM_SYSKEYDOWN;
                    CheckKeyCombo(raw.data.keyboard.VKey, isKeyDown);
                }

                return IntPtr.Zero;
            }

            if (msg == NativeMethods.WM_CLOSE)
            {
                NativeMethods.PostMessage(hWnd, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
                return IntPtr.Zero;
            }

            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private static InvalidOperationException CreateWin32Failure(string operation)
            => new InvalidOperationException($"{operation} (Win32 error {Marshal.GetLastWin32Error()}).");

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

        private void CheckKeyCombo(ushort changedVirtualKey, bool isKeyDown)
        {
            foreach (var (id, (virtualKey, modifiers, callback)) in _keyCombos)
            {
                if (virtualKey.HasValue)
                {
                    if (changedVirtualKey != virtualKey.Value)
                        continue;

                    if (!AreModifiersPressed(modifiers))
                        continue;
                }
                else
                {
                    if (!isKeyDown || !IsExpectedModifierKey(changedVirtualKey, modifiers) || !AreModifiersPressed(modifiers))
                        continue;
                }

                if (_keyCombos.TryRemove(id, out (int? virtualKey, ModifierKeys modifiers, Action callback) matched))
                {
                    matched.callback();
                    return;
                }
            }
        }

        private static bool AreModifiersPressed(ModifierKeys modifiers)
        {
            if (modifiers.HasFlag(ModifierKeys.Ctrl) && !IsAnyVirtualKeyDown(0x11, 0xA2, 0xA3))
                return false;

            if (modifiers.HasFlag(ModifierKeys.Shift) && !IsAnyVirtualKeyDown(0x10, 0xA0, 0xA1))
                return false;

            if (modifiers.HasFlag(ModifierKeys.Alt) && !IsAnyVirtualKeyDown(0x12, 0xA4, 0xA5))
                return false;

            if (modifiers.HasFlag(ModifierKeys.Win) && !IsAnyVirtualKeyDown(0x5B, 0x5C))
                return false;

            return true;
        }

        private static bool IsExpectedModifierKey(int changedVirtualKey, ModifierKeys modifiers)
        {
            return (modifiers.HasFlag(ModifierKeys.Ctrl) && IsOneOf(changedVirtualKey, 0x11, 0xA2, 0xA3))
                || (modifiers.HasFlag(ModifierKeys.Shift) && IsOneOf(changedVirtualKey, 0x10, 0xA0, 0xA1))
                || (modifiers.HasFlag(ModifierKeys.Alt) && IsOneOf(changedVirtualKey, 0x12, 0xA4, 0xA5))
                || (modifiers.HasFlag(ModifierKeys.Win) && IsOneOf(changedVirtualKey, 0x5B, 0x5C));
        }

        private static bool IsVirtualKeyDown(int virtualKey)
            => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        private static bool IsAnyVirtualKeyDown(params int[] virtualKeys)
        {
            foreach (int virtualKey in virtualKeys)
            {
                if (IsVirtualKeyDown(virtualKey))
                    return true;
            }

            return false;
        }

        private static bool IsOneOf(int value, params int[] candidates)
        {
            foreach (int candidate in candidates)
            {
                if (value == candidate)
                    return true;
            }

            return false;
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
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    public const uint WS_CHILD   = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_TABSTOP = 0x00010000;
    public const uint ES_READONLY    = 0x0800;
    public const uint ES_AUTOHSCROLL = 0x0080;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_SETFONT = 0x0030;
    public const uint WM_CTLCOLOREDIT = 0x0133;
    public const uint WM_CTLCOLORSTATIC = 0x0138;
    public const uint EM_SETSEL = 0x00B1;
    public const int EN_SETFOCUS = 0x0100;
    public const int BN_CLICKED = 0;
    public const uint SWP_NOZORDER = 0x0004;

    public const uint LWA_COLORKEY = 0x00000001;
    public const uint LWA_ALPHA = 0x00000002;
    public const uint ULW_ALPHA = 0x00000002;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
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

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("gdi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

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
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint RIM_TYPEMOUSE = 0;
    public const uint RIM_TYPEKEYBOARD = 1;
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

    [StructLayout(LayoutKind.Explicit)]
    public struct RAWMOUSE
    {
        public const int ButtonUnionOffset = 4;

        [FieldOffset(0)]
        public ushort usFlags;

        // Win32 inserts 2 bytes of padding after usFlags so the 4-byte button union starts at offset 4.
        [FieldOffset(ButtonUnionOffset)]
        public uint ulButtons;

        [FieldOffset(ButtonUnionOffset)]
        public ushort usButtonFlags;

        [FieldOffset(ButtonUnionOffset + sizeof(ushort))]
        public ushort usButtonData;

        [FieldOffset(8)]
        public uint ulRawButtons;

        [FieldOffset(12)]
        public int lLastX;

        [FieldOffset(16)]
        public int lLastY;

        [FieldOffset(20)]
        public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct RAWINPUTDATA
    {
        [FieldOffset(0)]
        public RAWMOUSE mouse;

        [FieldOffset(0)]
        public RAWKEYBOARD keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWINPUTDATA   data;
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

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern short GetAsyncKeyState(int vKey);

    public const uint RID_INPUT = 0x10000003;
}
