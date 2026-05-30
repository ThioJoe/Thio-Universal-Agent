// Thio-Universal-Agent/OS_Windows/WindowsInputProvider.cs
using System.Runtime.InteropServices;

#pragma warning disable CS0162 // Unreachable code detected

namespace Thio_Universal_Agent.OS_Windows
{
    public partial class WindowsInputProvider : IInputProvider
    {

     #if HUMAN_ONLY
        public bool HumanControlOnlyMode => true;
     #else
        public bool HumanControlOnlyMode => _appConfig.General.HumanControlOnlyMode;
     #endif

        /// <inheritdoc/>
        public Action? HumanClickCallback { get; set; }

        /// <inheritdoc/>
        public Action? HumanKeyComboCallback { get; set; }

        private readonly AppConfig _appConfig;
        private readonly IScreenProvider _screenProvider;

        // ----------- CONSTRUCTOR -----------
        public WindowsInputProvider(AppConfig appConfig, IScreenProvider screenProvider)
        {
            _appConfig = appConfig;
            _screenProvider = screenProvider;
        }
        // -----------------------------------

        public async Task SendModKeyComboAsync(string? key, bool? ctrl = null, bool? shift = null, bool? alt = null, bool? win = null)
        {
            if (HumanControlOnlyMode)
            {
                if (HumanKeyComboCallback is not null)
                {
                    ModifierKeys modifiers = GetModifierFlags(ctrl, shift, alt, win);
                    int? virtualKey = TryResolveHumanComboVirtualKey(key);
                    if (virtualKey is not null || modifiers != ModifierKeys.None)
                        _screenProvider.RegisterHumanKeyComboWatcher(virtualKey, modifiers, HumanKeyComboCallback);
                }

                return; // HUMAN CONTROL ONLY GUARD
            }

         #if !HUMAN_ONLY
            await EnsureCursorOnTargetMonitorAsync();

            // Make all of them nullable in case there is no primary key, meaning only modifier keys are pressed
            ushort? vk = null;
            ushort? scan = null;
            bool? extended = null;

            if (key != null)
            {
                // Try named keys first (win, enter, tab, f1, etc.) — TextCharCode only handles single typeable characters
                if (NamedKeys.TryGetValue(key, out (ushort vk, bool extended) named))
                {
                    vk = named.vk;
                    scan = MapVirtualKey((ushort)vk, (uint)MapVirtualKeyType.MAPVK_VK_TO_VSC);
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
            }
            // Ensure something is pressed or just skip. Should have already been caught but we'll return early to avoid an unnecessary API call
            else if (key == null && ctrl == false && shift == false && alt == false && win == false)
            {
                return;
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

            if (key != null && vk != null && scan != null && extended != null)
            {
                // Add main key down and up
                inputList.Add(CreateInput(vk: (ushort)vk, scan: (ushort)scan, isKeyUp: false, extended: (bool)extended));
                inputList.Add(CreateInput(vk: (ushort)vk, scan: (ushort)scan, isKeyUp: true, extended: (bool)extended));
            }

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

         #endif
        }

        private static ModifierKeys GetModifierFlags(bool? ctrl, bool? shift, bool? alt, bool? win)
        {
            ModifierKeys modifiers = ModifierKeys.None;
            if (ctrl == true) modifiers |= ModifierKeys.Ctrl;
            if (shift == true) modifiers |= ModifierKeys.Shift;
            if (alt == true) modifiers |= ModifierKeys.Alt;
            if (win == true) modifiers |= ModifierKeys.Win;
            return modifiers;
        }

        private static int? TryResolveHumanComboVirtualKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return null;

            if (NamedKeys.TryGetValue(key, out (ushort vk, bool extended) named))
                return named.vk;

            if (key.Length != 1)
                return null;

            TextCharCode keyChar = new TextCharCode(key);
            return keyChar.vk == 0xFFFF ? null : keyChar.vk;
        }

#if !HUMAN_ONLY
        /// <summary>
        /// Types the specified text using the helper methods to construct inputs.
        /// This handles shift states for standard keys and falls back to Unicode for others.
        /// </summary>
        public async Task TypeTextAsync(string text)
        {
            if (HumanControlOnlyMode) return; // HUMAN CONTROL ONLY GUARD

            if (string.IsNullOrEmpty(text)) return;

            await EnsureCursorOnTargetMonitorAsync();

            // Convert the string into a list of TextCharCode objects.
            // TextCharCode logic determines if a character is a standard key (needing Shift/VK)
            // or if it should be treated as a Unicode packet.
            List<TextCharCode> charCodeList = new List<TextCharCode>();

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
#endif

#if !HUMAN_ONLY
        private async Task EnsureCursorOnTargetMonitorAsync()
        {
            if (HumanControlOnlyMode) return; // HUMAN CONTROL ONLY GUARD

            try
            {
                var monitors = _screenProvider.GetMonitors();
                int? monitorIndex = _appConfig.Agent.MonitorIndex;

                if (monitorIndex.HasValue && monitorIndex.Value >= 0 && monitorIndex.Value < monitors.Count)
                {
                    var m = monitors[monitorIndex.Value];
                    (int currentX, int currentY) = GetCursorPosition();

                    // Check if cursor is outside the target monitor bounds
                    if (currentX < m.X || currentX >= m.X + m.Width ||
                        currentY < m.Y || currentY >= m.Y + m.Height)
                    {
                        // Move to the center of the target monitor
                        int targetX = m.X + (m.Width / 2);
                        int targetY = m.Y + (m.Height / 2);

                        SendMouseMove(targetX, targetY, suppressMarker: true);

                        // Brief pause to allow Windows to update the active Taskbar context
                        await Task.Delay(50);
                    }
                }
            }
            catch
            {
                // Fail silently to avoid breaking input if monitor data is temporarily inaccessible
            }
        }
#endif

        // Mouse Events
        public async Task MoveMouse_MonitorCoords(int x, int y)
        {
            #if !HUMAN_ONLY
            SendMouseMove(x, y);
            #endif
            await Task.CompletedTask;
        }

        public async Task LeftClick_MonitorCoords(int x, int y)
        {
            if (HumanControlOnlyMode == true)
            {
                _screenProvider.DrawClickPointMarker(x, y, int.MaxValue, 200, onClicked: HumanClickCallback);
            }
            else if (_appConfig.General.ShowClickMarkersDuration > 0)
            {
                _screenProvider.DrawClickPointMarker(x, y, _appConfig.General.ShowClickMarkersDuration);
            }

            if (HumanControlOnlyMode) return; // HUMAN CONTROL ONLY GUARD

         #if !HUMAN_ONLY
            SendMouseMove(x, y);

            await Task.Yield();

            SendMouseClick(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
         #endif
        }

        public async Task DoubleClick_MonitorCoords(int x, int y)
        {
            if (HumanControlOnlyMode == true)
            {
                _screenProvider.DrawClickPointMarker(x, y, int.MaxValue, 200, onClicked: HumanClickCallback);
            }
            else if (_appConfig.General.ShowClickMarkersDuration > 0)
            {
                _screenProvider.DrawClickPointMarker(x, y, _appConfig.General.ShowClickMarkersDuration);
            }

            if (HumanControlOnlyMode) return; // HUMAN CONTROL ONLY GUARD

         #if !HUMAN_ONLY
            SendMouseMove(x, y);

            await Task.Yield();

            SendMouseClick(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, count: 2);
         #endif

        }

        public async Task RightClick_MonitorCoords(int x, int y)
        {
            if (HumanControlOnlyMode == true)
            {
                _screenProvider.DrawClickPointMarker(x, y, int.MaxValue, 200, onClicked: HumanClickCallback);
            }
            else if (_appConfig.General.ShowClickMarkersDuration > 0)
            {
                _screenProvider.DrawClickPointMarker(x, y, _appConfig.General.ShowClickMarkersDuration);
            }

         #if !HUMAN_ONLY
            if (HumanControlOnlyMode) return; // HUMAN CONTROL ONLY GUARD

            SendMouseMove(x, y);

            await Task.Yield();

            SendMouseClick(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP);
         #endif

        }

        public async Task MiddleMouse_MonitorCoords(int x, int y)
        {
            if (HumanControlOnlyMode == true)
            {
                _screenProvider.DrawClickPointMarker(x, y, int.MaxValue, 200, onClicked: HumanClickCallback);
            }
            else if (_appConfig.General.ShowClickMarkersDuration > 0)
            {
                _screenProvider.DrawClickPointMarker(x, y, _appConfig.General.ShowClickMarkersDuration);
            }

            if (HumanControlOnlyMode) return; // HUMAN CONTROL ONLY GUARD

         #if !HUMAN_ONLY
            SendMouseMove(x, y);

            await Task.Yield();

            SendMouseClick(MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP);
         #endif

        }

#if !HUMAN_ONLY
        private void SendMouseClick(uint downFlag, uint upFlag, int count = 1)
        {
            if (HumanControlOnlyMode) return; // HUMAN CONTROL ONLY GUARD

            INPUT[] inputs = new INPUT[count * 2];

            for (int i = 0; i < count; i++)
            {
                inputs[i * 2].type = INPUT_MOUSE;
                inputs[i * 2].u.mi.dwFlags = downFlag;
                inputs[(i * 2) + 1].type = INPUT_MOUSE;
                inputs[(i * 2) + 1].u.mi.dwFlags = upFlag;
            }

            _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }
#endif

#if !HUMAN_ONLY
        /// <summary>
        /// Injects a hardware mouse-move event via SendInput using absolute normalized coordinates.
        /// Unlike SetCursorPos, this pushes through the hardware input queue so applications
        /// that process WM_MOUSEMOVE from the queue (e.g. during a drag) receive the event.
        /// </summary>
        private void SendMouseMove(int screenX, int screenY, bool suppressMarker = false)
        {
            IntPtr originalContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            int vScreenLeft   = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
            int vScreenTop    = GetSystemMetrics(77); // SM_YVIRTUALSCREEN
            int vScreenWidth  = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vScreenHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            SetThreadDpiAwarenessContext(originalContext);

            // Normalize to [0, 65535]. Subtracting Left/Top is crucial to handle multi-monitor setups correctly.
            int normalizedX = (int)(((screenX - vScreenLeft) * 65536.0) / vScreenWidth);
            int normalizedY = (int)(((screenY - vScreenTop) * 65536.0) / vScreenHeight);

            // Draw arrow
            if (!suppressMarker)
            {
                if (HumanControlOnlyMode == true)
                {
                    _screenProvider.DrawMouseMoveMarker(screenX, screenY, int.MaxValue, 200);
                }
                else if (_appConfig.General.ShowClickMarkersDuration > 0)
                {
                    _screenProvider.DrawMouseMoveArrow(screenX, screenY, _appConfig.General.ShowClickMarkersDuration);
                }
            }

            if (HumanControlOnlyMode) return; // HUMAN CONTROL ONLY GUARD

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
#endif

        public async Task ClickDrag_MonitorCoords(int x_start, int y_start, int x_end, int y_end)
        {
            if (HumanControlOnlyMode == true)
            {
                _screenProvider.DrawClickDragMarker(x_start, y_start, x_end, y_end, int.MaxValue, 200);
            }
            else if (_appConfig.General.ShowClickMarkersDuration > 0)
            {
                _screenProvider.DrawClickDragMarker(x_start, y_start, x_end, y_end, _appConfig.General.ShowClickMarkersDuration);
            }

            if (HumanControlOnlyMode) return; // HUMAN CONTROL ONLY GUARD

         #if !HUMAN_ONLY

            SendMouseMove(x_start, y_start, suppressMarker: true);

            await Task.Yield();

            SendMouseEvent(MOUSEEVENTF_LEFTDOWN);

            await Task.Yield();

            int steps = 10;
            for (int i = 1; i <= steps; i++)
            {
                int currentX = x_start + ((x_end - x_start) * i / steps);
                int currentY = y_start + ((y_end - y_start) * i / steps);
                SendMouseMove(currentX, currentY, suppressMarker: true);

                await Task.Yield();
            }

            await Task.Yield();

            SendMouseEvent(MOUSEEVENTF_LEFTUP);

         #endif

        }

        public async Task ScrollUp(int multiple = 1)
        {
            Scroll(ScrollDirection.Up, multiple);
            await Task.CompletedTask;
        }

        public async Task ScrollDown(int multiple = 1)
        {
            Scroll(ScrollDirection.Down, multiple);
            await Task.CompletedTask;
        }

        public (int X, int Y) GetCursorPosition()
        {
            IntPtr originalContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            GetCursorPos(out POINT pt);
            SetThreadDpiAwarenessContext(originalContext);
            return (pt.X, pt.Y);
        }


    } // End of WindowsInputProvider class


} // End namespace