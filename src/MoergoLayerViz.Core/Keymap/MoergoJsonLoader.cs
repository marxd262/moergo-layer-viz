using System.Text.Json;
using MoergoLayerViz.Core.Diagnostics;
using MoergoLayerViz.Core.Models;

namespace MoergoLayerViz.Core.Keymap;

/// <summary>
/// Parses a Moergo layout-editor JSON export into a <see cref="KeyboardConfig"/>.
/// The same schema is used by both GO60 and Glove80 exports; the difference is
/// only in the key count (which callers validate against the chosen
/// <see cref="Layout.IKeyboardProfile"/>).
/// </summary>
public static class MoergoJsonLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads the JSON file at <paramref name="path"/>. Throws <see cref="FileNotFoundException"/>
    /// if the file doesn't exist and <see cref="InvalidDataException"/> if parsing fails.
    /// </summary>
    public static KeyboardConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Layout JSON not found: {path}", path);

        var json = File.ReadAllText(path);
        return LoadFromJson(json);
    }

    /// <summary>Loads a config from an in-memory JSON string.</summary>
    public static KeyboardConfig LoadFromJson(string json)
    {
        MoergoLayoutDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<MoergoLayoutDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Failed to parse Moergo JSON: {ex.Message}", ex);
        }

        if (doc is null)
            throw new InvalidDataException("Moergo JSON deserialized to null");

        var layers = new List<Layer>(doc.Layers.Count);
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            var name = i < doc.LayerNames.Count ? doc.LayerNames[i] : $"Layer {i}";
            var bindings = doc.Layers[i].Select(ToBinding).ToList();
            layers.Add(new Layer(i, name, bindings));
        }

        var macros = (doc.Macros ?? new()).Select(ToMacro).ToList();
        var macroArity = macros.ToDictionary(m => m.Name, m => m.Params.Count, StringComparer.Ordinal);
        var holdTaps = (doc.HoldTaps ?? new())
            .Select(ht => ToHoldTap(ht, macroArity))
            .Where(ht => ht is not null)
            .Select(ht => ht!)
            .ToList();

        DiagnosticLog.Info("JsonLoader",
            $"Loaded keyboard='{doc.Keyboard}' layers={layers.Count} macros={macros.Count} holdTaps={holdTaps.Count}");

        return new KeyboardConfig(doc.Keyboard, layers, macros, holdTaps);
    }

    // ---- Conversion helpers ----

    private static KeyBinding ToBinding(MoergoBinding src)
    {
        var flatParams = new List<string>();
        if (src.Params is { Count: > 0 })
            FlattenParams(src.Params, flatParams);
        return new KeyBinding(src.Value, flatParams)
        {
            DecorationLabel = string.IsNullOrWhiteSpace(src.Decoration?.Label) ? null : src.Decoration!.Label,
            DecorationBackground = string.IsNullOrWhiteSpace(src.Decoration?.Background) ? null : src.Decoration!.Background,
            DecorationIcon = string.IsNullOrWhiteSpace(src.Decoration?.Icon) ? null : src.Decoration!.Icon,
        };
    }

    private static MoergoMacro ToMacro(MoergoMacroDefinition src)
    {
        var bindings = (src.Bindings ?? new()).Select(ToBinding).ToList();
        return new MoergoMacro(src.Name, src.Params ?? new(), bindings);
    }

    /// <summary>
    /// Converts a wire-level hold-tap into the public model. Skips entries
    /// that don't have at least two bindings — those are malformed for our
    /// purposes (we need both hold and tap sides). Each side's arity (how
    /// many keymap params it consumes) is computed by looking up the
    /// referenced behavior against the built-in arity table or the macro
    /// dictionary; unknown behaviors default to 0.
    /// </summary>
    private static HoldTap? ToHoldTap(MoergoHoldTapDefinition src, IReadOnlyDictionary<string, int> macroArity)
    {
        var bindings = src.Bindings;
        if (bindings is null || bindings.Count < 2) return null;
        if (string.IsNullOrWhiteSpace(src.Name)) return null;
        return new HoldTap(
            src.Name,
            bindings[0],
            bindings[1],
            ResolveBehaviorArity(bindings[0], macroArity),
            ResolveBehaviorArity(bindings[1], macroArity));
    }

    /// <summary>
    /// Returns how many params the named behavior consumes when invoked. Hard-codes
    /// the standard ZMK behaviors that turn up on hold-tap sides; falls back to
    /// the user macro dictionary; defaults to 0 for anything unknown (which is
    /// the safest choice — it routes all keymap params to the other side rather
    /// than dropping them).
    /// </summary>
    private static int ResolveBehaviorArity(string behavior, IReadOnlyDictionary<string, int> macroArity)
    {
        switch (behavior)
        {
            case "&kp":
            case "&mo":
            case "&to":
            case "&tog":
            case "&sl":
                return 1;
            case "&lt":
                return 2;
            case "&trans":
            case "&none":
            case "&bootloader":
            case "&sys_reset":
                return 0;
        }
        return macroArity.TryGetValue(behavior, out var n) ? n : 0;
    }

    /// <summary>
    /// Flattens nested params into a single ordered list of strings. The
    /// Moergo editor emits nested params only for complex behaviours (e.g.
    /// mod-taps holding another keycode); a depth-first walk preserves the
    /// original textual order that a ZMK keymap would print.
    /// </summary>
    private static void FlattenParams(IEnumerable<MoergoParam> src, List<string> sink)
    {
        foreach (var p in src)
        {
            if (p.Value is not null)
                sink.Add(NormalizeValue(p.Value));
            if (p.Params is { Count: > 0 })
                FlattenParams(p.Params, sink);
        }
    }

    /// <summary>
    /// Params can arrive as strings ("LSHFT"), numbers (1), or JsonElement
    /// (when the deserializer boxes them). Normalize everything to string
    /// so the rest of the app only deals with one type.
    /// </summary>
    private static string NormalizeValue(object value) =>
        value switch
        {
            string s => s,
            JsonElement e => e.ValueKind switch
            {
                JsonValueKind.String => e.GetString() ?? "",
                JsonValueKind.Number => e.GetRawText(),
                _ => e.ToString(),
            },
            _ => value.ToString() ?? "",
        };
}
