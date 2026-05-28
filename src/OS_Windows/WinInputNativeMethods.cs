using System.Runtime.InteropServices;

namespace Thio_Universal_Agent.OS_Windows;

public partial class WindowsInputProvider : IInputProvider
{
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    internal const int SM_XVIRTUALSCREEN  = 76;
    internal const int SM_YVIRTUALSCREEN  = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    // Flags for KEYBDINPUT structure used in API calls
    // Reference: https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYDOWN = 0x0000; //TODO: See if this needs to be used anywhere
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_SCANCODE = 0x0008;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_UNICODE = 0x0004;

    // Mouse events
    public const int INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const int WHEEL_DELTA = 120;

    // WM_MOUSEWHEEL scroll message and SendMessageTimeout flags
    const uint WM_MOUSEWHEEL = 0x020A;
    const uint SMTO_NORMAL = 0x0000;

    [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern ushort MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern short VkKeyScanEx(char ch, IntPtr dwhkl);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetPhysicalCursorPos(int X, int Y);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr RealChildWindowFromPoint(IntPtr hwndParent, POINT ptParentClientCoords);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetSystemMetrics(int nIndex);


    // Dictionary to store virtual key codes
    private static readonly Dictionary<string, (ushort vk, ushort scan, bool extended)> modifierKeyCodes = new Dictionary<string, (ushort, ushort, bool)>
        {
            {"LCTRL",  (0x11, 29,   false)},
            {"LSHIFT", (0x10, 42,   false)},
            {"LALT",   (0x12, 56,   false)},
            {"LWIN",   (0x5B, 0x5B, true)},
        };

    // Named key lookup: maps key names from the agent parser to VK codes and extended-key flag.
    // Keys like "win", "enter", "tab" etc. can't be resolved by VkKeyScanW (which only handles typeable characters).
    private static readonly Dictionary<string, (ushort vk, bool extended)> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            { "win",         (0x5B, true) },   // VK_LWIN
            { "lwin",        (0x5B, true) },
            { "rwin",        (0x5C, true) },   // VK_RWIN
            { "enter",       (0x0D, false) },  // VK_RETURN
            { "return",      (0x0D, false) },
            { "tab",         (0x09, false) },  // VK_TAB
            { "escape",      (0x1B, false) },  // VK_ESCAPE
            { "esc",         (0x1B, false) },
            { "backspace",   (0x08, false) },  // VK_BACK
            { "delete",      (0x2E, true) },   // VK_DELETE
            { "del",         (0x2E, true) },
            { "space",       (0x20, false) },  // VK_SPACE
            { "up",          (0x26, true) },   // VK_UP
            { "down",        (0x28, true) },   // VK_DOWN
            { "left",        (0x25, true) },   // VK_LEFT
            { "right",       (0x27, true) },   // VK_RIGHT
            { "home",        (0x24, true) },   // VK_HOME
            { "end",         (0x23, true) },   // VK_END
            { "pageup",      (0x21, true) },   // VK_PRIOR
            { "pgup",        (0x21, true) },
            { "pagedown",    (0x22, true) },   // VK_NEXT
            { "pgdn",        (0x22, true) },
            { "insert",      (0x2D, true) },   // VK_INSERT
            { "ins",         (0x2D, true) },
            { "printscreen", (0x2C, false) },  // VK_SNAPSHOT
            { "prtsc",       (0x2C, false) },
            { "capslock",    (0x14, false) },  // VK_CAPITAL
            { "numlock",     (0x90, true) },   // VK_NUMLOCK
            { "f1",  (0x70, false) },
            { "f2",  (0x71, false) },
            { "f3",  (0x72, false) },
            { "f4",  (0x73, false) },
            { "f5",  (0x74, false) },
            { "f6",  (0x75, false) },
            { "f7",  (0x76, false) },
            { "f8",  (0x77, false) },
            { "f9",  (0x78, false) },
            { "f10", (0x79, false) },
            { "f11", (0x7A, false) },
            { "f12", (0x7B, false) },
        };



    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // For some reason you have to include all 3 types of inputs in the union, even if you're only using one
    // Otherwise the struct size will be wrong for some reason and SendInput will fail
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;         // Virtual Key Code
        public ushort wScan;       // Hardware scan code
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    public enum MapVirtualKeyType
    {
        MAPVK_VK_TO_VSC = 0,
        MAPVK_VSC_TO_VK = 1,
        MAPVK_VK_TO_CHAR = 2,
        MAPVK_VSC_TO_VK_EX = 3,
        MAPVK_VK_TO_VSC_EX = 4
    }

    // Enum for key states
    [Flags]
    enum KeyState
    {
        Shift = 1,
        Ctrl = 2,
        Alt = 4,
        Hankaku = 8,
        Reserved1 = 16,
        Reserved2 = 32
    }

}
