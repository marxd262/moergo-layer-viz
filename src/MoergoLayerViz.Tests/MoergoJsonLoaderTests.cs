using MoergoLayerViz.Core.Keymap;
using MoergoLayerViz.Core.Layout;
using Xunit;

namespace MoergoLayerViz.Tests;

public class MoergoJsonLoaderTests
{
    [Fact]
    public void LoadsGo60FromJson_ProducesFiveLayersWithSixtyKeysEach()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Go60.json");
        Assert.True(File.Exists(path), $"Test fixture missing: {path}");

        var config = MoergoJsonLoader.LoadFromFile(path);
        Assert.Equal("go60", config.KeyboardId);
        Assert.Equal(5, config.LayerCount);
        Assert.All(config.Layers, l => Assert.Equal(60, l.Bindings.Count));
    }

    [Fact]
    public void LoadsGlove80FromJson_ProducesNineLayersWithEightyKeysEach()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Glove80.json");
        Assert.True(File.Exists(path), $"Test fixture missing: {path}");

        var config = MoergoJsonLoader.LoadFromFile(path);
        Assert.Equal("glove80", config.KeyboardId);
        Assert.Equal(9, config.LayerCount);
        Assert.All(config.Layers, l => Assert.Equal(80, l.Bindings.Count));
    }

    [Fact]
    public void Glove80_DetectsTestSignalMacro()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Glove80.json");
        var config = MoergoJsonLoader.LoadFromFile(path);
        var signals = SignalMacroScanner.DetectSignalMacros(config);

        Assert.Single(signals);
        var sig = signals[0];
        Assert.Equal("&test", sig.MacroName);
        Assert.Equal(0, sig.LayerParamIndex);
        Assert.Equal(1, sig.KeyParamIndex);
        Assert.True(sig.IsMomentary);
    }

    [Fact]
    public void Glove80_LayerSignalTable_MapsASignalKeyToLayer1()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Glove80.json");
        var config = MoergoJsonLoader.LoadFromFile(path);
        var signals = SignalMacroScanner.DetectSignalMacros(config);
        var table = LayerSignalTable.Build(config, signals);

        var mapping = table.TryResolve("A");
        Assert.NotNull(mapping);
        Assert.Equal(1, mapping!.TargetLayer);
        Assert.True(mapping.IsMomentary);
    }

    [Fact]
    public void Go60Profile_Returns60KeyPositions()
    {
        var profile = new Go60Profile();
        Assert.Equal(60, profile.Keys.Count);
        Assert.Equal(60, profile.KeyCount);
        // Indexes should be 0..59 contiguous
        for (int i = 0; i < profile.Keys.Count; i++)
            Assert.Equal(i, profile.Keys[i].Index);
    }
}
