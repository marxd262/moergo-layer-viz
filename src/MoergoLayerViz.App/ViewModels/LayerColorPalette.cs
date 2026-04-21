namespace MoergoLayerViz.App.ViewModels;

/// <summary>
/// Default layer tab colors — indexed, wraps after 10. Matches the Svalboard
/// palette for visual consistency with the sister app.
/// </summary>
public static class LayerColorPalette
{
    private static readonly string[] Colors =
    [
        "#89B4FA", // layer 0 — blue
        "#F38BA8", // layer 1 — pink
        "#A6E3A1", // layer 2 — green
        "#FAB387", // layer 3 — orange
        "#CBA6F7", // layer 4 — purple
        "#F9E2AF", // layer 5 — yellow
        "#94E2D5", // layer 6 — teal
        "#F5C2E7", // layer 7 — magenta
        "#B4BEFE", // layer 8 — lavender
        "#EBA0AC", // layer 9 — maroon
    ];

    public static string GetColor(int index) => Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];
}
