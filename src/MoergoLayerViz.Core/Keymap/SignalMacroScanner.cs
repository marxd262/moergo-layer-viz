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
/// <param name="LayerParamIndex">
/// Which macro parameter position carries the layer number, or null when the
/// macro hard-codes the layer as a literal (see <see cref="LiteralLayerIndex"/>).
/// </param>
/// <param name="KeyParamIndex">
/// Which macro parameter position carries the signal keycode, or null when the
/// macro hard-codes the keycode as a literal (see <see cref="LiteralKeycode"/>).
/// </param>
/// <param name="IsMomentary">
/// True when the macro uses <c>&amp;macro_pause_for_release</c> — the layer is
/// held only while the key is physically down.
/// </param>
/// <param name="LiteralLayerIndex">
/// Layer index baked into the macro body (no <c>&amp;macro_param_*</c> route).
/// Used when the macro takes no parameters and embeds <c>&amp;mo &lt;n&gt;</c>
/// directly.
/// </param>
/// <param name="LiteralKeycode">
/// Signal keycode baked into the macro body (no <c>&amp;macro_param_*</c>
/// route). Used when the macro embeds <c>&amp;kp &lt;CODE&gt;</c> directly.
/// </param>
public sealed record SignalMacro(
    string MacroName,
    int? LayerParamIndex,
    int? KeyParamIndex,
    bool IsMomentary,
    int? LiteralLayerIndex = null,
    string? LiteralKeycode = null)
{
    /// <summary>
    /// Resolves the layer this signal macro activates, given the layer-keymap
    /// binding that references it. Tries the routed param first, then falls
    /// back to <see cref="LiteralLayerIndex"/>. Returns false if neither
    /// resolves to a valid integer.
    /// </summary>
    public bool TryResolveTargetLayer(KeyBinding binding, out int layer)
    {
        if (LayerParamIndex is int idx
            && binding.Params.Count > idx
            && int.TryParse(binding.Params[idx], out layer))
            return true;
        if (LiteralLayerIndex is int lit)
        {
            layer = lit;
            return true;
        }
        layer = -1;
        return false;
    }

    /// <summary>
    /// Resolves the OS-visible signal keycode this macro emits, given the
    /// layer-keymap binding that references it. Tries the routed param first,
    /// then falls back to <see cref="LiteralKeycode"/>.
    /// </summary>
    public bool TryResolveSignalKeycode(KeyBinding binding, out string keycode)
    {
        if (KeyParamIndex is int idx
            && binding.Params.Count > idx
            && !string.IsNullOrWhiteSpace(binding.Params[idx]))
        {
            keycode = binding.Params[idx];
            return true;
        }
        if (!string.IsNullOrWhiteSpace(LiteralKeycode))
        {
            keycode = LiteralKeycode!;
            return true;
        }
        keycode = "";
        return false;
    }
}

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
    /// Identifies every macro that activates a layer and emits a host-visible
    /// keycode in the same phase. Two recognized shapes:
    /// <list type="bullet">
    /// <item><c>&amp;macro_press → &amp;mo / &amp;to / … → &amp;kp →
    ///   [&amp;macro_pause_for_release] → &amp;macro_release</c> — held-style.
    ///   <c>&amp;macro_pause_for_release</c> marks the macro as momentary
    ///   (the layer is active only while the key is physically down).</item>
    /// <item><c>&amp;macro_tap → &amp;to / &amp;tog / … → &amp;kp</c> —
    ///   fire-once. Useful for non-momentary <c>&amp;to</c>/<c>&amp;tog</c>
    ///   signal macros where the F-key fires as a clean tap and the tracker
    ///   only acts on press.</item>
    /// </list>
    /// Layer/keycode params can come from <c>&amp;macro_param_*</c> routes
    /// or be hard-coded literals; both are captured.
    /// </summary>
    public static IReadOnlyList<SignalMacro> DetectSignalMacros(KeyboardConfig config)
    {
        var found = new List<SignalMacro>();
        foreach (var macro in config.Macros)
        {
            if (TryClassifyMacro(macro, out var signal))
            {
                DiagnosticLog.Info("SignalMacroScanner",
                    $"Detected signal macro '{signal.MacroName}' " +
                    $"layerParam={signal.LayerParamIndex?.ToString() ?? "-"} " +
                    $"keyParam={signal.KeyParamIndex?.ToString() ?? "-"} " +
                    $"literalLayer={signal.LiteralLayerIndex?.ToString() ?? "-"} " +
                    $"literalKey={signal.LiteralKeycode ?? "-"} " +
                    $"momentary={signal.IsMomentary}");
                found.Add(signal);
            }
        }
        return found;
    }

    /// <summary>
    /// Walks every layer binding and returns the ones that activate layers
    /// via bare ZMK behaviors (no signal kp), which means the viewer can't
    /// follow them. Hold-taps wrapping a signal macro on their hold side are
    /// considered trackable and excluded; hold-taps wrapping a bare layer
    /// behavior are flagged just like a direct bare reference. UI surfaces
    /// this list as a user-facing warning.
    /// </summary>
    public static IReadOnlyList<UntrackableLayerSwitch> FindUntrackableLayerSwitches(
        KeyboardConfig config,
        IReadOnlyCollection<SignalMacro> signalMacros)
    {
        var signalNames = new HashSet<string>(signalMacros.Select(s => s.MacroName), StringComparer.Ordinal);
        var holdTapsByName = config.HoldTaps.ToDictionary(h => h.Name, StringComparer.Ordinal);
        var hits = new List<UntrackableLayerSwitch>();

        foreach (var layer in config.Layers)
        {
            for (int i = 0; i < layer.Bindings.Count; i++)
            {
                var b = layer.Bindings[i];
                var (untrackableBehavior, sourceParams) = ClassifyForUntrackability(
                    b, signalNames, holdTapsByName);
                if (untrackableBehavior is null) continue;

                int? target = null;
                if (sourceParams.Count > 0 && int.TryParse(sourceParams[0], out var t))
                    target = t;

                hits.Add(new UntrackableLayerSwitch(layer.Index, i, untrackableBehavior, target));
            }
        }
        return hits;
    }

    /// <summary>
    /// If the binding (or, for hold-taps, its hold side) activates a layer
    /// via a bare untrackable behavior, returns the offending behavior name
    /// and the params to read its target layer from. Returns (null, …) when
    /// the binding is trackable or unrelated to layer switching.
    /// </summary>
    private static (string? Behavior, IReadOnlyList<string> Params) ClassifyForUntrackability(
        KeyBinding b,
        HashSet<string> signalNames,
        Dictionary<string, HoldTap> holdTapsByName)
    {
        if (BareLayerBehaviors.Contains(b.Behavior))
        {
            // (shouldn't happen — signal macros don't start with bare behaviors,
            // but kept for parity with the original guard.)
            if (signalNames.Contains(b.Behavior)) return (null, Array.Empty<string>());
            return (b.Behavior, b.Params);
        }

        if (holdTapsByName.TryGetValue(b.Behavior, out var ht))
        {
            // Hold-tap wrapping a signal macro is trackable.
            if (signalNames.Contains(ht.HoldBinding)) return (null, Array.Empty<string>());
            // Hold-tap wrapping a bare layer behavior is just as untrackable
            // as a direct reference would be. The hold-tap binding's first
            // param is the hold-side param (in Moergo's distributed-param
            // shape), so it's still the layer index.
            if (BareLayerBehaviors.Contains(ht.HoldBinding))
                return (ht.HoldBinding, b.Params);
        }

        return (null, Array.Empty<string>());
    }

    private static bool TryClassifyMacro(MoergoMacro macro, out SignalMacro signal)
    {
        signal = null!;
        var b = macro.Bindings;
        if (b.Count == 0) return false;

        // Must start with a phase marker — &macro_press for hold-style macros
        // (with or without &macro_pause_for_release), or &macro_tap for
        // fire-once macros that emit a clean press+release of the signal kp.
        int startIdx = FirstIndexOfAny(b, 0, "&macro_press", "&macro_tap");
        if (startIdx < 0) return false;

        // Scan only this phase — bindings until the next phase marker or end.
        int endIdx = FirstIndexOfAny(b, startIdx + 1,
            "&macro_pause_for_release", "&macro_release", "&macro_press", "&macro_tap");
        if (endIdx < 0) endIdx = b.Count;

        int? layerParam = null;
        int? keyParam = null;
        int? literalLayer = null;
        string? literalKeycode = null;
        string? pendingRoute = null;

        for (int i = startIdx + 1; i < endIdx; i++)
        {
            var binding = b[i];

            // &macro_param_XtoY routes macro parameter X to the next behavior's param slot.
            // For our purposes we only care about X (which user-param feeds the next slot).
            if (binding.Behavior.StartsWith("&macro_param_", StringComparison.Ordinal))
            {
                pendingRoute = binding.Behavior; // keep the "from" half for parsing
                continue;
            }

            // A &mo (or &to, &tog, &lt, etc.): if a route is pending, it captures
            // a parameter slot; otherwise the macro is hard-coding the layer index
            // as a literal in its first param.
            if (BareLayerBehaviors.Contains(binding.Behavior))
            {
                if (pendingRoute is not null)
                {
                    var paramIdx = ExtractFromParam(pendingRoute);
                    if (paramIdx is int li) layerParam ??= li;
                }
                else if (binding.Params.Count > 0
                    && int.TryParse(binding.Params[0], out var litLayer))
                {
                    literalLayer ??= litLayer;
                }
                pendingRoute = null;
                continue;
            }

            // A &kp: routed → captures the signal-key param slot; literal → captures
            // the embedded keycode directly.
            if (binding.Behavior == "&kp")
            {
                if (pendingRoute is not null)
                {
                    var paramIdx = ExtractFromParam(pendingRoute);
                    if (paramIdx is int ki) keyParam ??= ki;
                }
                else if (binding.Params.Count > 0
                    && !string.IsNullOrWhiteSpace(binding.Params[0]))
                {
                    literalKeycode ??= binding.Params[0];
                }
                pendingRoute = null;
                continue;
            }

            // Anything else — drop the route marker.
            pendingRoute = null;
        }

        bool hasLayer = layerParam is not null || literalLayer is not null;
        bool hasKey = keyParam is not null || literalKeycode is not null;
        if (!hasLayer || !hasKey) return false;

        bool momentary = IndexOf(b, "&macro_pause_for_release") >= 0;
        signal = new SignalMacro(
            macro.Name,
            layerParam,
            keyParam,
            momentary,
            literalLayer,
            literalKeycode);
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
