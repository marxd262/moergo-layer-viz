using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoergoLayerViz.Core.Diagnostics;

namespace MoergoLayerViz.Core.Tooling;

/// <summary>
/// Injects the canonical layer-switch signal macros and matching hold-taps
/// into a Moergo layout-editor JSON export, preserving every Moergo-only
/// field we don't recognize (tuning fields, ordering, unknown top-level
/// keys, …) by mutating the parsed JSON tree in place rather than
/// round-tripping through our reduced <c>KeyboardConfig</c> model.
///
/// Generates four kinds of entries (skipping any whose name already exists,
/// so the operation is idempotent):
/// <list type="bullet">
/// <item><c>&amp;Tolayer</c> — parametric switch-to macro: <c>&amp;Tolayer &lt;layer&gt; &lt;Fkey&gt;</c>.</item>
/// <item><c>&amp;Molayer</c> — parametric momentary macro.</item>
/// <item>One <c>&amp;mo_&lt;layer&gt;_f&lt;NN&gt;signal</c> per layer (fixed: layer + Fkey baked in). The base layer
///   is included so jump-to-base bindings (e.g. <c>&amp;Tolayer 0 F13</c>) can be tracked back to base.</item>
/// <item>One <c>&amp;ht_&lt;layer&gt;</c> hold-tap per layer (hold = the fixed macro, tap = <c>&amp;kp</c>).</item>
/// </list>
/// Keybindings are never modified — the user wires the new behaviors in the
/// Moergo editor themselves.
/// </summary>
public static class SignalMacroGenerator
{
    /// <summary>Lowest Fkey we'll assign (ZMK's <c>F13</c>).</summary>
    public const int MinFkey = 13;

    /// <summary>Highest Fkey we'll assign (ZMK's <c>F24</c>).</summary>
    public const int MaxFkey = 24;

    public sealed record GenerateOptions(int StartFkey = MinFkey);

    public sealed record GeneratedItem(string Name, string Kind, int? LayerIndex, int? Fkey);

    public sealed record GenerateResult(
        string OutputJson,
        IReadOnlyList<GeneratedItem> Added,
        IReadOnlyList<string> SkippedExisting,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Mutates <paramref name="inputJson"/> and returns a pretty-printed
    /// JSON string with the generated additions. The input is parsed but
    /// otherwise untouched — every original field is preserved.
    /// </summary>
    public static GenerateResult Generate(string inputJson, GenerateOptions options)
    {
        if (options.StartFkey < MinFkey || options.StartFkey > MaxFkey)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"StartFkey must be between F{MinFkey} and F{MaxFkey}.");

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(inputJson, new JsonNodeOptions { PropertyNameCaseInsensitive = false });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Failed to parse input JSON: {ex.Message}", ex);
        }
        if (root is not JsonObject doc)
            throw new InvalidDataException("Input JSON root must be an object.");

        var layerNames = ReadLayerNames(doc);
        var macrosArray = EnsureArray(doc, "macros");
        var holdTapsArray = EnsureArray(doc, "holdTaps");

        var existingMacroNames = CollectNames(macrosArray);
        var existingHoldTapNames = CollectNames(holdTapsArray);

        var added = new List<GeneratedItem>();
        var skipped = new List<string>();
        var warnings = new List<string>();

        // 1. Parametric macros — added once, regardless of layer count.
        TryAddMacro(macrosArray, existingMacroNames, BuildToLayerMacro(),
            "&Tolayer", "parametric-macro", null, null, added, skipped);
        TryAddMacro(macrosArray, existingMacroNames, BuildMoLayerMacro(),
            "&Molayer", "parametric-macro", null, null, added, skipped);

        // 2. Per-layer fixed mo-macro + matching hold-tap. The base layer is included
        // so jump-to-base bindings can be tracked back to base.
        for (int layerIdx = 0; layerIdx < layerNames.Count; layerIdx++)
        {
            var rawName = layerNames[layerIdx];
            var slug = SanitizeLayerName(rawName, layerIdx);

            int fkey = options.StartFkey + layerIdx;

            if (fkey > MaxFkey)
            {
                warnings.Add($"Layer '{rawName}' (index {layerIdx}) would map to F{fkey}, beyond F{MaxFkey} — skipped.");
                continue;
            }

            var fkeyName = "F" + fkey.ToString(CultureInfo.InvariantCulture);
            var macroName = $"&mo_{slug}_f{fkey.ToString(CultureInfo.InvariantCulture)}signal";
            var holdTapName = $"&ht_{slug}";

            TryAddMacro(macrosArray, existingMacroNames,
                BuildFixedMoMacro(macroName, layerIdx, fkeyName),
                macroName, "fixed-mo-macro", layerIdx, fkey, added, skipped);

            TryAddHoldTap(holdTapsArray, existingHoldTapNames,
                BuildHoldTap(holdTapName, macroName),
                holdTapName, "hold-tap", layerIdx, fkey, added, skipped);
        }

        var output = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        DiagnosticLog.Info("SignalMacroGenerator",
            $"added={added.Count} skipped={skipped.Count} warnings={warnings.Count}");

        return new GenerateResult(output, added, skipped, warnings);
    }

    // ---- JSON shape builders ---------------------------------------------

    /// <summary>
    /// <c>&amp;Tolayer &lt;layer&gt; &lt;code&gt;</c> — parametric switch-to.
    /// Mirrors the user's hand-built shape exactly.
    /// </summary>
    private static JsonObject BuildToLayerMacro() => new()
    {
        ["name"] = "&Tolayer",
        ["description"] = "Switch to layer with extra key for layer tracking/switching",
        ["bindings"] = new JsonArray
        {
            BindingObj("&macro_press"),
            BindingObj("&macro_param_1to1"),
            BindingWithParam("&to", "MACRO_PLACEHOLDER"),
            BindingObj("&macro_param_2to1"),
            BindingWithParam("&kp", "MACRO_PLACEHOLDER"),
        },
        ["params"] = new JsonArray { "layer", "code" },
    };

    /// <summary>
    /// <c>&amp;Molayer &lt;layer&gt; &lt;code&gt;</c> — parametric momentary.
    /// Repeats the param-routing on the release segment so the <c>&amp;mo</c>
    /// and <c>&amp;kp</c> properly release when the key is let go.
    /// </summary>
    private static JsonObject BuildMoLayerMacro() => new()
    {
        ["name"] = "&Molayer",
        ["description"] = "Momentary layer with extra key for layer tracking/switching",
        ["bindings"] = new JsonArray
        {
            BindingObj("&macro_press"),
            BindingObj("&macro_param_1to1"),
            BindingWithParam("&mo", "MACRO_PLACEHOLDER"),
            BindingObj("&macro_param_2to1"),
            BindingWithParam("&kp", "MACRO_PLACEHOLDER"),
            BindingObj("&macro_pause_for_release"),
            BindingObj("&macro_release"),
            BindingObj("&macro_param_1to1"),
            BindingWithParam("&mo", "MACRO_PLACEHOLDER"),
            BindingObj("&macro_param_2to1"),
            BindingWithParam("&kp", "MACRO_PLACEHOLDER"),
        },
        ["params"] = new JsonArray { "layer", "code" },
    };

    /// <summary>
    /// Fixed-per-layer momentary macro: same shape as the user's hand-built
    /// <c>&amp;mo_symbol_f16signal</c> — press segment then post-release tail.
    /// </summary>
    private static JsonObject BuildFixedMoMacro(string name, int layerIndex, string fkey) => new()
    {
        ["name"] = name,
        ["description"] = "",
        ["bindings"] = new JsonArray
        {
            BindingObj("&macro_press"),
            BindingWithParam("&mo", layerIndex),
            BindingWithParam("&kp", fkey),
            BindingObj("&macro_pause_for_release"),
            BindingObj("&macro_release"),
            BindingWithParam("&mo", layerIndex),
            BindingWithParam("&kp", fkey),
        },
        ["params"] = new JsonArray(),
    };

    /// <summary>
    /// Hold-tap whose hold side is the fixed mo-macro and whose tap side is
    /// <c>&amp;kp</c> taking the user-supplied keycode at bind time. Tuning
    /// fields default to the user's existing <c>&amp;ht_symbol</c> values —
    /// the user can adjust them in Moergo.
    /// </summary>
    private static JsonObject BuildHoldTap(string name, string holdMacroName) => new()
    {
        ["name"] = name,
        ["description"] = "",
        ["bindings"] = new JsonArray { holdMacroName, "&kp" },
        ["tappingTermMs"] = 280,
        ["flavor"] = "balanced",
        ["quickTapMs"] = 175,
        ["requirePriorIdleMs"] = 150,
        ["holdTriggerOnRelease"] = true,
    };

    private static JsonObject BindingObj(string value) => new() { ["value"] = value };

    private static JsonObject BindingWithParam(string behavior, string param) => new()
    {
        ["value"] = behavior,
        ["params"] = new JsonArray { new JsonObject { ["value"] = param } },
    };

    private static JsonObject BindingWithParam(string behavior, int param) => new()
    {
        ["value"] = behavior,
        ["params"] = new JsonArray { new JsonObject { ["value"] = param } },
    };

    // ---- Helpers ----------------------------------------------------------

    private static List<string> ReadLayerNames(JsonObject doc)
    {
        var names = new List<string>();
        if (doc["layer_names"] is JsonArray arr)
        {
            foreach (var node in arr)
                names.Add(node?.GetValue<string>() ?? "");
        }
        return names;
    }

    private static JsonArray EnsureArray(JsonObject doc, string key)
    {
        if (doc[key] is JsonArray existing) return existing;
        var fresh = new JsonArray();
        doc[key] = fresh;
        return fresh;
    }

    private static HashSet<string> CollectNames(JsonArray arr)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in arr)
        {
            if (node is JsonObject obj && obj["name"]?.GetValue<string>() is { } n)
                set.Add(n);
        }
        return set;
    }

    private static void TryAddMacro(JsonArray macros, HashSet<string> existing, JsonObject macro,
        string name, string kind, int? layerIndex, int? fkey,
        List<GeneratedItem> added, List<string> skipped)
    {
        if (!existing.Add(name))
        {
            skipped.Add(name);
            return;
        }
        macros.Add(macro);
        added.Add(new GeneratedItem(name, kind, layerIndex, fkey));
    }

    private static void TryAddHoldTap(JsonArray holdTaps, HashSet<string> existing, JsonObject ht,
        string name, string kind, int? layerIndex, int? fkey,
        List<GeneratedItem> added, List<string> skipped)
    {
        if (!existing.Add(name))
        {
            skipped.Add(name);
            return;
        }
        holdTaps.Add(ht);
        added.Add(new GeneratedItem(name, kind, layerIndex, fkey));
    }

    /// <summary>
    /// Lowercase, replace any non-alphanumeric run with a single underscore,
    /// trim leading/trailing underscores. Empty input falls back to
    /// <c>layer&lt;index&gt;</c> so we always produce a valid identifier.
    /// </summary>
    internal static string SanitizeLayerName(string raw, int index)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "layer" + index.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder(raw.Length);
        bool prevUnderscore = false;
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                prevUnderscore = false;
            }
            else if (!prevUnderscore && sb.Length > 0)
            {
                sb.Append('_');
                prevUnderscore = true;
            }
        }
        while (sb.Length > 0 && sb[^1] == '_') sb.Length--;
        return sb.Length == 0 ? "layer" + index.ToString(CultureInfo.InvariantCulture) : sb.ToString();
    }
}
