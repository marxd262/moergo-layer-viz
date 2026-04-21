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

        DiagnosticLog.Info("JsonLoader",
            $"Loaded keyboard='{doc.Keyboard}' layers={layers.Count} macros={macros.Count}");

        return new KeyboardConfig(doc.Keyboard, layers, macros);
    }

    // ---- Conversion helpers ----

    private static KeyBinding ToBinding(MoergoBinding src)
    {
        var flatParams = new List<string>();
        if (src.Params is { Count: > 0 })
            FlattenParams(src.Params, flatParams);
        return new KeyBinding(src.Value, flatParams);
    }

    private static MoergoMacro ToMacro(MoergoMacroDefinition src)
    {
        var bindings = (src.Bindings ?? new()).Select(ToBinding).ToList();
        return new MoergoMacro(src.Name, src.Params ?? new(), bindings);
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
