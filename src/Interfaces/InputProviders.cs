// Thio-Universal-Agent/IInputProvider.cs
namespace Thio_Universal_Agent
{
    /// <summary>
    /// Interface for providing input capabilities to the AI agent across different operating systems.
    /// </summary>
    public interface IInputProvider
    {
        bool HumanControlOnlyMode { get; }

        /// <summary>
        /// Simulates typing a string of text.        /// </summary>
        /// <param name="text">The text to type.</param>
        Task TypeTextAsync(string text);

        /// <summary>
        /// Simulates pressing a single key with or without modifiers.
        /// </summary>
        Task SendModKeyComboAsync(string? key, bool? ctrl = null, bool? shift = null, bool? alt = null, bool? win = null);

        // Methods that use coordinates relative to the entire screen as opposed to within an individual window
        Task LeftClick_MonitorCoords(int x, int y);
        Task DoubleClick_MonitorCoords(int x, int y);
        Task RightClick_MonitorCoords(int x, int y);
        Task MiddleMouse_MonitorCoords(int x, int y);
        Task MoveMouse_MonitorCoords(int x, int y);
        Task ClickDrag_MonitorCoords(int x_start, int y_start, int x_end, int y_end);
        Task ScrollUp(int multiple);
        Task ScrollDown(int multiple);

        /// <summary>
        /// Returns the current cursor position in absolute screen coordinates.
        /// </summary>
        (int X, int Y) GetCursorPosition() => (0, 0);

        /// <summary>
        /// Sends key-down events for the specified modifier keys so they are held during a subsequent mouse action.
        /// Call <see cref="ReleaseModifierKeys"/> after the mouse action to release them.
        /// </summary>
        void HoldModifierKeys(ModifierKeys modifiers) { }

        /// <summary>
        /// Sends key-up events for the specified modifier keys, releasing them after a mouse action.
        /// </summary>
        void ReleaseModifierKeys(ModifierKeys modifiers) { }

        /// <summary>
        /// In human-control-only mode, this callback is passed to <see cref="IScreenProvider.DrawClickPointMarker"/>
        /// so that clicking near the marker auto-dismisses it. Set by the agent loop before pre-executing a
        /// click-only step or batch; reset to <c>null</c> afterward. Ignored outside human-control mode.
        /// </summary>
        Action? HumanClickCallback { get; set; }

        /// <summary>
        /// In human-control-only mode, this callback is invoked after the requested key combo is
        /// physically detected from the operator. Set by the agent loop before pre-executing an
        /// auto-advance key-combo step or batch; reset to <c>null</c> afterward.
        /// </summary>
        Action? HumanKeyComboCallback { get; set; }
    }
}