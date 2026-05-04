using MoergoLayerViz.Core.Layout;
using Xunit;

namespace MoergoLayerViz.Tests;

/// <summary>
/// Verifies the per-profile HID device matcher used to scope Raw HID
/// discovery to whichever keyboard the user has selected. Both Moergo
/// boards register under the same VID/PID (0x16C0/0x27DB — the shared
/// pid.codes ZMK identifier), so the matcher has to combine the IDs with
/// a case-insensitive **substring** match on the product name.
///
/// <para>Substring (rather than prefix) is necessary because Linux's
/// hidraw layer prepends the manufacturer name — the device shows up as
/// <c>"MoErgo Go60 Left"</c> there, not <c>"Go60 Left"</c>. macOS and
/// Windows expose the bare product string. A prefix match would silently
/// stop finding the device on Linux.</para>
///
/// <para>The test data covers both transports observed against real
/// hardware: USB exposes "&lt;Board&gt; Left" because the cable side is the
/// host; BLE exposes just "&lt;Board&gt;" because only the central is visible
/// to the OS. A future firmware revision could add a "Wireless" or "RH"
/// suffix; the substring match keeps that working.</para>
/// </summary>
public class KeyboardProfileHidMatcherTests
{
    private const int ZmkVid = 0x16C0;
    private const int ZmkPid = 0x27DB;

    [Theory]
    [InlineData("Go60 Left")]         // USB / macOS, observed
    [InlineData("Go60")]              // BLE, observed
    [InlineData("MoErgo Go60 Left")]  // Linux hidraw, observed — manufacturer prefix
    [InlineData("go60 right")]        // hypothetical RH-as-host USB; case-insensitive
    [InlineData("Go60 Wireless")]     // future firmware suffix
    public void Go60_Matches_GoodProductNames(string product)
    {
        var p = new Go60Profile();
        Assert.True(p.MatchesHidDevice(ZmkVid, ZmkPid, product));
    }

    [Theory]
    [InlineData("Glove80 Left")]
    [InlineData("Glove80")]
    [InlineData("MoErgo Glove80 Left")]  // Linux hidraw shape
    [InlineData("glove80 RH")]
    public void Glove80_Matches_GoodProductNames(string product)
    {
        var p = new Glove80Profile();
        Assert.True(p.MatchesHidDevice(ZmkVid, ZmkPid, product));
    }

    [Fact]
    public void Go60_DoesNotMatch_Glove80Device()
    {
        var p = new Go60Profile();
        Assert.False(p.MatchesHidDevice(ZmkVid, ZmkPid, "Glove80 Left"));
        Assert.False(p.MatchesHidDevice(ZmkVid, ZmkPid, "Glove80"));
    }

    [Fact]
    public void Glove80_DoesNotMatch_Go60Device()
    {
        var p = new Glove80Profile();
        Assert.False(p.MatchesHidDevice(ZmkVid, ZmkPid, "Go60 Left"));
        Assert.False(p.MatchesHidDevice(ZmkVid, ZmkPid, "Go60"));
    }

    [Fact]
    public void DoesNotMatch_WhenVendorOrProductIdDiffers()
    {
        var p = new Go60Profile();
        Assert.False(p.MatchesHidDevice(0x046D, ZmkPid, "Go60"));   // wrong VID
        Assert.False(p.MatchesHidDevice(ZmkVid, 0x1234, "Go60"));   // wrong PID
    }

    [Fact]
    public void DoesNotMatch_WhenProductNameMissing()
    {
        // Some HID devices return null/empty for GetProductName(); without
        // a name we can't disambiguate the two boards, so refuse to match
        // rather than guess.
        var go60 = new Go60Profile();
        var glove = new Glove80Profile();
        Assert.False(go60.MatchesHidDevice(ZmkVid, ZmkPid, null));
        Assert.False(go60.MatchesHidDevice(ZmkVid, ZmkPid, ""));
        Assert.False(glove.MatchesHidDevice(ZmkVid, ZmkPid, null));
    }

}
