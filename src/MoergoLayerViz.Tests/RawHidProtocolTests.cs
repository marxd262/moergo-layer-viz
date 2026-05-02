using MoergoLayerViz.Core.Input;
using Xunit;

namespace MoergoLayerViz.Tests;

/// <summary>
/// Pure parser coverage for the Raw HID firmware protocol. The I/O loop
/// in <c>RawHidLayerSource</c> is exercised live; here we just verify the
/// 32-byte report decoding against the documented byte layout.
/// </summary>
public class RawHidProtocolTests
{
    private static byte[] LayerStateReport(ushort bitmask)
    {
        var buf = new byte[32];
        buf[0] = 0xFF;
        buf[1] = 0x04;
        buf[2] = 0x01;
        buf[6] = (byte)(bitmask & 0xFF);
        buf[7] = (byte)((bitmask >> 8) & 0xFF);
        return buf;
    }

    private static byte[] KeyEventReport(byte position, bool pressed)
    {
        var buf = new byte[32];
        buf[0] = 0xF1;
        buf[2] = position;
        buf[3] = (byte)(pressed ? 1 : 0);
        return buf;
    }

    [Theory]
    [InlineData(0x0001, 0)]    // base layer only
    [InlineData(0x0003, 1)]    // base + L1; L1 is highest
    [InlineData(0x0009, 3)]    // base + L3; L3 is highest
    [InlineData(0x8000, 15)]   // L15 alone
    public void TryParseLayerState_ReturnsHighestSetBit(ushort bitmask, int expected)
    {
        var report = LayerStateReport(bitmask);
        var layer = RawHidProtocol.TryParseLayerState(report);
        Assert.Equal(expected, layer);
    }

    [Fact]
    public void TryParseLayerState_BitmaskZero_ReturnsBase()
    {
        // Firmware can briefly emit 0x0000 between transitions; treat as base.
        var report = LayerStateReport(0);
        Assert.Equal(0, RawHidProtocol.TryParseLayerState(report));
    }

    [Fact]
    public void TryParseLayerState_WrongMessageType_ReturnsNull()
    {
        var report = KeyEventReport(0, true);
        Assert.Null(RawHidProtocol.TryParseLayerState(report));
    }

    [Fact]
    public void TryParseLayerState_ShortBuffer_ReturnsNull()
    {
        var report = new byte[] { 0xFF, 0x04, 0x01, 0x00 };
        Assert.Null(RawHidProtocol.TryParseLayerState(report));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(59, true)]
    [InlineData(255, false)]
    public void TryParseKeyEvent_RoundTrips(byte position, bool pressed)
    {
        var report = KeyEventReport(position, pressed);
        var ev = RawHidProtocol.TryParseKeyEvent(report);
        Assert.NotNull(ev);
        Assert.Equal(position, ev!.Value.Position);
        Assert.Equal(pressed, ev.Value.Pressed);
    }

    [Fact]
    public void TryParseKeyEvent_WrongMessageType_ReturnsNull()
    {
        var report = LayerStateReport(0x0001);
        Assert.Null(RawHidProtocol.TryParseKeyEvent(report));
    }

    [Fact]
    public void TryParseKeyEvent_ShortBuffer_ReturnsNull()
    {
        var report = new byte[] { 0xF1, 0x00 };
        Assert.Null(RawHidProtocol.TryParseKeyEvent(report));
    }
}
