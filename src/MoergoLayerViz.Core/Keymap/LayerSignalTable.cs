using MoergoLayerViz.Core.Models;

namespace MoergoLayerViz.Core.Keymap;

/// <summary>
/// Maps an OS-visible keycode string (e.g. "A", "F20") to the ZMK layer that
/// it signals. Built from the intersection of:
/// <list type="bullet">
/// <item>detected signal macros (which macro invocations route which param to &amp;mo / &amp;kp), and</item>
/// <item>actual layer bindings where that macro is used.</item>
/// </list>
/// Consumed at runtime by <c>HotkeyLayerTracker</c>: when the OS fires a
/// keypress for a signal key, the tracker looks up which layer to activate.
/// </summary>
public sealed class LayerSignalTable
{
    private readonly Dictionary<string, SignalKeyMapping> _byKeycode;

    public LayerSignalTable(IReadOnlyDictionary<string, SignalKeyMapping> mappings)
    {
        _byKeycode = new Dictionary<string, SignalKeyMapping>(mappings, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>All (keycode → mapping) entries, for diagnostics/listing.</summary>
    public IReadOnlyDictionary<string, SignalKeyMapping> Mappings => _byKeycode;

    /// <summary>
    /// Resolves a signal keycode to the layer it activates, or null if the
    /// keycode isn't a known signal.
    /// </summary>
    public SignalKeyMapping? TryResolve(string keycode) =>
        _byKeycode.TryGetValue(keycode, out var m) ? m : null;

    /// <summary>
    /// Builds the table by walking every layer binding and pairing each macro
    /// invocation with the detected signal-macro pattern for that macro.
    /// Bindings using unknown macros are ignored.
    /// </summary>
    public static LayerSignalTable Build(
        KeyboardConfig config,
        IReadOnlyCollection<SignalMacro> signalMacros)
    {
        var byMacroName = signalMacros.ToDictionary(s => s.MacroName, StringComparer.Ordinal);
        var map = new Dictionary<string, SignalKeyMapping>(StringComparer.OrdinalIgnoreCase);

        foreach (var layer in config.Layers)
        {
            foreach (var b in layer.Bindings)
            {
                if (!byMacroName.TryGetValue(b.Behavior, out var sig)) continue;
                if (b.Params.Count <= Math.Max(sig.LayerParamIndex, sig.KeyParamIndex)) continue;

                if (!int.TryParse(b.Params[sig.LayerParamIndex], out var targetLayer))
                    continue;
                var keycode = b.Params[sig.KeyParamIndex];
                if (string.IsNullOrWhiteSpace(keycode)) continue;

                // Last writer wins — if the user binds the same macro to
                // multiple keys with different layers, we pick the last one
                // in JSON order. Good enough until we see a real conflict.
                map[keycode] = new SignalKeyMapping(keycode, targetLayer, sig.IsMomentary, sig.MacroName);
            }
        }

        return new LayerSignalTable(map);
    }
}

/// <summary>
/// An individual (keycode → layer) mapping derived from a signal-macro invocation.
/// </summary>
/// <param name="Keycode">The ZMK keycode name (e.g. "A", "F20") emitted by the signal kp.</param>
/// <param name="TargetLayer">Layer index that activates when this key is pressed.</param>
/// <param name="IsMomentary">True for hold-to-activate; false for toggle-like behavior.</param>
/// <param name="SourceMacroName">Which macro this mapping was derived from (for diagnostics).</param>
public sealed record SignalKeyMapping(
    string Keycode,
    int TargetLayer,
    bool IsMomentary,
    string SourceMacroName);
