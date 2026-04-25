using System.Text.Json.Serialization;

namespace MoergoLayerViz.Core.Keymap;

/// <summary>
/// Wire-level shape of the Moergo layout-editor JSON export. Only the fields
/// the viewer actually consumes are modelled — everything else is ignored.
/// </summary>
internal sealed class MoergoLayoutDocument
{
    [JsonPropertyName("keyboard")]
    public string Keyboard { get; set; } = "";

    [JsonPropertyName("layer_names")]
    public List<string> LayerNames { get; set; } = new();

    /// <summary>Array of layers, each containing a flat list of key bindings.</summary>
    [JsonPropertyName("layers")]
    public List<List<MoergoBinding>> Layers { get; set; } = new();

    /// <summary>User-defined macros (optional — factory layouts may omit this).</summary>
    [JsonPropertyName("macros")]
    public List<MoergoMacroDefinition>? Macros { get; set; }

    /// <summary>
    /// User-defined hold-taps (optional). Each entry has a <c>bindings</c>
    /// array of two strings: index 0 is the hold-side behavior, index 1 the
    /// tap-side. Only those two are needed to follow layer changes; tuning
    /// fields (tappingTermMs, flavor, …) are ignored.
    /// </summary>
    [JsonPropertyName("holdTaps")]
    public List<MoergoHoldTapDefinition>? HoldTaps { get; set; }
}

internal sealed class MoergoBinding
{
    /// <summary>Behavior reference including the leading '&', e.g. "&kp", "&mo", "&test".</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    /// <summary>Ordered parameters. Empty for zero-arg behaviors like <c>&amp;trans</c>.</summary>
    [JsonPropertyName("params")]
    public List<MoergoParam>? Params { get; set; }

    /// <summary>
    /// Optional user-authored decoration (label/icon/background) set in the
    /// Moergo layout editor. When a label is provided it overrides the
    /// derived display label.
    /// </summary>
    [JsonPropertyName("decoration")]
    public MoergoDecoration? Decoration { get; set; }
}

internal sealed class MoergoDecoration
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("background")]
    public string? Background { get; set; }
}

internal sealed class MoergoParam
{
    /// <summary>
    /// Parameter value — can be a string (keycode name) or a number (layer index).
    /// We normalize to string during loading.
    /// </summary>
    [JsonPropertyName("value")]
    public object? Value { get; set; }

    /// <summary>Nested params (rare — kept for round-trip safety; treated as additional leaves).</summary>
    [JsonPropertyName("params")]
    public List<MoergoParam>? Params { get; set; }
}

internal sealed class MoergoMacroDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("params")]
    public List<string>? Params { get; set; }

    [JsonPropertyName("bindings")]
    public List<MoergoBinding>? Bindings { get; set; }
}

internal sealed class MoergoHoldTapDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Two-element string array: <c>[holdBehavior, tapBehavior]</c>. Note this
    /// shape differs from <see cref="MoergoMacroDefinition.Bindings"/>, where
    /// each entry is a full <see cref="MoergoBinding"/> object.
    /// </summary>
    [JsonPropertyName("bindings")]
    public List<string>? Bindings { get; set; }
}
