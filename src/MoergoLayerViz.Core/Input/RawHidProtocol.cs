namespace MoergoLayerViz.Core.Input;

/// <summary>
/// Pure parser for the 32-byte Raw HID reports emitted by the
/// <c>zmk-keypeek-layer-notifier</c> module. Kept I/O-free so it can be
/// exercised by unit tests with synthetic buffers.
/// </summary>
internal static class RawHidProtocol
{
    public const byte LayerStateMessage = 0xFF;
    public const byte KeyEventMessage = 0xF1;
    public const int ReportSize = 32;
    public const int UsagePage = 0xFF60;
    public const int UsageId = 0x61;

    /// <summary>
    /// Returns the highest active layer index from a layer-state report, or
    /// null if the buffer isn't a layer-state report. A bitmask of 0
    /// (no layers active, including the base) is reported as layer 0 to match
    /// how the rest of the app treats "back to base".
    /// </summary>
    public static int? TryParseLayerState(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return null;
        if (payload[0] != LayerStateMessage) return null;
        // Sub-type / version bytes (1, 2) are not validated — the firmware
        // is the only writer and the upstream protocol may grow new fields.
        ushort bitmask = (ushort)(payload[6] | (payload[7] << 8));
        if (bitmask == 0) return 0;
        for (int i = 15; i >= 0; i--)
            if ((bitmask & (1 << i)) != 0) return i;
        return 0;
    }

    /// <summary>
    /// Returns the (matrix position, pressed-flag) tuple for a key-event
    /// report, or null if the buffer isn't one.
    /// </summary>
    public static (int Position, bool Pressed)? TryParseKeyEvent(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        if (payload[0] != KeyEventMessage) return null;
        return (payload[2], payload[3] == 0x01);
    }
}
