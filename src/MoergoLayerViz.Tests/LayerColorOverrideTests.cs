using System.IO;
using MoergoLayerViz.App.ViewModels;
using MoergoLayerViz.Core.Settings;
using Xunit;

namespace MoergoLayerViz.Tests;

public class LayerColorOverrideTests
{
    private sealed class InMemorySettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new();
        public UserSettings Load() => Current;
        public void Save(UserSettings settings) => Current = settings;
    }

    // The palette is static; constructing a MainWindowViewModel re-seeds it
    // from settings, so most tests reset palette state implicitly. Tests that
    // poke the palette directly call SetOverrides(empty) to start fresh.

    [Fact]
    public void Palette_NoOverride_ReturnsBuiltInDefault()
    {
        LayerColorPalette.SetOverrides(new());
        Assert.Equal(LayerColorPalette.GetDefaultColor(2), LayerColorPalette.GetColor("GO60", 2));
    }

    [Fact]
    public void Palette_Override_WinsAndIsScopedPerProfile()
    {
        LayerColorPalette.SetOverrides(new Dictionary<string, Dictionary<int, string>>
        {
            ["GO60"] = new() { [2] = "#FF00FF" },
        });
        Assert.Equal("#FF00FF", LayerColorPalette.GetColor("GO60", 2));
        Assert.Equal(LayerColorPalette.GetDefaultColor(2), LayerColorPalette.GetColor("Glove80", 2));
    }

    [Fact]
    public void Constructor_SeedsPaletteFromBothProfiles()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                LayerColors = new()
                {
                    ["GO60"] = new() { [0] = "#AA0000" },
                    ["Glove80"] = new() { [0] = "#00AA00" },
                },
            },
        };
        _ = new MainWindowViewModel(settings);
        Assert.Equal("#AA0000", LayerColorPalette.GetColor("GO60", 0));
        Assert.Equal("#00AA00", LayerColorPalette.GetColor("Glove80", 0));
    }

    [Fact]
    public void LoadLayout_TabColors_ReflectActiveProfileOverrides()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                Keyboard = "GO60",
                LayerColors = new()
                {
                    ["GO60"] = new() { [1] = "#123456" },
                },
            },
        };
        var vm = new MainWindowViewModel(settings);
        vm.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Go60.json"));

        Assert.True(vm.Layers.Count >= 2);
        Assert.Equal("#123456", vm.Layers[1].TabColor);
        // Layer 0 has no override — falls through to the built-in palette.
        Assert.Equal(LayerColorPalette.GetDefaultColor(0), vm.Layers[0].TabColor);
    }

    [Fact]
    public void LoadLayout_KeyFillColor_HonoursOverrideForLayerSwitchKeys()
    {
        // Setting a magenta override on layer 1 must reach any &mo 1 / &lt 1 X /
        // signal-macro key painted with the layer-1 tint. We don't care which
        // physical key — just that at least one paints with the override.
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                Keyboard = "GO60",
                LayerColors = new()
                {
                    ["GO60"] = new() { [1] = "#FF00FF" },
                },
            },
        };
        var vm = new MainWindowViewModel(settings);
        vm.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Go60.json"));

        Assert.Contains(vm.Keys, k => k.KeyFillColor == "#FF00FF");
    }

    [Fact]
    public void SetLayerColorOverride_PersistsAndRepaintsTabInPlace()
    {
        var settings = new InMemorySettingsService { Current = new UserSettings { Keyboard = "GO60" } };
        var vm = new MainWindowViewModel(settings);
        vm.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Go60.json"));
        var originalTabInstance = vm.Layers[1];

        vm.SetLayerColorOverride(1, "#ABCDEF");

        Assert.Equal("#ABCDEF", vm.Layers[1].TabColor);
        Assert.Same(originalTabInstance, vm.Layers[1]);  // repaint, not rebuild.
        Assert.Equal("#ABCDEF", settings.Current.LayerColors["GO60"][1]);
    }

    [Fact]
    public void SetLayerColorOverride_NullClearsOverrideAndPrunesEmptyMap()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                Keyboard = "GO60",
                LayerColors = new()
                {
                    ["GO60"] = new() { [1] = "#ABCDEF" },
                },
            },
        };
        var vm = new MainWindowViewModel(settings);
        vm.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Go60.json"));

        vm.SetLayerColorOverride(1, null);

        Assert.Equal(LayerColorPalette.GetDefaultColor(1), vm.Layers[1].TabColor);
        Assert.False(settings.Current.LayerColors.ContainsKey("GO60"));
    }

    [Fact]
    public void SetLayerColorOverride_DoesNotClobberOtherProfilesEntries()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                Keyboard = "GO60",
                LayerColors = new()
                {
                    ["Glove80"] = new() { [0] = "#00AA00" },
                },
            },
        };
        var vm = new MainWindowViewModel(settings);

        vm.SetLayerColorOverride(0, "#AA0000");

        Assert.Equal("#AA0000", settings.Current.LayerColors["GO60"][0]);
        Assert.Equal("#00AA00", settings.Current.LayerColors["Glove80"][0]);
    }
}
