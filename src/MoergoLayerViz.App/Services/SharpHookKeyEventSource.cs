using MoergoLayerViz.Core.Diagnostics;
using MoergoLayerViz.Core.Input;
using SharpHook;
using SharpHook.Data;

namespace MoergoLayerViz.App.Services;

/// <summary>
/// Adapts SharpHook's <see cref="SimpleGlobalHook"/> to the Core
/// <see cref="IKeyEventSource"/> abstraction, translating
/// <see cref="KeyCode"/> enum values to ZMK-style keycode strings
/// ("A", "F20", "N1", ...) that match what the Moergo editor writes
/// into signal-macro params.
/// <para>
/// Not supported on Linux/Wayland (the compositor blocks global hooks
/// for unfocused windows); callers gate construction on
/// <c>!OperatingSystem.IsLinux()</c>.
/// </para>
/// </summary>
public sealed class SharpHookKeyEventSource : IKeyEventSource
{
    private SimpleGlobalHook? _hook;

    public event Action<KeyEvent>? KeyEvent;

    public void Start()
    {
        if (_hook is not null) return;

        _hook = new SimpleGlobalHook();
        _hook.KeyPressed += OnKeyPressed;
        _hook.KeyReleased += OnKeyReleased;
        _hook.RunAsync();
        DiagnosticLog.Info("KeyEventSource", "Global hook started");
    }

    public void Stop()
    {
        if (_hook is null) return;
        _hook.KeyPressed -= OnKeyPressed;
        _hook.KeyReleased -= OnKeyReleased;
        _hook.Dispose();
        _hook = null;
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        var zmk = ToZmkKeycode(e.Data.KeyCode);
        if (zmk is null) return;
        KeyEvent?.Invoke(new KeyEvent(zmk, KeyEventKind.Pressed));
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        var zmk = ToZmkKeycode(e.Data.KeyCode);
        if (zmk is null) return;
        KeyEvent?.Invoke(new KeyEvent(zmk, KeyEventKind.Released));
    }

    /// <summary>
    /// Translates a SharpHook <see cref="KeyCode"/> (values prefixed "Vc")
    /// to the ZMK keycode string the Moergo editor embeds in signal-macro
    /// params. Returns null for keys we don't have a ZMK equivalent for.
    /// </summary>
    private static string? ToZmkKeycode(KeyCode code)
    {
        // Strip "Vc" prefix. SharpHook names include VcA, VcB, VcF1, Vc1, etc.
        var name = code.ToString();
        if (!name.StartsWith("Vc", StringComparison.Ordinal)) return null;
        var stripped = name[2..];

        // ZMK number keys are "N1".."N0". Everything else matches by name.
        if (stripped.Length == 1 && stripped[0] >= '0' && stripped[0] <= '9')
            return "N" + stripped;

        return stripped;
    }

    public void Dispose() => Stop();
}
