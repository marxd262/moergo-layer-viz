using System.IO;
using System.Linq;
using MoergoLayerViz.App.ViewModels;
using MoergoLayerViz.Core.Keymap;
using MoergoLayerViz.Core.Settings;
using Xunit;

namespace MoergoLayerViz.Tests;

public class CombosRenderingTests
{
    private sealed class InMemorySettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new();
        public UserSettings Load() => Current;
        public void Save(UserSettings settings) => Current = settings;
    }

    [Fact]
    public void Loader_ParsesCombosArray()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Go60.json");
        var config = MoergoJsonLoader.LoadFromFile(path);

        // The Go60 fixture has 4 combos defined: F12, Space, F5, F10.
        Assert.Equal(4, config.Combos.Count);
        var f12 = Assert.Single(config.Combos, c => c.Name == "F12");
        Assert.Equal(new[] { 1, 2 }, f12.KeyPositions);
        Assert.Equal(new[] { -1 }, f12.Layers);
        Assert.Equal("&kp", f12.Binding.Behavior);
        Assert.Equal(new[] { "F12" }, f12.Binding.Params);
    }

    [Fact]
    public void AllLayersCombo_FlagsParticipatingKeysOnEveryLayer()
    {
        var vm = LoadGo60();

        // F12 combo is on key positions 1 + 2, layers = [-1] (all layers).
        for (int layer = 0; layer < vm.Layers.Count; layer++)
        {
            vm.Layers[layer].SelectCommand.Execute(null);
            Assert.True(vm.Keys[1].IsInCombo, $"key 1 should be in combo on layer {layer}");
            Assert.True(vm.Keys[2].IsInCombo, $"key 2 should be in combo on layer {layer}");
        }
    }

    [Fact]
    public void NonComboKey_StaysFlaggedFalse()
    {
        var vm = LoadGo60();

        vm.Layers[0].SelectCommand.Execute(null);
        // Index 0: outermost left-pinky on top row, not part of any combo.
        Assert.False(vm.Keys[0].IsInCombo);
        // Index 30: somewhere in the middle of the board, not part of any combo.
        Assert.False(vm.Keys[30].IsInCombo);
    }

    [Fact]
    public void LayerScopedCombo_OnlyFlagsKeysOnDeclaredLayers()
    {
        // Two-layer config; combo on layer 1 only.
        const string json = """
        {
          "keyboard": "go60",
          "layer_names": ["Base", "Symbol"],
          "layers": [
            [
              { "value": "&kp", "params": [{ "value": "Q" }] },
              { "value": "&kp", "params": [{ "value": "W" }] }
            ],
            [
              { "value": "&kp", "params": [{ "value": "Q" }] },
              { "value": "&kp", "params": [{ "value": "W" }] }
            ]
          ],
          "combos": [
            {
              "name": "Esc",
              "binding": { "value": "&kp", "params": [{ "value": "ESC" }] },
              "keyPositions": [0, 1],
              "layers": [1]
            }
          ]
        }
        """;
        var config = MoergoJsonLoader.LoadFromJson(json);

        var combo = Assert.Single(config.Combos);
        Assert.False(combo.AppliesToLayer(0));
        Assert.True(combo.AppliesToLayer(1));
        Assert.False(combo.AppliesToAllLayers);
    }

    [Fact]
    public void Tooltip_ContainsComboNameKeysAndBoundKeycode()
    {
        var vm = LoadGo60();
        vm.Layers[0].SelectCommand.Execute(null);

        var tooltip = vm.Keys[1].Tooltip;
        Assert.Contains("Combo \"F12\"", tooltip);
        Assert.Contains("&kp F12", tooltip);
        // Participants are listed by their rendered label, joined with " + "
        // (Go60 base layer: idx 1 -> "1" via N1, idx 2 -> "2" via N2).
        Assert.Contains("Keys: 1 + 2", tooltip);
    }

    [Fact]
    public void EmptyCombosCollection_LeavesEveryKeyFalse()
    {
        // Layout with no combos block at all.
        const string json = """
        {
          "keyboard": "go60",
          "layer_names": ["Base"],
          "layers": [[
            { "value": "&kp", "params": [{ "value": "A" }] },
            { "value": "&kp", "params": [{ "value": "B" }] }
          ]]
        }
        """;
        var config = MoergoJsonLoader.LoadFromJson(json);
        Assert.Empty(config.Combos);
    }

    private static MainWindowViewModel LoadGo60()
    {
        var settings = new InMemorySettingsService { Current = new UserSettings { Keyboard = "GO60" } };
        var vm = new MainWindowViewModel(settings);
        vm.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Go60.json"));
        Assert.True(vm.HasLayoutLoaded);
        return vm;
    }
}
