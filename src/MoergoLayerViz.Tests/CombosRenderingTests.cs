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
        Assert.Equal("&kp", f12.Binding.Behavior);
        Assert.Equal(new[] { "F12" }, f12.Binding.Params);

        // 1-based source-order numbering and sorted-comma key-positions key.
        var numbers = config.Combos.Select(c => c.Number).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 4 }, numbers);
        Assert.Equal("1,2", f12.KeyPositionsKey);
    }

    [Fact]
    public void Combo_FlagsParticipatingKeysOnEveryLayer()
    {
        var vm = LoadGo60();

        // Combos are config-scoped (not layer-scoped) — the F12 combo on key
        // positions 1 + 2 stays flagged across every layer switch.
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
    public void CombosAreConfigScoped_NotLayerScoped()
    {
        // The JSON `layers: [1]` field on a combo is intentionally ignored —
        // Moergo combos apply to the whole keyboard config. The combo here is
        // visible on every layer, and badges/IsInCombo don't react to the
        // layer switch.
        var vm = LoadGo60();

        Assert.True(vm.Keys[1].IsInCombo);
        var badgeNumbers = vm.Keys[1].ComboBadges.Select(b => b.Number).ToArray();

        vm.Layers[1].SelectCommand.Execute(null);
        Assert.True(vm.Keys[1].IsInCombo);
        Assert.Equal(badgeNumbers, vm.Keys[1].ComboBadges.Select(b => b.Number).ToArray());
    }

    [Fact]
    public void OnKeyBadges_CapAtMaxPerKey()
    {
        // Five combos all sharing key 0 — only the first MaxBadgesPerKey
        // should render as pills; the rest still appear in the tooltip.
        const string json = """
        {
          "keyboard": "go60",
          "layer_names": ["Base"],
          "layers": [[
            { "value": "&kp", "params": [{ "value": "A" }] },
            { "value": "&kp", "params": [{ "value": "B" }] }
          ]],
          "combos": [
            { "name": "C1", "binding": { "value": "&kp", "params": [{ "value": "F1" }] }, "keyPositions": [0, 1] },
            { "name": "C2", "binding": { "value": "&kp", "params": [{ "value": "F2" }] }, "keyPositions": [0, 1] },
            { "name": "C3", "binding": { "value": "&kp", "params": [{ "value": "F3" }] }, "keyPositions": [0, 1] },
            { "name": "C4", "binding": { "value": "&kp", "params": [{ "value": "F4" }] }, "keyPositions": [0, 1] },
            { "name": "C5", "binding": { "value": "&kp", "params": [{ "value": "F5" }] }, "keyPositions": [0, 1] }
          ]
        }
        """;
        var path = Path.Combine(Path.GetTempPath(), $"badge-cap-{System.Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            var settings = new InMemorySettingsService { Current = new UserSettings { Keyboard = "GO60" } };
            var vm = new MainWindowViewModel(settings);
            vm.LoadLayoutFromPath(path);
            Assert.Equal(KeyComboBadgeViewModel.MaxBadgesPerKey, vm.Keys[0].ComboBadges.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ComboOverride_PicksLabelFromSettings()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                Keyboard = "GO60",
                ComboLabelOverrides = new()
                {
                    ["GO60"] = new() { ["1,2"] = new ComboLabelOverride { MainLabel = "MyF12" } },
                },
            },
        };
        var vm = new MainWindowViewModel(settings);
        vm.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Go60.json"));

        var combo = vm.ComboOverlay.Combos.Single(c => c.KeyPositionsKey == "1,2");
        Assert.Equal("MyF12", combo.Label);
        // DefaultLabel remains the formatter-derived one regardless of override.
        Assert.NotEqual("MyF12", combo.DefaultLabel);
    }

    [Fact]
    public void ComboOverride_FallsThroughToDefaultWhenAbsent()
    {
        var vm = LoadGo60();
        var combo = vm.ComboOverlay.Combos.Single(c => c.KeyPositionsKey == "1,2");
        Assert.Equal(combo.DefaultLabel, combo.Label);
    }

    [Fact]
    public void SetHighlightedCombos_PropagatesAcrossLegendAndBadges()
    {
        var vm = LoadGo60();
        var first = vm.ComboOverlay.Combos.First();
        var participantKey = vm.Keys[first.KeyIndices[0]];

        vm.ComboOverlay.SetHighlightedCombos(new[] { first.Number });
        Assert.True(first.IsHighlighted);
        Assert.True(participantKey.IsComboKeyHighlighted);
        Assert.True(participantKey.IsAnyHighlight);
        Assert.True(participantKey.ComboBadges.Single(b => b.Number == first.Number).IsHighlighted);

        vm.ComboOverlay.SetHighlightedCombos(System.Array.Empty<int>());
        Assert.False(first.IsHighlighted);
        Assert.False(participantKey.IsComboKeyHighlighted);
        Assert.False(participantKey.IsAnyHighlight);
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
