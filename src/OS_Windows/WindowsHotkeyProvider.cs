// Thio-Universal-Agent/OS_Windows/WindowsHotkeyProvider.cs
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Thio_Universal_Agent.OS_Windows;

/// <summary>
/// Windows implementation of <see cref="IHotkeyProvider"/>.
/// Creates an invisible message-only window (HWND_MESSAGE) on a dedicated background
/// thread so that <c>RegisterHotKey</c> and the <c>WndProc</c> callback share the same
/// thread affinity required by the Win32 message model.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHotkeyProvider : IHotkeyProvider
{
    #region PInvoke Definitions

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint    cbSize;
        public uint    style;
        public IntPtr  lpfnWndProc;    // function pointer
        public int     cbClsExtra;
        public int     cbWndExtra;
        public IntPtr  hInstance;
        public IntPtr  hIcon;
        public IntPtr  hCursor;
        public IntPtr  hbrBackground;
        public string? lpszMenuName;
        public string  lpszClassName;
        public IntPtr  hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    ptX, ptY;
    }

    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_CLOSE  = 0x0010;

    #endregion

    public event Action<int>? HotkeyPressed;

    private IntPtr _hwnd = IntPtr.Zero;
    private readonly Thread _messageThread;
    private readonly ManualResetEventSlim _ready = new(false);

    // Keep the delegate alive for the lifetime of this object to prevent GC collection
    // while the unmanaged window class still holds a pointer to it.
    private readonly WndProcDelegate _wndProcDelegate;

    public WindowsHotkeyProvider()
    {
        _wndProcDelegate = WndProc;

        _messageThread = new Thread(MessagePump)
        {
            IsBackground = true,
            Name = "HotkeyMessagePump"
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();

        // Wait until the window is created before returning to the caller.
        _ready.Wait();
    }

    /// <inheritdoc/>
    /// <remarks>Must be called from any thread; marshalled internally to the message-pump thread via PostMessage.</remarks>
    public void RegisterHotkey(int id, HotkeyModifiers modifiers, int virtualKey)
    {
        if (_hwnd == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(WindowsHotkeyProvider));

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        // WM_APP + id encodes the registration request; WndProc picks it up on the pump thread.
        // We pass virtualKey in the low word of lParam and modifiers in the high word.
        uint lParamValue = ((uint)modifiers << 16) | ((uint)virtualKey & 0xFFFF);
        _pendingRegistrations[(id, (int)lParamValue)] = tcs;
        PostMessage(_hwnd, WM_APP_REGISTER, (IntPtr)id, (IntPtr)(int)lParamValue);

        // Block the caller until registration succeeds or throws.
        int error = tcs.Task.GetAwaiter().GetResult();
        if (error != 0)
            throw new InvalidOperationException(
                $"RegisterHotKey failed for id={id} (Win32 error {error}). " +
                $"The hotkey combination may already be registered by another application.");
    }

    /// <inheritdoc/>
    public void UnregisterHotkey(int id)
    {
        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, WM_APP_UNREGISTER, (IntPtr)id, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _messageThread.Join(millisecondsTimeout: 2000);
            _hwnd = IntPtr.Zero;
        }
        _ready.Dispose();
    }

    #region Message Pump

    // Custom WM_APP messages used to marshal calls onto the pump thread.
    private const uint WM_APP_REGISTER   = 0x8001;
    private const uint WM_APP_UNREGISTER = 0x8002;

    // Pending registration results, keyed by (id, encodedParams).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), TaskCompletionSource<int>> _pendingRegistrations = new();

    private void MessagePump()
    {
        const string className = "TUA_HotkeyWindow";

        // Register a minimal window class.
        var wc = new WNDCLASSEX
        {
            cbSize      = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            lpszClassName = className,
        };
        RegisterClassExW(ref wc);

        // Create a message-only window (HWND_MESSAGE parent = no desktop presence).
        _hwnd = CreateWindowExW(0, className, "TUA Hotkey Listener", 0,
            0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        _ready.Set();

        if (_hwnd == IntPtr.Zero)
            return;

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        DestroyWindow(_hwnd);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                HotkeyPressed?.Invoke((int)wParam);
                return IntPtr.Zero;

            case WM_APP_REGISTER:
            {
                int id           = (int)wParam;
                int encoded      = (int)lParam;
                uint modifiers   = (uint)(encoded >> 16) & 0xFFFF;
                uint virtualKey  = (uint)(encoded & 0xFFFF);

                bool ok = RegisterHotKey(hWnd, id, modifiers, virtualKey);
                int error = ok ? 0 : Marshal.GetLastWin32Error();

                // Signal the waiting caller.
                var key = (id, encoded);
                if (_pendingRegistrations.TryRemove(key, out var tcs))
                    tcs.SetResult(error);

                return IntPtr.Zero;
            }

            case WM_APP_UNREGISTER:
                UnregisterHotKey(hWnd, (int)wParam);
                return IntPtr.Zero;

            case WM_CLOSE:
                // Unregister everything before the pump exits.
                foreach (var key in _pendingRegistrations.Keys)
                    UnregisterHotKey(hWnd, key.Item1);
                PostMessage(hWnd, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    #endregion
}
