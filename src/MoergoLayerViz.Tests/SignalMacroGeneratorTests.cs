using System.Text.Json;
using System.Text.Json.Nodes;
using MoergoLayerViz.Core.Keymap;
using MoergoLayerViz.Core.Tooling;
using Xunit;

namespace MoergoLayerViz.Tests;

public class SignalMacroGeneratorTests
{
    private const string MinimalJson = """
        {
          "keyboard": "go60",
          "layer_names": ["Base", "Symbol", "Cursor"],
          "layers": [
            [{"value":"&trans"}],
            [{"value":"&trans"}],
            [{"value":"&trans"}]
          ]
        }
        """;

    [Fact]
    public void Generate_AddsAllExpectedItems_ForThreeLayerKeyboard()
    {
        var result = SignalMacroGenerator.Generate(MinimalJson, new SignalMacroGenerator.GenerateOptions(StartFkey: 13));

        var names = result.Added.Select(a => a.Name).ToList();
        Assert.Contains("&Tolayer", names);
        Assert.Contains("&Molayer", names);
        Assert.Contains("&mo_base_f13signal", names);
        Assert.Contains("&mo_symbol_f14signal", names);
        Assert.Contains("&mo_cursor_f15signal", names);
        Assert.Contains("&ht_base", names);
        Assert.Contains("&ht_symbol", names);
        Assert.Contains("&ht_cursor", names);

        Assert.Equal(8, result.Added.Count);
        Assert.Empty(result.SkippedExisting);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Generate_IncludesBaseLayer()
    {
        // The base layer gets the same per-layer fixed macro + hold-tap as the
        // others, so &Tolayer 0 F13-style "jump back to base" bindings are
        // trackable.
        var result = SignalMacroGenerator.Generate(MinimalJson, new SignalMacroGenerator.GenerateOptions());
        Assert.Contains(result.Added, a => a.Name == "&mo_base_f13signal");
        Assert.Contains(result.Added, a => a.Name == "&ht_base");
    }

    [Fact]
    public void Generate_FixedMacroShape_MatchesUserHandBuiltExample()
    {
        // The user's existing &mo_symbol_f16signal definition (start=15 puts symbol
        // — layer index 1 — on F16) — golden-shape comparison so any future change
        // is intentional.
        var result = SignalMacroGenerator.Generate(MinimalJson, new SignalMacroGenerator.GenerateOptions(StartFkey: 15));

        var output = JsonNode.Parse(result.OutputJson)!.AsObject();
        var macros = output["macros"]!.AsArray();
        var generated = macros.First(m => m!["name"]!.GetValue<string>() == "&mo_symbol_f16signal")!.AsObject();

        Assert.Equal("", generated["description"]!.GetValue<string>());
        Assert.Empty(generated["params"]!.AsArray());

        var bindings = generated["bindings"]!.AsArray();
        Assert.Equal(7, bindings.Count);
        Assert.Equal("&macro_press", bindings[0]!["value"]!.GetValue<string>());
        Assert.Equal("&mo", bindings[1]!["value"]!.GetValue<string>());
        Assert.Equal(1, bindings[1]!["params"]![0]!["value"]!.GetValue<int>());
        Assert.Equal("&kp", bindings[2]!["value"]!.GetValue<string>());
        Assert.Equal("F16", bindings[2]!["params"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("&macro_pause_for_release", bindings[3]!["value"]!.GetValue<string>());
        Assert.Equal("&macro_release", bindings[4]!["value"]!.GetValue<string>());
        Assert.Equal("&mo", bindings[5]!["value"]!.GetValue<string>());
        Assert.Equal(1, bindings[5]!["params"]![0]!["value"]!.GetValue<int>());
        Assert.Equal("&kp", bindings[6]!["value"]!.GetValue<string>());
        Assert.Equal("F16", bindings[6]!["params"]![0]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void Generate_HoldTapShape_MatchesUserHandBuiltExample()
    {
        var result = SignalMacroGenerator.Generate(MinimalJson, new SignalMacroGenerator.GenerateOptions(StartFkey: 15));

        var output = JsonNode.Parse(result.OutputJson)!.AsObject();
        var holdTaps = output["holdTaps"]!.AsArray();
        var ht = holdTaps.First(h => h!["name"]!.GetValue<string>() == "&ht_symbol")!.AsObject();

        var bindings = ht["bindings"]!.AsArray();
        Assert.Equal(2, bindings.Count);
        Assert.Equal("&mo_symbol_f16signal", bindings[0]!.GetValue<string>());
        Assert.Equal("&kp", bindings[1]!.GetValue<string>());

        Assert.Equal(280, ht["tappingTermMs"]!.GetValue<int>());
        Assert.Equal("balanced", ht["flavor"]!.GetValue<string>());
        Assert.Equal(175, ht["quickTapMs"]!.GetValue<int>());
        Assert.Equal(150, ht["requirePriorIdleMs"]!.GetValue<int>());
        Assert.True(ht["holdTriggerOnRelease"]!.GetValue<bool>());
    }

    [Fact]
    public void Generate_IsIdempotent()
    {
        var first = SignalMacroGenerator.Generate(MinimalJson, new SignalMacroGenerator.GenerateOptions());
        var second = SignalMacroGenerator.Generate(first.OutputJson, new SignalMacroGenerator.GenerateOptions());

        Assert.Empty(second.Added);
        Assert.Equal(first.Added.Count, second.SkippedExisting.Count);
        // Output JSON should be unchanged on the second run.
        Assert.Equal(first.OutputJson, second.OutputJson);
    }

    [Fact]
    public void Generate_StartFkey_IsRespected()
    {
        var result = SignalMacroGenerator.Generate(MinimalJson, new SignalMacroGenerator.GenerateOptions(StartFkey: 20));
        var names = result.Added.Select(a => a.Name).ToList();
        Assert.Contains("&mo_base_f20signal", names);
        Assert.Contains("&mo_symbol_f21signal", names);
        Assert.Contains("&mo_cursor_f22signal", names);
    }

    [Fact]
    public void Generate_WarnsAndSkips_WhenFkeyExceedsF24()
    {
        // 14 layers (L0–L13). Start at F13 → L0..L11 land on F13..F24; L12 → F25, L13 → F26 (both over).
        var layerNames = string.Join(",", Enumerable.Range(0, 14).Select(i => $"\"L{i}\""));
        var layers = string.Join(",", Enumerable.Repeat("[{\"value\":\"&trans\"}]", 14));
        var json = $$"""{ "keyboard": "go60", "layer_names": [{{layerNames}}], "layers": [{{layers}}] }""";

        var result = SignalMacroGenerator.Generate(json, new SignalMacroGenerator.GenerateOptions(StartFkey: 13));

        // 12 layers (F13–F24) generate; L12 and L13 are warned out.
        Assert.Contains("&mo_l11_f24signal", result.Added.Select(a => a.Name));
        Assert.DoesNotContain(result.Added, a => a.Name == "&mo_l12_f25signal");
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("L12"));
        Assert.Contains(result.Warnings, w => w.Contains("L13"));
    }

    [Fact]
    public void Generate_RejectsStartFkey_OutsideValidRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SignalMacroGenerator.Generate(MinimalJson, new SignalMacroGenerator.GenerateOptions(StartFkey: 12)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SignalMacroGenerator.Generate(MinimalJson, new SignalMacroGenerator.GenerateOptions(StartFkey: 25)));
    }

    [Fact]
    public void Generate_PreservesUnknownFields()
    {
        const string jsonWithExtras = """
            {
              "keyboard": "go60",
              "layer_names": ["Base", "Symbol"],
              "layers": [
                [{"value":"&trans"}],
                [{"value":"&trans"}]
              ],
              "_unknownTopLevel": 42,
              "config_parameters": [{"name": "AUTO_LAYER_TIMEOUT_MS", "value": 0}]
            }
            """;

        var result = SignalMacroGenerator.Generate(jsonWithExtras, new SignalMacroGenerator.GenerateOptions());
        var output = JsonNode.Parse(result.OutputJson)!.AsObject();

        Assert.Equal(42, output["_unknownTopLevel"]!.GetValue<int>());
        Assert.Equal("AUTO_LAYER_TIMEOUT_MS",
            output["config_parameters"]!.AsArray()[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Generate_OutputJson_RoundTripsThroughLoader()
    {
        var result = SignalMacroGenerator.Generate(MinimalJson, new SignalMacroGenerator.GenerateOptions());
        var config = MoergoJsonLoader.LoadFromJson(result.OutputJson);

        Assert.Contains(config.Macros, m => m.Name == "&Tolayer");
        Assert.Contains(config.Macros, m => m.Name == "&Molayer");
        Assert.Contains(config.Macros, m => m.Name == "&mo_base_f13signal");
        Assert.Contains(config.Macros, m => m.Name == "&mo_symbol_f14signal");
        Assert.Contains(config.HoldTaps, ht => ht.Name == "&ht_base");
        Assert.Contains(config.HoldTaps, ht => ht.Name == "&ht_symbol");
        Assert.Contains(config.HoldTaps, ht => ht.Name == "&ht_cursor");
    }

    [Fact]
    public void Generate_OutputJson_TriggersSignalMacroDetection()
    {
        var result = SignalMacroGenerator.Generate(MinimalJson, new SignalMacroGenerator.GenerateOptions());
        var config = MoergoJsonLoader.LoadFromJson(result.OutputJson);
        var signals = SignalMacroScanner.DetectSignalMacros(config);

        var names = signals.Select(s => s.MacroName).ToList();
        Assert.Contains("&Tolayer", names);
        Assert.Contains("&Molayer", names);
        Assert.Contains("&mo_base_f13signal", names);
        Assert.Contains("&mo_symbol_f14signal", names);
        Assert.Contains("&mo_cursor_f15signal", names);
    }

    [Theory]
    [InlineData("Symbol", "symbol")]
    [InlineData("Cursor Group", "cursor_group")]
    [InlineData("Func+Mods", "func_mods")]
    [InlineData("  Padded  ", "padded")]
    [InlineData("multi   spaces", "multi_spaces")]
    [InlineData("123Numeric", "123numeric")]
    [InlineData("", "layer4")]
    [InlineData("___", "layer4")]
    public void SanitizeLayerName_ProducesExpectedSlug(string input, string expected)
    {
        Assert.Equal(expected, SignalMacroGenerator.SanitizeLayerName(input, 4));
    }
}
