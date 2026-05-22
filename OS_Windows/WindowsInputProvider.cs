// Thio-Universal-Agent/OS_Windows/WindowsInputProvider.cs
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static Thio_Universal_Agent.OS_Windows.WindowsInputProvider;

namespace Thio_Universal_Agent.OS_Windows
{
    public partial class WindowsInputProvider : IInputProvider
    {
        #region PInvoke Definitions

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

        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        // Dictionary to store virtual key codes


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

        #endregion


        public async Task SendModKeyComboAsync(string key, bool? ctrl = null, bool? shift = null, bool? alt = null, bool? win = null)
        {
            ushort vk;
            ushort scan;
            bool extended;

            // Try named keys first (win, enter, tab, f1, etc.) — TextCharCode only handles single typeable characters
            if (NamedKeys.TryGetValue(key, out var named))
            {
                vk = named.vk;
                scan = MapVirtualKey(vk, (uint)MapVirtualKeyType.MAPVK_VK_TO_VSC);
                extended = named.extended;
                shift ??= false;
            }
            else
            {
                TextCharCode keyChar = new TextCharCode(key);
                vk = keyChar.vk;
                scan = keyChar.scan;
                extended = keyChar.extended;
                shift ??= keyChar.shiftState;
            }

            ctrl ??= false;
            alt ??= false;
            win ??= false;

            // Array to contain list of individual key up and down events in sequence
            List<INPUT> inputList = new();

            // Add modifier keys down
            if (win == true)
                inputList.Add(CreateInput(vk: modifierKeyCodes["LWIN"].vk, scan: modifierKeyCodes["LWIN"].scan, isKeyUp: false, extended: modifierKeyCodes["LWIN"].extended));
            if (ctrl == true)
                inputList.Add(CreateInput(vk: modifierKeyCodes["LCTRL"].vk, scan: modifierKeyCodes["LCTRL"].scan, isKeyUp: false, extended: false));
            if (shift == true)
                inputList.Add(CreateInput(vk: modifierKeyCodes["LSHIFT"].vk, scan: modifierKeyCodes["LSHIFT"].scan, isKeyUp: false, extended: false));
            if (alt == true)
                inputList.Add(CreateInput(vk: modifierKeyCodes["LALT"].vk, scan: modifierKeyCodes["LALT"].scan, isKeyUp: false, extended: false));

            // Add main key down and up
            inputList.Add(CreateInput(vk: vk, scan: scan, isKeyUp: false, extended: extended));
            inputList.Add(CreateInput(vk: vk, scan: scan, isKeyUp: true, extended: extended));

            // Add modifier keys up
            if (alt == true)
                inputList.Add(CreateInput(vk: modifierKeyCodes["LALT"].vk, scan: modifierKeyCodes["LALT"].scan, isKeyUp: true, extended: false));
            if (shift == true)
                inputList.Add(CreateInput(vk: modifierKeyCodes["LSHIFT"].vk, scan: modifierKeyCodes["LSHIFT"].scan, isKeyUp: true, extended: false));
            if (ctrl == true)
                inputList.Add(CreateInput(vk: modifierKeyCodes["LCTRL"].vk, scan: modifierKeyCodes["LCTRL"].scan, isKeyUp: true, extended: false));
            if (win == true)
                inputList.Add(CreateInput(vk: modifierKeyCodes["LWIN"].vk, scan: modifierKeyCodes["LWIN"].scan, isKeyUp: true, extended: modifierKeyCodes["LWIN"].extended));

            INPUT[] inputs = inputList.ToArray();
            if (inputs.Length > 0)
            {
                _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            }
        }

        /// <summary>
        /// Types the specified text using the helper methods to construct inputs.
        /// This handles shift states for standard keys and falls back to Unicode for others.
        /// </summary>
        public async Task TypeTextAsync(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Convert the string into a list of TextCharCode objects.
            // TextCharCode logic determines if a character is a standard key (needing Shift/VK)
            // or if it should be treated as a Unicode packet.
            var charCodeList = new List<TextCharCode>();

            // Iterate over the string. 
            // Note: If you need to handle surrogate pairs (like emojis) that occupy 2 chars,
            // you might want to use StringInfo.GetTextElementEnumerator, but based on your 
            // TextCharCode constructor, passing single chars converted to string is the standard approach.
            foreach (char c in text)
            {
                charCodeList.Add(new TextCharCode(c.ToString()));
            }

            // Use the existing helper to generate the specific INPUT array
            // This handles injecting LSHIFT down/up events where required by the keyboard layout
            INPUT[] inputs = CreateInputArray(charCodeList);

            // Send all inputs in a single batch
            if (inputs.Length > 0)
            {
                _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            }

            await Task.CompletedTask;
        }

        // Helper function to create a single input
        private static INPUT CreateInput(ushort vk, ushort scan, bool isKeyUp = false, bool extended = false, bool scanFlag = false, bool unicodeFlag = false)
        {
            uint dwFlags = 0;

            if (isKeyUp)
                dwFlags |= KEYEVENTF_KEYUP;

            if (unicodeFlag)
            {
                dwFlags |= KEYEVENTF_UNICODE;
                // Note: Be sure that vk is 0 when using KEYEVENTF_UNICODE
            }
            else // KEYEVENTF_UNICODE can only be combined with KEYEVENTF_KEYUP, so only check for the rest of the flags if unicode is false
            {
                if (extended)
                    dwFlags |= KEYEVENTF_EXTENDEDKEY;

                if (scanFlag)
                    dwFlags |= KEYEVENTF_SCANCODE;
            }

            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = scan,
                        dwFlags = dwFlags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        private INPUT[] CreateInputArray(List<TextCharCode> charList)
        {
            List<INPUT> inputList = new();
            foreach (TextCharCode charCode in charList)
            {
                if (charCode.unicodeFlag == true)
                {
                    List<INPUT> unicodeInputs = MakeUnicodeInput(charCode);
                    inputList.AddRange(unicodeInputs);
                }
                else
                {
                    List<INPUT> singleCharInputs = MakeCharInput(charCode);
                    inputList.AddRange(singleCharInputs);
                }
            }

            return inputList.ToArray();
        }

        private static List<INPUT> MakeUnicodeInput(TextCharCode charCode)
        {
            List<INPUT> inputList = new();

            if (charCode.unicodeArray == null)
            {
                return new List<INPUT>();
            }
            else
            {
                // Make down inputs
                foreach (ushort unicodeChar in charCode.unicodeArray)
                {
                    INPUT downInput = CreateInput(vk: 0, scan: unicodeChar, isKeyUp: false, extended: false, scanFlag: false, unicodeFlag: true);
                    inputList.Add(downInput);

                }

                // Make up inputs
                foreach (ushort unicodeChar in charCode.unicodeArray)
                {
                    INPUT upInput = CreateInput(vk: 0, scan: unicodeChar, isKeyUp: true, extended: false, scanFlag: false, unicodeFlag: true);
                    inputList.Add(upInput);
                }

                return inputList;
            }
        }

        private static List<INPUT> MakeCharInput(TextCharCode charCode)
        {
            List<INPUT> inputList = new();
            // Add shift if needed
            if (charCode.shiftState == true)
            {
                inputList.Add(CreateInput(vk: modifierKeyCodes["LSHIFT"].vk, scan: modifierKeyCodes["LSHIFT"].scan, isKeyUp: false, extended: false));
            }

            // Key down
            INPUT downInput = CreateInput(vk: charCode.vk, scan: charCode.scan, isKeyUp: false, extended: charCode.extended, scanFlag: charCode.scanFlag, unicodeFlag: charCode.unicodeFlag);
            // Key up
            INPUT upInput = CreateInput(vk: charCode.vk, scan: charCode.scan, isKeyUp: true, extended: charCode.extended, scanFlag: charCode.scanFlag, unicodeFlag: charCode.unicodeFlag);

            // Add to the list
            inputList.Add(downInput);
            inputList.Add(upInput);

            // Remove shift if needed
            if (charCode.shiftState == true)
            {
                inputList.Add(CreateInput(vk: modifierKeyCodes["LSHIFT"].vk, scan: modifierKeyCodes["LSHIFT"].scan, isKeyUp: true, extended: false));
            }

            return inputList;
        }

        private class TextCharCode
        {
            public char? character;
            public ushort vk;
            public bool shiftState;
            public ushort scan;
            public bool extended;
            public bool scanFlag;
            public bool unicodeFlag;
            public ushort[]? unicodeArray;

            public TextCharCode(string characterString, bool extended = false, bool scanFlag = false, bool unicodeFlag = false)
            {
                (ushort vkCode, bool shift) = getVKCode(characterString);

                // Check if the mapping failed (-1 will be cast to ushort 0xFFFF). Assume it's a unicode character in this case.
                if (unicodeFlag == true || vkCode == 0xFFFF)
                {
                    this.unicodeArray = UnicodeToUShortArray(characterString);
                    this.character = null;
                    this.vk = vkCode;
                    this.scan = 0;
                    this.unicodeFlag = true;
                    this.shiftState = false;
                }
                else
                {
                    this.unicodeArray = null;
                    this.character = characterString[0];
                    this.vk = vkCode;
                    this.scan = getScanCode((ushort)this.vk);
                    this.unicodeFlag = false;
                    this.shiftState = shift;
                }

                this.extended = extended;
                this.scanFlag = scanFlag;

            }

            private static (ushort vk, bool shiftState) getVKCode(string character)
            {
                // Check if it's a single char or a unicode char (more than one char)
                if (character.Length > 1)
                {
                    return (0xFFFF, false);
                }
                else
                {
                    char c = character[0];
                    short returnInfo = VkKeyScanW(c);

                    // Low order byte is the virtual key code
                    ushort vk = (ushort)(returnInfo & 0xFF);

                    // High order byte is the shift state
                    ushort shiftStateData = (ushort)((returnInfo >> 8) & 0xFF);
                    bool shiftState = (shiftStateData & 1) == 1;

                    return (vk, shiftState);
                }
            }

            private static ushort getScanCode(ushort vkCode)
            {
                return MapVirtualKey((uint)vkCode, (uint)MapVirtualKeyType.MAPVK_VK_TO_VSC);
            }

            private static ushort[] UnicodeToUShortArray(string inputChar)
            {
                List<ushort> result = new List<ushort>();
                result.AddRange(inputChar.Select(c => (ushort)c));
                ushort[] finalArray = result.ToArray();
                return finalArray;
            }
        }

        // Mouse Events
        public async Task MoveMouse_MonitorCoords(int x, int y)
        {
            IntPtr originalContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            SetCursorPos(x, y);
            SetThreadDpiAwarenessContext(originalContext);
            await Task.CompletedTask;
        }

        public async Task LeftClick_MonitorCoords(int x, int y)
        {
            IntPtr originalContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            SetCursorPos(x, y);
            SetThreadDpiAwarenessContext(originalContext);
            SendMouseClick(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
            await Task.CompletedTask;
        }

        public async Task DoubleClick_MonitorCoords(int x, int y)
        {
            IntPtr originalContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            SetCursorPos(x, y);
            SetThreadDpiAwarenessContext(originalContext);
            SendMouseClick(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, count: 2);
            await Task.CompletedTask;
        }

        public async Task RightClick_MonitorCoords(int x, int y)
        {
            IntPtr originalContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            SetCursorPos(x, y);
            SetThreadDpiAwarenessContext(originalContext);
            SendMouseClick(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP);
            await Task.CompletedTask;
        }

        public async Task MiddleMouse_MonitorCoords(int x, int y)
        {
            IntPtr originalContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            SetCursorPos(x, y);
            SetThreadDpiAwarenessContext(originalContext);
            SendMouseClick(MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP);
            await Task.CompletedTask;
        }

        private static void SendMouseClick(uint downFlag, uint upFlag, int count = 1)
        {
            INPUT[] inputs = new INPUT[count * 2];

            // Loop the count instead of hard coding just one up and down
            for (int i = 0; i < count; i++)
            {
                inputs[i * 2].type = INPUT_MOUSE;
                inputs[i * 2].u.mi.dwFlags = downFlag;
                inputs[i * 2 + 1].type = INPUT_MOUSE;
                inputs[i * 2 + 1].u.mi.dwFlags = upFlag;
            }

            _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void SendMouseEvent(uint flag)
        {
            INPUT[] inputs =
            [
                new INPUT
                {
                    type = INPUT_MOUSE,
                    u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } }
                }
            ];
            _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>
        /// Injects a hardware mouse-move event via SendInput using absolute normalized coordinates.
        /// Unlike SetCursorPos, this pushes through the hardware input queue so applications
        /// that process WM_MOUSEMOVE from the queue (e.g. during a drag) receive the event.
        /// </summary>
        private static void SendMouseMove(int screenX, int screenY)
        {
            int vScreenWidth  = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vScreenHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            // Normalize to [0, 65535] as required by MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
            int normalizedX = (screenX * 65535 + vScreenWidth  / 2) / vScreenWidth;
            int normalizedY = (screenY * 65535 + vScreenHeight / 2) / vScreenHeight;

            INPUT[] inputs =
            [
                new INPUT
                {
                    type = INPUT_MOUSE,
                    u = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            dx       = normalizedX,
                            dy       = normalizedY,
                            dwFlags  = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
                        }
                    }
                }
            ];
            _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        public async Task ClickDrag_MonitorCoords(int x_start, int y_start, int x_end, int y_end)
        {
            // SetThreadDpiAwarenessContext is thread-local; capture and restore on each segment
            // because await continuations may resume on a different thread-pool thread.
            IntPtr originalContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            // Move to start and press left button down
            SendMouseMove(x_start, y_start);
            SendMouseEvent(MOUSEEVENTF_LEFTDOWN);

            SetThreadDpiAwarenessContext(originalContext);

            // Brief delay so the target application registers the drag initiation
            await Task.Delay(50);

            // Re-apply DPI context on the (potentially new) continuation thread
            originalContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            // Use SendInput (hardware input queue) instead of SetCursorPos so that applications
            // processing WM_MOUSEMOVE during a drag actually receive the move event.
            SendMouseMove(x_end, y_end);

            SetThreadDpiAwarenessContext(originalContext);

            // Brief delay so the cursor movement is recognized before the button release
            await Task.Delay(50);

            SendMouseEvent(MOUSEEVENTF_LEFTUP);
        }

        public enum ScrollMode : int
        {
            WindowMessage = 1,
            SendInput = 2
        }

        private enum ScrollDirection : int
        {
            Up = 1,
            Down = 2
        }

        private void Scroll(ScrollDirection direction, ScrollMode? mode, int multiple)
        {
            ScrollMode scrollMode;
            int scrollAmount = Math.Abs(multiple); // Ensure a sign hasn't been given to multiple already

            // Set sign based on direction
            if (direction == ScrollDirection.Down)
                scrollAmount = -scrollAmount; // Negative for scrolling down

            // Determine mode to use
            if (mode != null)
                scrollMode = (ScrollMode)mode;
            else if (multiple == 1)
                scrollMode = ScrollMode.SendInput; // For single notches, SendInput is more reliable across apps
            else
                scrollMode = ScrollMode.WindowMessage; // For multiple notches, WindowMessage can be more reliable

            // Send the input
            if (scrollMode == ScrollMode.SendInput)
                ScrollMouse_WithSendInput_Async(WHEEL_DELTA * scrollAmount);
            else
                ScrollMouse_WithWM_Async(scrollAmount);
        }

        // ScrollUp - Two overloads
        public async Task ScrollUp(int multiple = 1)
        {
            Scroll(ScrollDirection.Up, null, multiple);
            await Task.CompletedTask;
        }

        public async Task ScrollUp(int multiple, ScrollMode forceScrollMode)
        {
            Scroll(ScrollDirection.Up, forceScrollMode, multiple);
            await Task.CompletedTask;
        }

        // ScrollDown - Two overloads
        public async Task ScrollDown(int multiple = 1)
        {
            Scroll(ScrollDirection.Down, null, multiple);
            await Task.CompletedTask;
        }

        public async Task ScrollDown(int multiple, ScrollMode forceScrollMode)
        {
            Scroll(ScrollDirection.Down, forceScrollMode, multiple);
            await Task.CompletedTask;
        }

        public (int X, int Y) GetCursorPosition()
        {
            IntPtr originalContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            GetCursorPos(out POINT pt);
            SetThreadDpiAwarenessContext(originalContext);
            return (pt.X, pt.Y);
        }

        private void ScrollMouse_WithSendInput_Async(int scrollAmount)
        {
            INPUT[] inputs = new INPUT[1];

            inputs[0].type = INPUT_MOUSE;
            inputs[0].u.mi.dwFlags = MOUSEEVENTF_WHEEL;
            // The cast to uint is required because mouseData is a uint, 
            // and negative scroll amounts will properly underflow to the correct bitwise representation.
            inputs[0].u.mi.mouseData = (uint)scrollAmount;

            _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>
        /// Sends a WM_MOUSEWHEEL message directly to the window or control under the cursor.
        /// Positive multiplier scrolls up, negative scrolls down.
        /// </summary>
        /// <param name="multiplier">Scroll amount; positive = up, negative = down. 1.0 = one standard notch (120 delta).</param>
        /// <param name="forceWindowHandle">When true, targets the top-level window instead of the child control.</param>
        /// <param name="useSendMessage">When true, uses SendMessageTimeout (25 ms) instead of PostMessage.</param>
        /// <param name="targetHandle">Explicit window/control handle to target. Auto-detected from cursor position if null.</param>
        /// <param name="mousePosX">X screen coordinate embedded in the message. Auto-detected if null.</param>
        /// <param name="mousePosY">Y screen coordinate embedded in the message. Auto-detected if null.</param>
        private static void ScrollMouse_WithWM_Async(int multiplier, bool forceWindowHandle = false, bool useSendMessage = false,
            IntPtr? targetHandle = null, int? mousePosX = null, int? mousePosY = null)
        {
            if (targetHandle == null || mousePosX == null || mousePosY == null)
            {
                GetCursorPos(out POINT cursorPos);
                mousePosX ??= cursorPos.X;
                mousePosY ??= cursorPos.Y;

                if (targetHandle == null)
                {
                    IntPtr windowHandle = WindowFromPoint(new POINT { X = cursorPos.X, Y = cursorPos.Y });

                    if (!forceWindowHandle)
                    {
                        // RealChildWindowFromPoint needs parent-relative client coordinates
                        POINT clientPos = new POINT { X = cursorPos.X, Y = cursorPos.Y };
                        ScreenToClient(windowHandle, ref clientPos);
                        IntPtr controlHandle = RealChildWindowFromPoint(windowHandle, clientPos);
                        targetHandle = controlHandle != IntPtr.Zero ? controlHandle : windowHandle;
                    }
                    else
                    {
                        targetHandle = windowHandle;
                    }
                }
            }

            // 120 is the standard delta for one scroll notch, regardless of the system scroll lines setting
            int delta = (int)Math.Round(120.0 * multiplier);
            IntPtr wParam = new IntPtr(delta << 16);                                              // delta in high word, key flags (0) in low word
            IntPtr lParam = new IntPtr((mousePosY.Value << 16) | (mousePosX.Value & 0xFFFF));    // y in high word, x in low word

            if (useSendMessage)
            {
                // PostMessage is generally safer; SendMessageTimeout used when the caller needs synchronous delivery.
                // The short timeout means we won't block if the target window is unresponsive.
                try
                {
                    SendMessageTimeout(targetHandle.Value, WM_MOUSEWHEEL, wParam, lParam, SMTO_NORMAL, 25, out _);
                }
                catch { } // We don't care if it doesn't work perfectly
            }
            else
            {
                PostMessage(targetHandle.Value, WM_MOUSEWHEEL, wParam, lParam);
            }
        }


    } // End of WindowsInputProvider class


} // End namespace