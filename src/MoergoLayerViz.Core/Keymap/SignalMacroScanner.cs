using MoergoLayerViz.Core.Diagnostics;
using MoergoLayerViz.Core.Models;

namespace MoergoLayerViz.Core.Keymap;

/// <summary>
/// A macro that both activates a layer and emits an OS-visible keypress —
/// the only reliable way for this viewer to track layer state on a ZMK
/// keyboard, since raw <c>&amp;mo</c>/<c>&amp;lt</c> switches are handled
/// in firmware and never reach the host.
/// </summary>
/// <param name="MacroName">Behavior name including '&amp;' (e.g. "&amp;test").</param>
/// <param name="LayerParamIndex">Which macro parameter position carries the layer number.</param>
/// <param name="KeyParamIndex">Which macro parameter position carries the signal keycode.</param>
/// <param name="IsMomentary">
/// True when the macro uses <c>&amp;macro_pause_for_release</c> — the layer is
/// held only while the key is physically down.
/// </param>
public sealed record SignalMacro(
    string MacroName,
    int LayerParamIndex,
    int KeyParamIndex,
    bool IsMomentary);

/// <summary>
/// A layer-switch binding found in the loaded keymap that the viewer cannot
/// observe because it uses a bare ZMK behavior (&amp;mo, &amp;lt, &amp;magic,
/// ...) instead of a signal macro. Shown to the user as a warning listing
/// which layers they need to upgrade.
/// </summary>
/// <param name="LayerIndex">Which layer holds the untrackable binding.</param>
/// <param name="KeyIndex">Which key slot on that layer.</param>
/// <param name="Behavior">The bare behavior name, e.g. "&amp;mo".</param>
/// <param name="TargetLayer">
/// Target layer index if resolvable from the first parameter, else null
/// (some behaviors like <c>&amp;magic</c> take no direct layer parameter).
/// </param>
public sealed record UntrackableLayerSwitch(
    int LayerIndex,
    int KeyIndex,
    string Behavior,
    int? TargetLayer);

/// <summary>
/// Scans a <see cref="KeyboardConfig"/> to auto-detect:
/// <list type="bullet">
/// <item>which macros follow the "layer + signal kp" pattern, and</item>
/// <item>which remaining layer-switch bindings <em>don't</em> follow that
/// pattern — so the user knows which layers will silently fail to track.</item>
/// </list>
/// </summary>
public static class SignalMacroScanner
{
    // Bare ZMK layer-switch behaviors that the viewer can't observe.
    // Anything not in this list (and not a signal macro) is treated as a
    // regular keycode for visualization purposes.
    private static readonly HashSet<string> BareLayerBehaviors = new(StringComparer.Ordinal)
    {
        "&mo",       // momentary — hold to activate
        "&to",       // set default layer (persistent)
        "&tog",      // toggle
        "&lt",       // layer-tap: tap sends kp, hold activates layer
        "&sl",       // sticky layer
        "&magic",    // Glove80-specific — param indirected through a hold-tap
        "&lower",    // common user-defined wrapper (bare — no signal)
    };

    /// <summary>
    /// Identifies every macro in <paramref name="config"/> that is structured
    /// like <c>&amp;macro_press → &amp;mo (routed param) → &amp;kp (routed param)
    /// → &amp;macro_pause_for_release → &amp;macro_release</c>.
    /// </summary>
    public static IReadOnlyList<SignalMacro> DetectSignalMacros(KeyboardConfig config)
    {
        var found = new List<SignalMacro>();
        foreach (var macro in config.Macros)
        {
            if (TryClassifyMacro(macro, out var signal))
            {
                DiagnosticLog.Info("SignalMacroScanner",
                    $"Detected signal macro '{signal.MacroName}' layerParam={signal.LayerParamIndex} keyParam={signal.KeyParamIndex} momentary={signal.IsMomentary}");
                found.Add(signal);
            }
        }
        return found;
    }

    /// <summary>
    /// Walks every layer binding and returns the ones that activate layers
    /// via bare ZMK behaviors (no signal kp), which means the viewer can't
    /// follow them. UI surfaces this list as a user-facing warning.
    /// </summary>
    public static IReadOnlyList<UntrackableLayerSwitch> FindUntrackableLayerSwitches(
        KeyboardConfig config,
        IReadOnlyCollection<SignalMacro> signalMacros)
    {
        var signalNames = new HashSet<string>(signalMacros.Select(s => s.MacroName), StringComparer.Ordinal);
        var hits = new List<UntrackableLayerSwitch>();

        foreach (var layer in config.Layers)
        {
            for (int i = 0; i < layer.Bindings.Count; i++)
            {
                var b = layer.Bindings[i];
                if (!BareLayerBehaviors.Contains(b.Behavior)) continue;
                if (signalNames.Contains(b.Behavior)) continue; // (shouldn't happen — signal macros don't start with & bare behaviors)

                int? target = null;
                if (b.Params.Count > 0 && int.TryParse(b.Params[0], out var t))
                    target = t;

                hits.Add(new UntrackableLayerSwitch(layer.Index, i, b.Behavior, target));
            }
        }
        return hits;
    }

    private static bool TryClassifyMacro(MoergoMacro macro, out SignalMacro signal)
    {
        signal = null!;
        var b = macro.Bindings;
        if (b.Count == 0) return false;

        // Must contain &macro_press, an &mo (or similar layer-switch), and an &kp.
        int pressIdx = IndexOf(b, "&macro_press");
        if (pressIdx < 0) return false;

        // We only parse the "press phase" — bindings between &macro_press
        // and (&macro_pause_for_release | &macro_release | end-of-list).
        int endIdx = FirstIndexOfAny(b, pressIdx + 1, "&macro_pause_for_release", "&macro_release");
        if (endIdx < 0) endIdx = b.Count;

        int? layerParam = null;
        int? keyParam = null;
        string? pendingRoute = null;

        for (int i = pressIdx + 1; i < endIdx; i++)
        {
            var binding = b[i];

            // &macro_param_XtoY routes macro parameter X to the next behavior's param slot.
            // For our purposes we only care about X (which user-param feeds the next slot).
            if (binding.Behavior.StartsWith("&macro_param_", StringComparison.Ordinal))
            {
                pendingRoute = binding.Behavior; // keep the "from" half for parsing
                continue;
            }

            // A &mo (or &to, &tog, &lt, etc.) under a pending route => captures the layer param.
            if (BareLayerBehaviors.Contains(binding.Behavior) && pendingRoute is not null)
            {
                var paramIdx = ExtractFromParam(pendingRoute);
                if (paramIdx is int li) layerParam ??= li;
                pendingRoute = null;
                continue;
            }

            // A &kp under a pending route => captures the signal-key param.
            if (binding.Behavior == "&kp" && pendingRoute is not null)
            {
                var paramIdx = ExtractFromParam(pendingRoute);
                if (paramIdx is int ki) keyParam ??= ki;
                pendingRoute = null;
                continue;
            }

            // Anything else — drop the route marker.
            pendingRoute = null;
        }

        if (layerParam is null || keyParam is null) return false;

        bool momentary = IndexOf(b, "&macro_pause_for_release") >= 0;
        signal = new SignalMacro(macro.Name, layerParam.Value, keyParam.Value, momentary);
        return true;
    }

    /// <summary>
    /// Parses <c>&amp;macro_param_1to1</c>-style names. Returns the "from"
    /// index (1-based macro parameter number), converted to 0-based.
    /// </summary>
    private static int? ExtractFromParam(string name)
    {
        // Format: "&macro_param_<from>to<to>"
        const string prefix = "&macro_param_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = name[prefix.Length..];
        var toIdx = rest.IndexOf("to", StringComparison.Ordinal);
        if (toIdx <= 0) return null;
        var fromStr = rest[..toIdx];
        if (!int.TryParse(fromStr, out var from)) return null;
        return from - 1; // convert to 0-based
    }

    private static int IndexOf(IReadOnlyList<KeyBinding> bindings, string behavior)
    {
        for (int i = 0; i < bindings.Count; i++)
            if (bindings[i].Behavior == behavior) return i;
        return -1;
    }

    private static int FirstIndexOfAny(IReadOnlyList<KeyBinding> bindings, int startAt, params string[] targets)
    {
        for (int i = startAt; i < bindings.Count; i++)
            if (targets.Contains(bindings[i].Behavior)) return i;
        return -1;
    }
}
