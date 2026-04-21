namespace MoergoLayerViz.Core.Input;

/// <summary>
/// Direction of a key event.
/// </summary>
public enum KeyEventKind
{
    Pressed,
    Released,
}

/// <summary>
/// A platform-agnostic key event. Keycode names follow the ZMK convention
/// ("A", "F20", "N1") so they can be matched directly against macro signal
/// params without a translation table.
/// </summary>
/// <param name="Keycode">ZMK-style name, e.g. "A", "F20", "LSHFT".</param>
/// <param name="Kind">Pressed or released.</param>
public sealed record KeyEvent(string Keycode, KeyEventKind Kind);

/// <summary>
/// Abstracts the global keyboard hook so the runtime logic
/// (<see cref="HotkeyLayerTracker"/>) can be unit-tested with a fake source.
/// Concrete implementation in the App project wraps SharpHook.
/// </summary>
public interface IKeyEventSource : IDisposable
{
    /// <summary>Raised on the hook thread for every intercepted key event.</summary>
    event Action<KeyEvent>? KeyEvent;

    /// <summary>Starts the hook (idempotent).</summary>
    void Start();

    /// <summary>Stops the hook but keeps the object reusable.</summary>
    void Stop();
}
