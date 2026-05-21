// Thio-Universal-Agent/IInputProvider.cs
using System.Threading.Tasks;

namespace Thio_Universal_Agent
{
    /// <summary>
    /// Interface for providing input capabilities to the AI agent across different operating systems.
    /// </summary>
    public interface IInputProvider
    {
        /// <summary>
        /// Simulates typing a string of text.
        /// </summary>
        /// <param name="text">The text to type.</param>
        Task TypeTextAsync(string text);

        /// <summary>
        /// Simulates pressing a single key with or without modifiers.
        /// </summary>
        Task SendModKeyComboAsync(string key, bool? ctrl = null, bool? shift = null, bool? alt = null, bool? win = null);

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
    }


}