namespace MoergoLayerViz.Core.Layout;

/// <summary>
/// Physical layout for the Moergo GO60: 60 keys, 6 columns per hand, three-key
/// thumb clusters. Canvas coordinates were established interactively against a
/// Moergo marketing screenshot; see the project README for the source image.
/// <para>
/// Binding indices 0-59 follow Moergo's row-major JSON export order, which
/// interleaves both hands per row:
/// </para>
/// <list type="bullet">
///   <item><description>Rows 1-4 (indices 0-47): 12 keys each — left hand outer→inner, then right hand inner→outer.</description></item>
///   <item><description>Row 5 (indices 48-53): 6 keys — only the ring/middle/index columns have a 5th key; left hand outer→inner then right hand inner→outer.</description></item>
///   <item><description>Left thumb cluster (indices 54-56): outer→inner (rotations 10°, 20°, 35°).</description></item>
///   <item><description>Right thumb cluster (indices 57-59): inner→outer (rotations -35°, -20°, -10°).</description></item>
/// </list>
/// Confirmed against a real GO60 export: binding 56 = rightmost left-thumb key
/// (TAB on the shipped base layer), binding 57 = leftmost right-thumb key (RCTRL).
/// </summary>
public sealed class Go60Profile : IKeyboardProfile
{
    public string Id => "GO60";
    public string DisplayName => "Moergo GO60";
    public int KeyCount => 60;
    public double CanvasWidth => 1400;
    public double CanvasHeight => 600;

    public IReadOnlyList<KeyPosition> Keys { get; } = BuildKeys();

    private static IReadOnlyList<KeyPosition> BuildKeys()
    {
        // Six columns per hand, outer-to-inner. Each column has an x
        // coordinate, the y of its topmost key, and a flag for whether
        // the 5th (bottom) row exists. The shorter outer fingers start
        // lower; the inner-index column has no row-5 key (thumb cluster
        // sits there instead).
        (double X, double Top, bool HasRow5)[] leftCols =
        {
            (40,  110, false), // outer pinky
            (104, 106, false), // pinky
            (168,  76, true ), // ring
            (232,  76, true ), // middle
            (296,  76, true ), // index
            (360,  76, false), // inner index
        };

        // Right-hand columns, outer-to-inner to mirror `leftCols`. When
        // iterating a row left-to-right across the full keyboard we walk
        // `leftCols` forward then `rightCols` in reverse, so the innermost
        // right column (x=860) comes first on the right side.
        (double X, double Top, bool HasRow5)[] rightCols =
        {
            (1180, 110, false), // outer pinky
            (1116, 106, false), // pinky
            (1052,  76, true ), // ring
            ( 988,  76, true ), // middle
            ( 924,  76, true ), // index
            ( 860,  76, false), // inner index
        };

        const double RowPitch = 64;

        var list = new List<KeyPosition>(60);
        int idx = 0;

        // Rows 1-4 (indices 0-47): 12 keys each, six per hand.
        for (int row = 0; row < 4; row++)
        {
            foreach (var c in leftCols)                       // left: outer → inner
                list.Add(new KeyPosition(idx++, c.X, c.Top + row * RowPitch));
            for (int i = rightCols.Length - 1; i >= 0; i--)   // right: inner → outer
                list.Add(new KeyPosition(idx++, rightCols[i].X, rightCols[i].Top + row * RowPitch));
        }

        // Row 5 (indices 48-53): only the three middle columns per hand have
        // a 5th key, sitting just inboard of the thumb cluster.
        foreach (var c in leftCols)
            if (c.HasRow5) list.Add(new KeyPosition(idx++, c.X, c.Top + 4 * RowPitch));
        for (int i = rightCols.Length - 1; i >= 0; i--)
            if (rightCols[i].HasRow5) list.Add(new KeyPosition(idx++, rightCols[i].X, rightCols[i].Top + 4 * RowPitch));

        // Left thumb cluster (54-56): outer → inner. Rotations fan the keys
        // toward the thumb's natural arc; coords match the xaml.io sketch.
        list.Add(new KeyPosition(idx++, 370, 340, RotationDegrees: 10));
        list.Add(new KeyPosition(idx++, 440, 360, RotationDegrees: 20));
        list.Add(new KeyPosition(idx++, 504, 400, RotationDegrees: 35));

        // Right thumb cluster (57-59): inner → outer, mirroring the left.
        list.Add(new KeyPosition(idx++, 716, 400, RotationDegrees: -35));
        list.Add(new KeyPosition(idx++, 780, 360, RotationDegrees: -20));
        list.Add(new KeyPosition(idx++, 850, 340, RotationDegrees: -10));

        return list;
    }
}
