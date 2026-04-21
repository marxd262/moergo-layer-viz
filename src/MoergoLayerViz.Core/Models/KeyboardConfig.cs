namespace MoergoLayerViz.Core.Models;

/// <summary>
/// A fully-loaded keymap: layers, layer names, and the user-defined macros
/// that may implement signal-emitting layer switches.
/// </summary>
public sealed record KeyboardConfig(
    string KeyboardId,
    IReadOnlyList<Layer> Layers,
    IReadOnlyList<MoergoMacro> Macros)
{
    public int LayerCount => Layers.Count;
}
