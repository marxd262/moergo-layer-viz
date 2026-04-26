namespace MoergoLayerViz.Core.Layout;

/// <summary>Which half of the split keyboard a key belongs to. Drives the stacked-layout split rendering.</summary>
public enum Hand
{
    Left,
    Right,
}

/// <summary>
/// Absolute position of a single physical key on the board canvas.
/// Coordinates are the upper-left corner of the rectangle, in canvas units.
/// </summary>
/// <param name="Index">
/// Matches the JSON keymap binding index (layers[layer][index]).
/// Do not reorder without reshuffling the JSON ordering too.
/// </param>
/// <param name="X">Upper-left X in canvas units.</param>
/// <param name="Y">Upper-left Y in canvas units.</param>
/// <param name="Width">Rectangle width. Defaults to <see cref="StandardKeySize"/>.</param>
/// <param name="Height">Rectangle height. Defaults to <see cref="StandardKeySize"/>.</param>
/// <param name="RotationDegrees">Rotation around the key's centre.</param>
/// <param name="Description">
/// Optional human-readable physical position label (e.g. "Row 2, L col 4",
/// "Left thumb 1") used for tooltips. Profiles populate this; null is fine
/// and callers fall back to the index.
/// </param>
/// <param name="Hand">
/// Which half of the split keyboard this key belongs to. Profiles assign
/// this at build time; the stacked-layout renderer partitions keys by hand
/// so each half can be translated independently.
/// </param>
public sealed record KeyPosition(
    int Index,
    double X,
    double Y,
    double Width = KeyPosition.StandardKeySize,
    double Height = KeyPosition.StandardKeySize,
    double RotationDegrees = 0,
    string? Description = null,
    Hand Hand = Hand.Left)
{
    public const double StandardKeySize = 60;
}
