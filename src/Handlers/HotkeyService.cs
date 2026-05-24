// Thio-Universal-Agent/Handlers/HotkeyService.cs
namespace Thio_Universal_Agent.Handlers;

/// <summary>
/// Hosted service that registers configurable system-wide hotkeys via <see cref="IHotkeyProvider"/>
/// and routes them to the appropriate <see cref="AgentSessionManager"/> action.
/// Call <see cref="ReloadHotkeys"/> after mutating <see cref="AppConfig.Hotkeys"/> to apply changes immediately.
/// </summary>
public sealed class HotkeyService : IHostedService
{
    private const int IdPauseResume = 1;
    private const int IdStop        = 2;

    private readonly IHotkeyProvider      _provider;
    private readonly AgentSessionManager  _sessions;
    private readonly AppConfig            _config;
    private readonly ILogger<HotkeyService> _logger;

    // Track which IDs are currently registered so ReloadHotkeys can cleanly swap them.
    private readonly HashSet<int> _registeredIds = [];

    public HotkeyService(
        IHotkeyProvider provider,
        AgentSessionManager sessions,
        AppConfig config,
        ILogger<HotkeyService> logger)
    {
        _provider = provider;
        _sessions = sessions;
        _config   = config;
        _logger   = logger;

        _provider.HotkeyPressed += OnHotkeyPressed;
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RegisterFromConfig();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        UnregisterAll();
        return Task.CompletedTask;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Unregisters any active hotkeys and re-registers from the current <see cref="AppConfig.Hotkeys"/>.
    /// Call this after the config has been mutated via the settings page.
    /// </summary>
    public void ReloadHotkeys()
    {
        UnregisterAll();
        RegisterFromConfig();
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private void RegisterFromConfig()
    {
        if (!_config.Hotkeys.Enabled)
        {
            _logger.LogInformation("Global hotkeys are disabled in config.");
            return;
        }

        TryRegister(IdPauseResume, _config.Hotkeys.PauseResumeHotkey);
        TryRegister(IdStop,        _config.Hotkeys.StopHotkey);
    }

    private void TryRegister(int id, string hotkeyString)
    {
        if (!HotkeyStringParser.TryParse(hotkeyString, out var modifiers, out var vk))
        {
            _logger.LogWarning("Could not parse hotkey string \"{Hotkey}\" (id={Id}); skipping.", hotkeyString, id);
            return;
        }

        try
        {
            _provider.RegisterHotkey(id, modifiers, vk);
            _registeredIds.Add(id);
            _logger.LogInformation("Registered global hotkey id={Id} as \"{Hotkey}\".", id, hotkeyString);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register hotkey id={Id} (\"{Hotkey}\").", id, hotkeyString);
        }
    }

    private void UnregisterAll()
    {
        foreach (int id in _registeredIds)
            _provider.UnregisterHotkey(id);

        _registeredIds.Clear();
    }

    private void OnHotkeyPressed(int id)
    {
        AgentSession? session = _sessions.GetActiveSession();
        if (session is null) return;

        switch (id)
        {
            case IdPauseResume:
                if (session.IsPaused)
                {
                    _logger.LogInformation("Hotkey: resuming session {Id}.", session.SessionId);
                    _sessions.ResumeSession(session.SessionId);
                }
                else
                {
                    _logger.LogInformation("Hotkey: pausing session {Id}.", session.SessionId);
                    _sessions.PauseSession(session.SessionId);
                }
                break;

            case IdStop:
                _logger.LogInformation("Hotkey: stopping session {Id}.", session.SessionId);
                _sessions.StopSession(session.SessionId);
                break;
        }
    }
}

// ── Hotkey string parser ──────────────────────────────────────────────────────

/// <summary>
/// Parses a human-readable hotkey string such as "Ctrl+Shift+P" into the
/// <see cref="HotkeyModifiers"/> flags and Win32 virtual-key code required by
/// <see cref="IHotkeyProvider.RegisterHotkey"/>.
/// </summary>
internal static class HotkeyStringParser
{
    // Map of special key names → Win32 virtual-key codes.
    private static readonly Dictionary<string, int> SpecialKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Escape"] = 0x1B,
        ["Esc"]    = 0x1B,
        ["Tab"]    = 0x09,
        ["Space"]  = 0x20,
        ["Enter"]  = 0x0D,
        ["Return"] = 0x0D,
        ["Back"]   = 0x08,
        ["Delete"] = 0x2E,
        ["Del"]    = 0x2E,
        ["Insert"] = 0x2D,
        ["Ins"]    = 0x2D,
        ["Home"]   = 0x24,
        ["End"]    = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Left"]   = 0x25,
        ["Up"]     = 0x26,
        ["Right"]  = 0x27,
        ["Down"]   = 0x28,
        // F-keys
        ["F1"]  = 0x70, ["F2"]  = 0x71, ["F3"]  = 0x72, ["F4"]  = 0x73,
        ["F5"]  = 0x74, ["F6"]  = 0x75, ["F7"]  = 0x76, ["F8"]  = 0x77,
        ["F9"]  = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
    };

    /// <summary>
    /// Attempts to parse <paramref name="hotkeyString"/> into modifier flags and a virtual-key code.
    /// Returns false if the string is empty, has no non-modifier key, or contains an unrecognised token.
    /// </summary>
    public static bool TryParse(string hotkeyString, out HotkeyModifiers modifiers, out int virtualKey)
    {
        modifiers  = HotkeyModifiers.None;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(hotkeyString))
            return false;

        var tokens = hotkeyString.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        // All tokens except the last are treated as modifiers; the last is the key.
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            switch (tokens[i].ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL": modifiers |= HotkeyModifiers.Control; break;
                case "SHIFT":   modifiers |= HotkeyModifiers.Shift;   break;
                case "ALT":     modifiers |= HotkeyModifiers.Alt;     break;
                case "WIN":
                case "WINDOWS": modifiers |= HotkeyModifiers.Win;     break;
                default: return false; // unrecognised modifier
            }
        }

        string keyToken = tokens[^1];

        // Special named keys
        if (SpecialKeys.TryGetValue(keyToken, out int vk))
        {
            virtualKey = vk;
            return true;
        }

        // Single digit 0–9 → VK_0–VK_9 (0x30–0x39)
        if (keyToken.Length == 1 && char.IsAsciiDigit(keyToken[0]))
        {
            virtualKey = keyToken[0]; // '0'=0x30 … '9'=0x39
            return true;
        }

        // Single letter A–Z → VK_A–VK_Z (0x41–0x5A)
        if (keyToken.Length == 1 && char.IsAsciiLetter(keyToken[0]))
        {
            virtualKey = char.ToUpperInvariant(keyToken[0]);
            return true;
        }

        return false; // unrecognised key
    }
}
