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

        // Dictionary to store virtual key codes and scan codes. Will want to use wscan codes for SendInput
        private static readonly Dictionary<string, (ushort vk, ushort scan)> modifierKeyCodes = new Dictionary<string, (ushort, ushort)>
        {
            {"LCTRL", (0x11, 29)},
            {"LSHIFT", (0x10, 42)},
            {"LALT", (0x12, 56)},
        };

        // Flags for KEYBDINPUT structure used in API calls
        // Reference: https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput
        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_KEYDOWN = 0x0000; //TODO: See if this needs to be used anywhere
        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint KEYEVENTF_SCANCODE = 0x0008;
        const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        const uint KEYEVENTF_UNICODE = 0x0004;

        public const int INPUT_MOUSE = 0;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;

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

        public enum MapVirtualKeyType : uint
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


        public async Task SendModKeyComboAsync(string key, bool? ctrl = null, bool? shift = null, bool? alt = null)
        {
            TextCharCode keyChar = new TextCharCode(key);

            // If shift state wasn't provided, default to the state from the key
            if (shift == null)
                shift = keyChar.shiftState;

            if (ctrl == null)
                ctrl = false;

            if (alt == null)
                alt = false;

            // Array to contain list of individual key up and down events in sequence
            List<INPUT> inputList = new();

            // Add modifier keys down
            if (ctrl == true)
                inputList.Add(CreateInput(vk: modifierKeyCodes["LCTRL"].vk, scan: modifierKeyCodes["LCTRL"].scan, isKeyUp: false, extended: false));
            if (shift == true)
                inputList.Add(CreateInput(vk: modifierKeyCodes["LSHIFT"].vk, scan: modifierKeyCodes["LSHIFT"].scan, isKeyUp: false, extended: false));
            if (alt == true)
                inputList.Add(CreateInput(vk: modifierKeyCodes["LALT"].vk, scan: modifierKeyCodes["LALT"].scan, isKeyUp: false, extended: false));

            // Add main key down and up
            inputList.Add(CreateInput(vk: keyChar.vk, scan: keyChar.scan, isKeyUp: false, extended: false));
            inputList.Add(CreateInput(vk: keyChar.vk, scan: keyChar.scan, isKeyUp: true, extended: false));

            // Add modifier keys up
            if (ctrl == true)
                inputList.Add(CreateInput(vk: modifierKeyCodes["LCTRL"].vk, scan: modifierKeyCodes["LCTRL"].scan, isKeyUp: true, extended: false));
            if (shift == true)
                inputList.Add(CreateInput(vk: modifierKeyCodes["LSHIFT"].vk, scan: modifierKeyCodes["LSHIFT"].scan, isKeyUp: true, extended: false));
            if (alt == true)
                inputList.Add(CreateInput(vk: modifierKeyCodes["LALT"].vk, scan: modifierKeyCodes["LALT"].scan, isKeyUp: true, extended: false));
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



    } // End of WindowsInputProvider class


} // End namespace