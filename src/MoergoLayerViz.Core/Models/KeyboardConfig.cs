namespace MoergoLayerViz.Core.Models;

/// <summary>
/// A fully-loaded keymap: layers, layer names, the user-defined macros that
/// may implement signal-emitting layer switches, and the hold-taps that may
/// wrap them on their hold side.
/// </summary>
public sealed record KeyboardConfig(
    string KeyboardId,
    IReadOnlyList<Layer> Layers,
    IReadOnlyList<MoergoMacro> Macros,
    IReadOnlyList<HoldTap> HoldTaps)
{
    public int LayerCount => Layers.Count;
}
