using MoergoLayerViz.Core.Input;
using SharpHook;
using SharpHook.Data;

namespace MoergoLayerViz.App.Services;

/// <summary>
/// Adapts the shared <see cref="SharpHookProvider"/> hook to the Core
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
    private readonly SharpHookProvider _provider;
    private bool _started;

    public event Action<KeyEvent>? KeyEvent;

    /// <summary>
    /// Raised on a threadpool thread when the underlying hook faults — most
    /// commonly on macOS when Accessibility permission is denied. Forwarded
    /// from <see cref="SharpHookProvider"/>. Subscribers must marshal to
    /// the UI thread themselves.
    /// </summary>
    public event Action<Exception>? HookFailed;

    public SharpHookKeyEventSource(SharpHookProvider provider)
    {
        _provider = provider;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _provider.KeyPressed += OnKeyPressed;
        _provider.KeyReleased += OnKeyReleased;
        _provider.HookFailed += OnHookFailed;
        _provider.Acquire();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _provider.KeyPressed -= OnKeyPressed;
        _provider.KeyReleased -= OnKeyReleased;
        _provider.HookFailed -= OnHookFailed;
        _provider.Release();
    }

    private void OnHookFailed(Exception ex) => HookFailed?.Invoke(ex);

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
