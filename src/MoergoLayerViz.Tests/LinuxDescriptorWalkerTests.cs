using MoergoLayerViz.Core.Input;
using Xunit;

namespace MoergoLayerViz.Tests;

/// <summary>
/// Coverage for the minimal HID report-descriptor walker used by
/// <c>LinuxRawHidLayerSource</c> to decide whether a /dev/hidraw node
/// exposes the FF60/61 usage pair we care about. The walker only needs to
/// answer "does this descriptor declare (page, usage) anywhere?" — not
/// fully parse the descriptor — so the tests target that question directly.
/// </summary>
public class LinuxDescriptorWalkerTests
{
    // HID short-item encoding: prefix byte = (tag << 4) | (type << 2) | size.
    // Usage Page (Generic) = 0x05, Usage = 0x09. We use 16-bit forms (0x06,
    // 0x0A) for non-standard pages like 0xFF60.

    [Fact]
    public void Matches_TopLevel_FF60_61()
    {
        // Usage Page (FF60h, 16-bit), Usage (61h, 8-bit), Collection (App)
        var desc = new byte[]
        {
            0x06, 0x60, 0xFF,   // Usage Page (FF60)
            0x09, 0x61,         // Usage (0x61)
            0xA1, 0x01,         // Collection (Application)
            0xC0,               // End Collection
        };
        Assert.True(LinuxRawHidLayerSource.DescriptorMatchesUsage(desc, 0xFF60, 0x61));
    }

    [Fact]
    public void Rejects_KeyboardOnly_Descriptor()
    {
        // Standard keyboard descriptor preamble — Generic Desktop / Keyboard
        var desc = new byte[]
        {
            0x05, 0x01,         // Usage Page (Generic Desktop)
            0x09, 0x06,         // Usage (Keyboard)
            0xA1, 0x01,         // Collection (Application)
            0xC0,
        };
        Assert.False(LinuxRawHidLayerSource.DescriptorMatchesUsage(desc, 0xFF60, 0x61));
    }

    [Fact]
    public void Rejects_WhenUsagePageMatches_ButUsageDoesNot()
    {
        var desc = new byte[]
        {
            0x06, 0x60, 0xFF,   // Usage Page (FF60)
            0x09, 0x42,         // Usage (0x42) — wrong
            0xA1, 0x01,
            0xC0,
        };
        Assert.False(LinuxRawHidLayerSource.DescriptorMatchesUsage(desc, 0xFF60, 0x61));
    }

    [Fact]
    public void Matches_When_Target_Appears_AfterAnotherCollection()
    {
        // First a keyboard collection, then our raw-HID collection. ZMK
        // composite firmware looks like this — the boot keyboard interface
        // shares the same descriptor as the custom one.
        var desc = new byte[]
        {
            // Keyboard
            0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0xC0,
            // Raw HID
            0x06, 0x60, 0xFF, 0x09, 0x61, 0xA1, 0x01, 0xC0,
        };
        Assert.True(LinuxRawHidLayerSource.DescriptorMatchesUsage(desc, 0xFF60, 0x61));
    }

    [Fact]
    public void Empty_Descriptor_DoesNotMatch()
    {
        Assert.False(LinuxRawHidLayerSource.DescriptorMatchesUsage(System.Array.Empty<byte>(), 0xFF60, 0x61));
    }

    [Fact]
    public void Truncated_Descriptor_DoesNotCrash()
    {
        // Prefix declares 2 data bytes but only 1 is present.
        var desc = new byte[] { 0x06, 0x60 };
        Assert.False(LinuxRawHidLayerSource.DescriptorMatchesUsage(desc, 0xFF60, 0x61));
    }
}
