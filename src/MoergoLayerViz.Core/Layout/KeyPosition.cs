namespace MoergoLayerViz.Core.Layout;

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
public sealed record KeyPosition(
    int Index,
    double X,
    double Y,
    double Width = KeyPosition.StandardKeySize,
    double Height = KeyPosition.StandardKeySize,
    double RotationDegrees = 0)
{
    public const double StandardKeySize = 60;
}
