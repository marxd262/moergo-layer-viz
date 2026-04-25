using System.Linq;
using MoergoLayerViz.Core.Keymap;
using MoergoLayerViz.Core.Models;
using Xunit;

namespace MoergoLayerViz.Tests;

/// <summary>
/// Covers the literal-signal-macro + hold-tap-wrapper pattern introduced in
/// the Go60_CustomLayerSwitching fixture: macros that hard-code the layer
/// index and signal keycode (no <c>&amp;macro_param_*</c> routing), wrapped
/// by hold-taps so the same physical key keeps its tap behavior.
/// </summary>
public class CustomLayerSwitchingTests
{
    private static KeyboardConfig LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Go60_CustomLayerSwitching.json");
        Assert.True(File.Exists(path), $"Test fixture missing: {path}");
        return MoergoJsonLoader.LoadFromFile(path);
    }

    [Fact]
    public void DetectsLiteralSignalMacros_WithEmbeddedLayerAndKeycode()
    {
        var config = LoadFixture();
        var signals = SignalMacroScanner.DetectSignalMacros(config);

        var symbol = Assert.Single(signals, s => s.MacroName == "&mo_symbol_f16signal");
        Assert.Null(symbol.LayerParamIndex);
        Assert.Null(symbol.KeyParamIndex);
        Assert.Equal(1, symbol.LiteralLayerIndex);
        Assert.Equal("F16", symbol.LiteralKeycode);
        Assert.True(symbol.IsMomentary);

        var cursor = Assert.Single(signals, s => s.MacroName == "&mo_cursor_f17signal");
        Assert.Equal(2, cursor.LiteralLayerIndex);
        Assert.Equal("F17", cursor.LiteralKeycode);

        var keypad = Assert.Single(signals, s => s.MacroName == "&mo_keypad_f18signal");
        Assert.Equal(3, keypad.LiteralLayerIndex);
        Assert.Equal("F18", keypad.LiteralKeycode);
    }

    [Fact]
    public void DetectsRoutedSignalMacros_StillWork()
    {
        // The fixture also contains the legacy &Mom macro (param-routed). It
        // must classify with both routed indices set and no literals — same
        // as before this feature was added.
        var config = LoadFixture();
        var signals = SignalMacroScanner.DetectSignalMacros(config);

        var mom = signals.FirstOrDefault(s => s.MacroName == "&Mom");
        Assert.NotNull(mom);
        Assert.Equal(0, mom!.LayerParamIndex);
        Assert.Equal(1, mom.KeyParamIndex);
        Assert.Null(mom.LiteralLayerIndex);
        Assert.Null(mom.LiteralKeycode);
        Assert.True(mom.IsMomentary);
    }

    [Fact]
    public void LayerSignalTable_ResolvesViaHoldTap_ToLiteralLayerAndKeycode()
    {
        // The keymap references &ht_symbol/&ht_cursor — never the signal
        // macros directly. The table must follow the indirection and map the
        // signal F-keys to their layers. (&ht_keypad is defined but not
        // placed on any key in this fixture, so F18 isn't reachable yet.)
        var config = LoadFixture();
        var signals = SignalMacroScanner.DetectSignalMacros(config);
        var table = LayerSignalTable.Build(config, signals);

        var f16 = table.TryResolve("F16");
        Assert.NotNull(f16);
        Assert.Equal(1, f16!.TargetLayer);
        Assert.True(f16.IsMomentary);
        Assert.Equal("&mo_symbol_f16signal", f16.SourceMacroName);

        var f17 = table.TryResolve("F17");
        Assert.NotNull(f17);
        Assert.Equal(2, f17!.TargetLayer);
        Assert.Equal("&mo_cursor_f17signal", f17.SourceMacroName);
    }

    [Fact]
    public void DetectsMacroTapBased_ToSignalMacro_AsNonMomentary()
    {
        // &macro_tap shape: fire-once. Used for &to / &tog signal macros
        // where the F-key fires as a clean tap and the tracker only acts on
        // press (set-and-stay semantics). No &macro_pause_for_release ⇒
        // IsMomentary should be false.
        const string json = """
        {
          "keyboard": "go60",
          "layer_names": ["Base", "Symbol"],
          "layers": [
            [ { "value": "&trans" } ],
            [ { "value": "&trans" } ]
          ],
          "macros": [
            {
              "name": "&to_base_f19signal",
              "description": "",
              "bindings": [
                { "value": "&macro_tap" },
                { "value": "&to", "params": [ { "value": 0 } ] },
                { "value": "&kp", "params": [ { "value": "F19" } ] }
              ],
              "params": []
            }
          ]
        }
        """;

        var config = MoergoJsonLoader.LoadFromJson(json);
        var signals = SignalMacroScanner.DetectSignalMacros(config);

        var sig = Assert.Single(signals);
        Assert.Equal("&to_base_f19signal", sig.MacroName);
        Assert.Equal(0, sig.LiteralLayerIndex);
        Assert.Equal("F19", sig.LiteralKeycode);
        Assert.False(sig.IsMomentary);
    }

    [Fact]
    public void FindUntrackableLayerSwitches_ExcludesSignalBackedHoldTaps()
    {
        // None of the &ht_* hold-taps in the fixture should appear as
        // untrackable: each wraps a signal macro on its hold side.
        var config = LoadFixture();
        var signals = SignalMacroScanner.DetectSignalMacros(config);
        var untrackable = SignalMacroScanner.FindUntrackableLayerSwitches(config, signals);

        Assert.DoesNotContain(untrackable, u =>
            u.Behavior is "&ht_symbol" or "&ht_cursor" or "&ht_keypad");
    }
}
