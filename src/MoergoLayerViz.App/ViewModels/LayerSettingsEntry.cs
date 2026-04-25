namespace MoergoLayerViz.App.ViewModels;

/// <summary>
/// One row in the Settings window's combined per-layer table. Aggregates the
/// existing <see cref="LayerColorEntry"/> + <see cref="LayerSignalEntry"/> so a
/// single <c>ItemsControl</c> can render the layer name, color picker + reset,
/// and the signal-key picker on one line.
/// </summary>
public sealed class LayerSettingsEntry
{
    public int Index { get; }
    public string DisplayName { get; }
    public LayerColorEntry Color { get; }
    public LayerSignalEntry Signal { get; }

    public LayerSettingsEntry(LayerColorEntry color, LayerSignalEntry signal)
    {
        Index = color.Index;
        DisplayName = color.DisplayName;
        Color = color;
        Signal = signal;
    }
}
