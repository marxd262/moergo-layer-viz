namespace MoergoLayerViz.Core.Layout;

/// <summary>
/// Physical layout for the Moergo Glove80: 80 keys, 6 columns per hand, six-key
/// thumb clusters. Canvas coordinates established against the Moergo-published
/// Glove80 base-layer reference image.
/// <para>
/// Binding indices 0-79 follow Moergo's Glove80 JSON export order. Row 5
/// (ZXCV) is split around the left thumb; the right thumb is appended after
/// row 6:
/// </para>
/// <list type="bullet">
///   <item><description>Row 1 / F-row (indices 0-9): 10 keys. Left outer→inner (no F-row on inner index); right inner→outer (no F-row on inner index). Binding 0 = F1, 9 = F10.</description></item>
///   <item><description>Rows 2-4 (indices 10-45): 12 keys each. Left outer→inner, then right inner→outer.</description></item>
///   <item><description>Row 5 LH (indices 46-51): L1..L6 outer→inner (GRAVE, ZXCVB).</description></item>
///   <item><description>Thumb top-row (indices 52-57): LH thumb row-A 3 keys inner→outer (52-54), then RH thumb row-A 3 keys outer→inner (55-57).</description></item>
///   <item><description>Row 5 RH (indices 58-63): R1..R6 inner→outer (NM,./PgUp).</description></item>
///   <item><description>Row 6 LH (indices 64-68): L1..L5 outer→inner (Magic, Home, End, ←, →). No row 6 on L6.</description></item>
///   <item><description>Thumb bottom-row (indices 69-74): LH thumb row-B 3 keys inner→outer (69-71), then RH thumb row-B 3 keys outer→inner (72-74).</description></item>
///   <item><description>Row 6 RH (indices 75-79): R2..R6 inner→outer (↑, ↓, {, }, PgDn). No row 6 on R1.</description></item>
/// </list>
/// Each thumb is a 2×3 grid laid out along the thumb's arc. The two rows are
/// interleaved into the JSON at different points (not kept contiguous). The
/// six keys of each thumb therefore use <b>non-contiguous</b> index ranges —
/// LH thumb = {52-54, 69-71}, RH thumb = {55-57, 72-74}.
/// <para>
/// Ground truth (verified against the shipped Glove80.json fixture):
/// </para>
/// <list type="bullet">
///   <item><description>Binding 64 = `&amp;magic` at L1 row 6 (bottom-left of main matrix).</description></item>
///   <item><description>Binding 79 = `&amp;kp PG_DN` at R6 row 6 (bottom-right of main matrix).</description></item>
///   <item><description>LH thumb = {&amp;lt 1,RET (69), LSHFT (70), LALT (71), DEL (52), LCTRL (53), &amp;lower (54)}.</description></item>
///   <item><description>RH thumb = {SPACE (74), &amp;lt 5,TAB (73), &amp;test 1,A (72), BSPC (57), RCTRL (56), &amp;caps_word (55)}.</description></item>
/// </list>
/// <para>
/// The outer pinky (L1/R6) and pinky (L2/R5) columns drop a half-row below the
/// ring/middle/index columns. The inner-index columns (L6/R1) are short —
/// rows 2-5 only, no F-row (row 1) or bottom row (row 6).
/// </para>
/// <para>
/// Horizontal column pitch is uniform (64 units); column splay is encoded as
/// per-column Y offsets only. Real Glove80 columns also fan outward; that
/// refinement is deferred.
/// </para>
/// </summary>
public sealed class Glove80Profile : IKeyboardProfile
{
    public string Id => "Glove80";
    public string DisplayName => "Moergo Glove80";
    public int KeyCount => 80;

    // Leftmost main-matrix key at x=30; rightmost at x=1170 (spans to 1230);
    // half-key horizontal margin on both sides → 1260. Centerline at x=630.
    // Vertical extent: topmost key at y=30, deepest thumb key at y=482 + 60 = 542.
    public double CanvasWidth => 1260;
    public double CanvasHeight => 570;

    public IReadOnlyList<KeyPosition> Keys { get; } = BuildKeys();

    private static IReadOnlyList<KeyPosition> BuildKeys()
    {
        // Six columns per hand, outer-to-inner. HasRow0 / HasRow5 gate the
        // short inner-index columns (L6/R1) which only have rows 1..4.
        // L1/L2 and R6/R5 drop half a row to represent splay.
        // TODO(splay): real Glove80 columns fan outward; v1 uses uniform X pitch.
        (double X, double Top, bool HasRow0, bool HasRow5)[] leftCols =
        {
            (  30,  62, true,  true ), // L1 outer pinky (half row drop)
            (  94,  62, true,  true ), // L2 pinky       (half row drop)
            ( 158,  30, true,  true ), // L3 ring
            ( 222,  30, true,  true ), // L4 middle
            ( 286,  30, true,  true ), // L5 index
            ( 350,  94, false, false), // L6 inner index (short — rows 1..4 only)
        };

        (double X, double Top, bool HasRow0, bool HasRow5)[] rightCols =
        {
            (1170,  62, true,  true ), // R6 outer pinky (half row drop)
            (1106,  62, true,  true ), // R5 pinky       (half row drop)
            (1042,  30, true,  true ), // R4 ring
            ( 978,  30, true,  true ), // R3 middle
            ( 914,  30, true,  true ), // R2 index
            ( 850,  94, false, false), // R1 inner index (short)
        };

        const double RowPitch = 64;

        // For short columns (HasRow0=false) Top is stored at the row-1 Y of the
        // tall columns (30 + 64 = 94) so that `Top + (row - 1) * RowPitch`
        // places row 1 at y=94 and row 4 at y=286, matching tall cols.
        static double YFor((double X, double Top, bool HasRow0, bool HasRow5) c, int row)
            => c.Top + (row - (c.HasRow0 ? 0 : 1)) * RowPitch;

        var list = new List<KeyPosition>(80);
        int idx = 0;

        // Row 1 / F-row (indices 0-9). Inner-index columns (L6/R1) skipped.
        foreach (var c in leftCols)
            if (c.HasRow0) list.Add(new KeyPosition(idx++, c.X, YFor(c, 0), Hand: Hand.Left));
        for (int i = rightCols.Length - 1; i >= 0; i--)
            if (rightCols[i].HasRow0)
                list.Add(new KeyPosition(idx++, rightCols[i].X, YFor(rightCols[i], 0), Hand: Hand.Right));

        // Rows 2-4 (indices 10-45): 12 keys each, all columns.
        for (int row = 1; row <= 3; row++)
        {
            foreach (var c in leftCols)
                list.Add(new KeyPosition(idx++, c.X, YFor(c, row), Hand: Hand.Left));
            for (int i = rightCols.Length - 1; i >= 0; i--)
                list.Add(new KeyPosition(idx++, rightCols[i].X, YFor(rightCols[i], row), Hand: Hand.Right));
        }

        // Row 5 left (indices 46-51): GRAVE, ZXCVB. L1..L6 outer→inner.
        foreach (var c in leftCols)
            list.Add(new KeyPosition(idx++, c.X, YFor(c, 4), Hand: Hand.Left));

        // Thumb top-row (indices 52-57): LH row-A 3 keys, then RH row-A 3 keys.
        // Row A is the row closer to the main matrix (less rotation).
        // Within each hand's row, inner→outer (from the hand's index-finger
        // side toward the palm/thumb-tip side).
        list.Add(new KeyPosition(idx++, 430, 340, RotationDegrees:  12, Hand: Hand.Left));  // 52: LH A inner
        list.Add(new KeyPosition(idx++, 503, 358, RotationDegrees:  25, Hand: Hand.Left));  // 53: LH A middle
        list.Add(new KeyPosition(idx++, 565, 390, RotationDegrees:  35, Hand: Hand.Left));  // 54: LH A outer

        list.Add(new KeyPosition(idx++, 643, 424, RotationDegrees: -35, Hand: Hand.Right)); // 55: RH A outer
        list.Add(new KeyPosition(idx++, 700, 382, RotationDegrees: -25, Hand: Hand.Right)); // 56: RH A middle
        list.Add(new KeyPosition(idx++, 770, 352, RotationDegrees: -12, Hand: Hand.Right)); // 57: RH A inner

        // Row 5 right (indices 58-63): NM,./PgUp. R1..R6 inner→outer.
        for (int i = rightCols.Length - 1; i >= 0; i--)
            list.Add(new KeyPosition(idx++, rightCols[i].X, YFor(rightCols[i], 4), Hand: Hand.Right));

        // Row 6 left (indices 64-68): Magic, Home, End, ←, →.
        // L1..L5 outer→inner; L6 has no row 6.
        foreach (var c in leftCols)
            if (c.HasRow5) list.Add(new KeyPosition(idx++, c.X, YFor(c, 5), Hand: Hand.Left));

        // Thumb bottom-row (indices 69-74): LH row-B 3 keys, then RH row-B 3.
        // Row B is the row further from the main matrix (higher rotation).
        list.Add(new KeyPosition(idx++, 375, 398, RotationDegrees:  10, Hand: Hand.Left));  // 69: LH B inner
        list.Add(new KeyPosition(idx++, 450, 415, RotationDegrees:  23, Hand: Hand.Left));  // 70: LH B middle
        list.Add(new KeyPosition(idx++, 520, 448, RotationDegrees:  36, Hand: Hand.Left));  // 71: LH B outer

        list.Add(new KeyPosition(idx++, 685, 482, RotationDegrees: -36, Hand: Hand.Right)); // 72: RH B outer
        list.Add(new KeyPosition(idx++, 748, 439, RotationDegrees: -25, Hand: Hand.Right)); // 73: RH B middle
        list.Add(new KeyPosition(idx++, 820, 408, RotationDegrees: -12, Hand: Hand.Right)); // 74: RH B inner

        // Row 6 right (indices 75-79): ↑, ↓, {, }, PgDn.
        // R2..R6 inner→outer; R1 has no row 6.
        for (int i = rightCols.Length - 1; i >= 0; i--)
            if (rightCols[i].HasRow5)
                list.Add(new KeyPosition(idx++, rightCols[i].X, YFor(rightCols[i], 5), Hand: Hand.Right));

        System.Diagnostics.Debug.Assert(idx == 80, $"Expected 80 keys, built {idx}");
        return list;
    }
}
