using MoergoLayerViz.Core.Diagnostics;
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
        DiagnosticLog.Debug("Hook", $"press raw={e.Data.KeyCode} zmk={zmk ?? "(unmapped)"}");
        if (zmk is null) return;
        KeyEvent?.Invoke(new KeyEvent(zmk, KeyEventKind.Pressed));
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        var zmk = ToZmkKeycode(e.Data.KeyCode);
        DiagnosticLog.Debug("Hook", $"release raw={e.Data.KeyCode} zmk={zmk ?? "(unmapped)"}");
        if (zmk is null) return;
        KeyEvent?.Invoke(new KeyEvent(zmk, KeyEventKind.Released));
    }

    /// <summary>
    /// SharpHook enum names → ZMK canonical short-form keycodes used by the
    /// Moergo editor (and matched against in the lookup). Alpha keys / F-keys
    /// pass through the name-strip path; this map covers modifiers,
    /// punctuation, whitespace/edit, arrows, and nav keys — the set that
    /// diverges between SharpHook's verbose names ("LeftShift", "Equals",
    /// "Slash") and ZMK's short forms ("LSHFT", "EQUAL", "FSLH").
    /// </summary>
    private static readonly Dictionary<string, string> SharpHookToZmk = new(StringComparer.Ordinal)
    {
        // Modifiers
        ["LeftShift"] = "LSHFT",    ["RightShift"] = "RSHFT",
        ["LeftControl"] = "LCTRL",  ["RightControl"] = "RCTRL",
        ["LeftAlt"] = "LALT",       ["RightAlt"] = "RALT",
        ["LeftMeta"] = "LGUI",      ["RightMeta"] = "RGUI",

        // Punctuation
        ["Equals"] = "EQUAL",       ["Minus"] = "MINUS",
        ["Slash"] = "FSLH",         ["Backslash"] = "BSLH",
        ["Semicolon"] = "SEMI",     ["Quote"] = "SQT",
        ["BackQuote"] = "GRAVE",
        ["Comma"] = "COMMA",        ["Period"] = "DOT",
        ["OpenBracket"] = "LBKT",   ["CloseBracket"] = "RBKT",

        // Whitespace / edit
        ["Space"] = "SPACE",        ["Backspace"] = "BSPC",
        ["Enter"] = "RET",          ["Tab"] = "TAB",
        ["Escape"] = "ESC",         ["Delete"] = "DEL",
        ["CapsLock"] = "CAPS",

        // Arrows
        ["Up"] = "UP",              ["Down"] = "DOWN",
        ["Left"] = "LEFT",          ["Right"] = "RIGHT",

        // Nav block
        ["PageUp"] = "PG_UP",       ["PageDown"] = "PG_DN",
        ["Home"] = "HOME",          ["End"] = "END",
        ["Insert"] = "INS",

        // Numpad — SharpHook's VcNumPad* names map to ZMK's KP_* short forms.
        ["NumPad0"] = "KP_N0",      ["NumPad1"] = "KP_N1",
        ["NumPad2"] = "KP_N2",      ["NumPad3"] = "KP_N3",
        ["NumPad4"] = "KP_N4",      ["NumPad5"] = "KP_N5",
        ["NumPad6"] = "KP_N6",      ["NumPad7"] = "KP_N7",
        ["NumPad8"] = "KP_N8",      ["NumPad9"] = "KP_N9",
        ["NumPadAdd"] = "KP_PLUS",      ["NumPadSubtract"] = "KP_MINUS",
        ["NumPadMultiply"] = "KP_MULTIPLY",
        ["NumPadDivide"] = "KP_DIVIDE",
        ["NumPadDecimal"] = "KP_DOT",   ["NumPadSeparator"] = "KP_DOT",
        ["NumPadEnter"] = "KP_ENTER",
        ["NumPadEquals"] = "KP_EQUAL",
        ["NumLock"] = "KP_NUMLOCK",
    };

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

        // Fall back to the explicit SharpHook → ZMK map; anything not listed
        // (A-Z, F1-F24, etc.) uses its stripped name, which matches ZMK.
        return SharpHookToZmk.TryGetValue(stripped, out var zmk) ? zmk : stripped;
    }

    public void Dispose() => Stop();
}
