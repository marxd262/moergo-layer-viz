using SharpHook;
using SharpHook.Data;

namespace MoergoLayerViz.App.Services;

/// <summary>
/// Listens for a global hotkey using SharpHook and fires a callback.
/// Works cross-platform (Windows, macOS, Linux). On macOS, requires
/// Accessibility permission (OS prompts automatically on first use).
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    private readonly SharpHookProvider _provider;
    private bool _started;

    public GlobalHotkeyService(SharpHookProvider provider)
    {
        _provider = provider;
    }

    /// <summary>Fired on the hook thread when the hotkey is pressed.</summary>
    public Action? HotkeyPressed { get; set; }

    /// <summary>The key to listen for (default: F12).</summary>
    public KeyCode Key { get; set; } = KeyCode.VcF12;

    /// <summary>Required modifier mask (default: None).</summary>
    public EventMask Modifiers { get; set; } = EventMask.None;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _provider.KeyPressed += OnKeyPressed;
        _provider.Acquire();
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (e.Data.KeyCode == Key &&
            (Modifiers == EventMask.None || (e.RawEvent.Mask & Modifiers) == Modifiers))
        {
            HotkeyPressed?.Invoke();
        }
    }

    /// <summary>Updates the key/modifier to listen for.</summary>
    public void UpdateHotkey(KeyCode key, EventMask modifiers)
    {
        Key = key;
        Modifiers = modifiers;
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _provider.KeyPressed -= OnKeyPressed;
        _provider.Release();
    }

    /// <summary>Parses a key name (e.g. "F12") to a SharpHook KeyCode.</summary>
    public static KeyCode ParseKey(string name) => Enum.Parse<KeyCode>($"Vc{name}");

    /// <summary>Parses a modifier name (e.g. "None", "Ctrl") to a SharpHook EventMask.</summary>
    public static EventMask ParseModifiers(string name)
    {
        if (string.IsNullOrEmpty(name) || name == "None")
            return EventMask.None;
        return Enum.Parse<EventMask>(name);
    }

    public void Dispose()
    {
        Stop();
    }
}
