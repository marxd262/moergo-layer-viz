namespace MoergoLayerViz.Core.Models;

/// <summary>
/// A single key binding in a ZMK layer, flattened from the nested Moergo JSON format.
/// <para>
/// Examples:
/// <list type="bullet">
/// <item><c>&amp;kp A</c> → Behavior="&amp;kp", Params=["A"]</item>
/// <item><c>&amp;mo 1</c> → Behavior="&amp;mo", Params=["1"]</item>
/// <item><c>&amp;HRM_left_hand_v1_TKZ LSHFT A</c> → Behavior="&amp;HRM_left_hand_v1_TKZ", Params=["LSHFT", "A"]</item>
/// <item><c>&amp;trans</c> → Behavior="&amp;trans", Params=[]</item>
/// </list>
/// </para>
/// </summary>
public sealed record KeyBinding(string Behavior, IReadOnlyList<string> Params)
{
    public static readonly KeyBinding Transparent = new("&trans", Array.Empty<string>());
    public static readonly KeyBinding None = new("&none", Array.Empty<string>());

    /// <summary>Formatted for display: "&amp;kp A" or just "&amp;trans".</summary>
    public string Display =>
        Params.Count == 0 ? Behavior : $"{Behavior} {string.Join(' ', Params)}";
}
