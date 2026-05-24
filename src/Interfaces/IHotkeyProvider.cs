// Thio-Universal-Agent/Interfaces/IHotkeyProvider.cs
namespace Thio_Universal_Agent;

/// <summary>
/// Modifier key flags used when registering a global hotkey.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None     = 0x0000,
    Alt      = 0x0001,
    Control  = 0x0002,
    Shift    = 0x0004,
    Win      = 0x0008,
    /// <summary>
    /// Prevents the hotkey from firing repeatedly while the keys are held down.
    /// Maps to MOD_NOREPEAT (0x4000).
    /// </summary>
    NoRepeat = 0x4000,
}

/// <summary>
/// Platform-agnostic contract for registering and receiving system-wide hotkeys.
/// </summary>
public interface IHotkeyProvider : IDisposable
{
    /// <summary>
    /// Fired when a registered hotkey is pressed. The argument is the <paramref name="id"/>
    /// passed to <see cref="RegisterHotkey"/>.
    /// </summary>
    event Action<int> HotkeyPressed;

    /// <summary>
    /// Registers a global hotkey combination.
    /// </summary>
    /// <param name="id">Caller-defined integer ID used to identify the hotkey in <see cref="HotkeyPressed"/>.</param>
    /// <param name="modifiers">Modifier keys that must be held.</param>
    /// <param name="virtualKey">Virtual-key code for the non-modifier key (e.g. 0x50 for 'P').</param>
    /// <exception cref="InvalidOperationException">Thrown if registration fails (e.g. the combination is already in use).</exception>
    void RegisterHotkey(int id, HotkeyModifiers modifiers, int virtualKey);

    /// <summary>
    /// Unregisters a previously registered hotkey by its ID. No-op if the ID was never registered.
    /// </summary>
    void UnregisterHotkey(int id);
}
