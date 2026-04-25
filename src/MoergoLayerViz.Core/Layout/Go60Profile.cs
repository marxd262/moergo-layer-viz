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
///   <item><description>Left thumb cluster (indices 54-56): outer→inner (rotations 10°, 22°, 34°).</description></item>
///   <item><description>Right thumb cluster (indices 57-59): inner→outer (rotations -34°, -22°, -10°).</description></item>
/// </list>
/// Confirmed against a real GO60 export: binding 56 = rightmost left-thumb key
/// (TAB on the shipped base layer), binding 57 = leftmost right-thumb key (RCTRL).
/// </summary>
public sealed class Go60Profile : IKeyboardProfile
{
    public string Id => "GO60";
    public string DisplayName => "Moergo GO60";
    public int KeyCount => 60;
    // Canvas tightened so horizontal margin is half a key-width (30) on both
    // sides: leftmost key sits at x=30, rightmost at x=1110 (spans to 1170),
    // giving a total width of 1200. Vertical margin is one full key-height (60):
    // topmost key at y=60, bottommost at y=384 (spans to 444), canvas height 504.
    public double CanvasWidth => 1200;
    public double CanvasHeight => 504;

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
            (30,  94, false), // outer pinky
            (94,  90, false), // pinky
            (158, 60, true ), // ring
            (222, 60, true ), // middle
            (286, 60, true ), // index
            (350, 60, false), // inner index
        };

        // Right-hand columns, outer-to-inner to mirror `leftCols`. When
        // iterating a row left-to-right across the full keyboard we walk
        // `leftCols` forward then `rightCols` in reverse, so the innermost
        // right column (x=790) comes first on the right side.
        (double X, double Top, bool HasRow5)[] rightCols =
        {
            (1110, 94, false), // outer pinky
            (1046, 90, false), // pinky
            ( 982, 60, true ), // ring
            ( 918, 60, true ), // middle
            ( 854, 60, true ), // index
            ( 790, 60, false), // inner index
        };

        const double RowPitch = 64;

        // Per-column finger names used in tooltips. Aligned with leftCols /
        // rightCols (outer-to-inner). Col numbering across the profile:
        // 1 = outer pinky, 6 = inner index — same on both hands.
        string[] fingerByCol = { "outer pinky", "pinky", "ring", "middle", "index", "inner index" };

        var list = new List<KeyPosition>(60);
        int idx = 0;

        // Rows 1-4 (indices 0-47): 12 keys each, six per hand.
        for (int row = 0; row < 4; row++)
        {
            for (int c = 0; c < leftCols.Length; c++)         // left: outer → inner
            {
                var col = c + 1;
                list.Add(new KeyPosition(idx++, leftCols[c].X, leftCols[c].Top + row * RowPitch,
                    Description: $"Row {row + 1}, L col {col} ({fingerByCol[c]})"));
            }
            for (int i = rightCols.Length - 1; i >= 0; i--)   // right: inner → outer
            {
                var col = i + 1; // mirrored: i=5 (inner index) → col 6, i=0 (outer pinky) → col 1
                list.Add(new KeyPosition(idx++, rightCols[i].X, rightCols[i].Top + row * RowPitch,
                    Description: $"Row {row + 1}, R col {col} ({fingerByCol[i]})"));
            }
        }

        // Row 5 (indices 48-53): only the three middle columns per hand have
        // a 5th key, sitting just inboard of the thumb cluster.
        for (int c = 0; c < leftCols.Length; c++)
            if (leftCols[c].HasRow5)
                list.Add(new KeyPosition(idx++, leftCols[c].X, leftCols[c].Top + 4 * RowPitch,
                    Description: $"Row 5, L col {c + 1} ({fingerByCol[c]})"));
        for (int i = rightCols.Length - 1; i >= 0; i--)
            if (rightCols[i].HasRow5)
                list.Add(new KeyPosition(idx++, rightCols[i].X, rightCols[i].Top + 4 * RowPitch,
                    Description: $"Row 5, R col {i + 1} ({fingerByCol[i]})"));

        // Left thumb cluster (54-56): outer → inner. Rotations fan the keys
        // toward the thumb's natural arc; coords match the xaml.io sketch.
        list.Add(new KeyPosition(idx++, 360, 320, RotationDegrees: 10, Description: "Left thumb 1 (outer)"));
        list.Add(new KeyPosition(idx++, 437, 338, RotationDegrees: 22, Description: "Left thumb 2"));
        list.Add(new KeyPosition(idx++, 509, 370, RotationDegrees: 34, Description: "Left thumb 3 (inner)"));

        // Right thumb cluster (57-59): inner → outer, mirroring the left
        // around centerline x=600 (canvas width 1200).
        list.Add(new KeyPosition(idx++, 642, 402, RotationDegrees: -34, Description: "Right thumb 1 (inner)"));
        list.Add(new KeyPosition(idx++, 708, 360, RotationDegrees: -22, Description: "Right thumb 2"));
        list.Add(new KeyPosition(idx++, 780, 330, RotationDegrees: -10, Description: "Right thumb 3 (outer)"));

        return list;
    }
}
