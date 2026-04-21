namespace MoergoLayerViz.Core.Layout;

/// <summary>
/// Physical layout for the Moergo Glove80. Stub — 80 key positions to be
/// filled in against a Glove80 reference image. Until then the profile reports
/// the correct key count (so JSON parsing passes) but renders keys in a
/// placeholder 10x8 grid.
/// </summary>
public sealed class Glove80Profile : IKeyboardProfile
{
    public string Id => "Glove80";
    public string DisplayName => "Moergo Glove80";
    public int KeyCount => 80;
    public double CanvasWidth => 1400;
    public double CanvasHeight => 720;

    public IReadOnlyList<KeyPosition> Keys { get; } = BuildPlaceholderKeys();

    private static IReadOnlyList<KeyPosition> BuildPlaceholderKeys()
    {
        // Temporary 10x8 grid so the view has something to render until
        // the real Glove80 coordinates are added.
        var list = new List<KeyPosition>(80);
        const double size = KeyPosition.StandardKeySize;
        const double padding = 4;
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 10; col++)
            {
                var x = 60 + col * (size + padding);
                var y = 60 + row * (size + padding);
                list.Add(new KeyPosition(row * 10 + col, x, y));
            }
        }
        return list;
    }
}
