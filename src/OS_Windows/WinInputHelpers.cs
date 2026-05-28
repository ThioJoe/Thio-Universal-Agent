using System.Runtime.InteropServices;

namespace Thio_Universal_Agent.OS_Windows;

public partial class WindowsInputProvider : IInputProvider
{
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
                this.scan = getScanCode(vk);
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

        private static ushort getScanCode(ushort vkCode) => MapVirtualKey(vkCode, (uint)MapVirtualKeyType.MAPVK_VK_TO_VSC);

        private static ushort[] UnicodeToUShortArray(string inputChar)
        {
            List<ushort> result = new List<ushort>();
            result.AddRange(inputChar.Select(c => (ushort)c));
            ushort[] finalArray = result.ToArray();
            return finalArray;
        }
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

    /// <inheritdoc/>
    public void HoldModifierKeys(ModifierKeys modifiers)
    {
        List<INPUT> inputList = [];
        if (modifiers.HasFlag(ModifierKeys.Win))
            inputList.Add(CreateInput(vk: modifierKeyCodes["LWIN"].vk, scan: modifierKeyCodes["LWIN"].scan, isKeyUp: false, extended: modifierKeyCodes["LWIN"].extended));
        if (modifiers.HasFlag(ModifierKeys.Ctrl))
            inputList.Add(CreateInput(vk: modifierKeyCodes["LCTRL"].vk, scan: modifierKeyCodes["LCTRL"].scan, isKeyUp: false, extended: false));
        if (modifiers.HasFlag(ModifierKeys.Shift))
            inputList.Add(CreateInput(vk: modifierKeyCodes["LSHIFT"].vk, scan: modifierKeyCodes["LSHIFT"].scan, isKeyUp: false, extended: false));
        if (modifiers.HasFlag(ModifierKeys.Alt))
            inputList.Add(CreateInput(vk: modifierKeyCodes["LALT"].vk, scan: modifierKeyCodes["LALT"].scan, isKeyUp: false, extended: false));
        if (inputList.Count > 0)
            SendInput((uint)inputList.Count, [.. inputList], Marshal.SizeOf(typeof(INPUT)));
    }

    /// <inheritdoc/>
    public void ReleaseModifierKeys(ModifierKeys modifiers)
    {
        List<INPUT> inputList = [];
        if (modifiers.HasFlag(ModifierKeys.Alt))
            inputList.Add(CreateInput(vk: modifierKeyCodes["LALT"].vk, scan: modifierKeyCodes["LALT"].scan, isKeyUp: true, extended: false));
        if (modifiers.HasFlag(ModifierKeys.Shift))
            inputList.Add(CreateInput(vk: modifierKeyCodes["LSHIFT"].vk, scan: modifierKeyCodes["LSHIFT"].scan, isKeyUp: true, extended: false));
        if (modifiers.HasFlag(ModifierKeys.Ctrl))
            inputList.Add(CreateInput(vk: modifierKeyCodes["LCTRL"].vk, scan: modifierKeyCodes["LCTRL"].scan, isKeyUp: true, extended: false));
        if (modifiers.HasFlag(ModifierKeys.Win))
            inputList.Add(CreateInput(vk: modifierKeyCodes["LWIN"].vk, scan: modifierKeyCodes["LWIN"].scan, isKeyUp: true, extended: modifierKeyCodes["LWIN"].extended));
        if (inputList.Count > 0)
            SendInput((uint)inputList.Count, [.. inputList], Marshal.SizeOf(typeof(INPUT)));
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
        else
            scrollMode = ScrollMode.SendInput; // SendInput is more reliable across apps

        // Send the input
        if (scrollMode == ScrollMode.SendInput)
            ScrollMouse_WithSendInput_Async(WHEEL_DELTA * scrollAmount);
        else
            ScrollMouse_WithWM_Async(scrollAmount);
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
        IntPtr wParam = (IntPtr)((delta & 0xFFFF) << 16);                                                          // delta in high word, key flags (0) in low word
        IntPtr lParam = (IntPtr)(((mousePosY.Value & 0xFFFF) << 16) | (mousePosX.Value & 0xFFFF));                 // y in high word, x in low word

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

}
