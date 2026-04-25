using System.IO;
using MoergoLayerViz.App.ViewModels;
using MoergoLayerViz.Core.Settings;
using Xunit;

namespace MoergoLayerViz.Tests;

public class LayerSignalOverrideTests
{
    private sealed class InMemorySettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new();
        public UserSettings Load() => Current;
        public void Save(UserSettings settings) => Current = settings;
    }

    [Fact]
    public void NoLayoutLoaded_NoEffectiveMappings()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService());
        Assert.Empty(vm.EffectiveSignalMappings);
    }

    [Fact]
    public void LoadLayout_PopulatesAutoSignalMappings()
    {
        // Go60.json has at least one signal macro (the F-key signaled layer
        // switches the rest of the runtime depends on); verify a non-empty
        // auto map and that GetAutoSignalKeycodeForLayer returns one of them.
        var vm = new MainWindowViewModel(new InMemorySettingsService { Current = new UserSettings { Keyboard = "GO60" } });
        vm.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Go60_CustomLayerSwitching.json"));
        Assert.NotEmpty(vm.EffectiveSignalMappings);

        var anyAutoLayer = vm.EffectiveSignalMappings.Values.First().TargetLayer;
        Assert.NotNull(vm.GetAutoSignalKeycodeForLayer(anyAutoLayer));
    }

    [Fact]
    public void SetManualLayerSignal_AddsMappingForLayerWithoutAuto()
    {
        var settings = new InMemorySettingsService { Current = new UserSettings { Keyboard = "GO60" } };
        var vm = new MainWindowViewModel(settings);
        vm.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Go60_CustomLayerSwitching.json"));

        // Find a layer that has no auto-detected signal mapping.
        var totalLayers = vm.Layers.Count;
        int? freeLayer = null;
        for (int i = 0; i < totalLayers; i++)
        {
            if (vm.GetAutoSignalKeycodeForLayer(i) is null) { freeLayer = i; break; }
        }
        Assert.NotNull(freeLayer);

        vm.SetManualLayerSignal(freeLayer.Value, "F23");

        Assert.Equal("F23", vm.GetManualSignalKeycodeForLayer(freeLayer.Value));
        Assert.True(vm.EffectiveSignalMappings.ContainsKey("F23"));
        Assert.Equal(freeLayer.Value, vm.EffectiveSignalMappings["F23"].TargetLayer);
        Assert.Equal("F23", settings.Current.ManualLayerSignals["GO60"][freeLayer.Value]);
    }

    [Fact]
    public void SetManualLayerSignal_AutoWins_ManualForCoveredLayerIsIgnoredInTable()
    {
        var settings = new InMemorySettingsService { Current = new UserSettings { Keyboard = "GO60" } };
        var vm = new MainWindowViewModel(settings);
        vm.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Go60_CustomLayerSwitching.json"));

        // Pick a layer that already has an auto-detected signal.
        var coveredLayer = vm.EffectiveSignalMappings.Values.First().TargetLayer;
        Assert.NotNull(vm.GetAutoSignalKeycodeForLayer(coveredLayer));

        vm.SetManualLayerSignal(coveredLayer, "F22");

        // Persisted (UI doesn't lose the user's intent) but not active.
        Assert.Equal("F22", settings.Current.ManualLayerSignals["GO60"][coveredLayer]);
        Assert.False(vm.EffectiveSignalMappings.ContainsKey("F22"));
    }

    [Fact]
    public void SetManualLayerSignal_NullClears_AndPrunesEmptyMap()
    {
        var settings = new InMemorySettingsService { Current = new UserSettings { Keyboard = "GO60" } };
        var vm = new MainWindowViewModel(settings);
        vm.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Go60_CustomLayerSwitching.json"));

        // Pick the first layer without auto coverage.
        int free = Enumerable.Range(0, vm.Layers.Count)
            .First(i => vm.GetAutoSignalKeycodeForLayer(i) is null);

        vm.SetManualLayerSignal(free, "F21");
        Assert.Equal("F21", settings.Current.ManualLayerSignals["GO60"][free]);

        vm.SetManualLayerSignal(free, null);

        Assert.False(settings.Current.ManualLayerSignals.ContainsKey("GO60"));
        Assert.False(vm.EffectiveSignalMappings.ContainsKey("F21"));
    }

    [Fact]
    public void ManualMappings_ScopedPerProfile_DoNotClobberOtherProfile()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                Keyboard = "GO60",
                ManualLayerSignals = new()
                {
                    ["Glove80"] = new() { [3] = "F18" },
                },
            },
        };
        var vm = new MainWindowViewModel(settings);

        vm.SetManualLayerSignal(4, "F19");

        Assert.Equal("F19", settings.Current.ManualLayerSignals["GO60"][4]);
        Assert.Equal("F18", settings.Current.ManualLayerSignals["Glove80"][3]);
    }

    [Fact]
    public void ManualMappings_SeededFromSettings_AppearInEffectiveTableForUncoveredLayer()
    {
        // Pre-seed a manual mapping for a layer we know has no auto coverage:
        // we don't know which layers are auto-covered without loading first,
        // so seed an unlikely high layer index that won't conflict, then load
        // and verify it surfaces in the merged table.
        var settings = new InMemorySettingsService { Current = new UserSettings { Keyboard = "GO60" } };
        var vm = new MainWindowViewModel(settings);
        vm.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Go60_CustomLayerSwitching.json"));

        int free = Enumerable.Range(0, vm.Layers.Count)
            .First(i => vm.GetAutoSignalKeycodeForLayer(i) is null);

        // Persist the seed via Save and rebuild.
        var freshSettings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                Keyboard = "GO60",
                ManualLayerSignals = new() { ["GO60"] = new() { [free] = "F20" } },
            },
        };
        var vm2 = new MainWindowViewModel(freshSettings);
        vm2.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Go60.json"));

        Assert.True(vm2.EffectiveSignalMappings.ContainsKey("F20"));
        Assert.Equal(free, vm2.EffectiveSignalMappings["F20"].TargetLayer);
        Assert.False(vm2.EffectiveSignalMappings["F20"].IsMomentary);
    }
}
