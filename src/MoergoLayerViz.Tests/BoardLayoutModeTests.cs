using MoergoLayerViz.App.ViewModels;
using MoergoLayerViz.Core.Layout;
using MoergoLayerViz.Core.Settings;
using Xunit;

namespace MoergoLayerViz.Tests;

public class BoardLayoutModeTests
{
    private sealed class InMemorySettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new();
        public UserSettings Load() => Current;
        public void Save(UserSettings settings) => Current = settings;
    }

    [Fact]
    public void HorizontalMode_PreservesProfileCanvas()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService());
        Assert.False(vm.BoardLayout.IsStackedLayout);

        var profile = new Go60Profile();
        Assert.Equal(profile.CanvasWidth, vm.BoardLayout.CanvasWidth);
        Assert.Equal(profile.CanvasHeight, vm.BoardLayout.CanvasHeight);
        Assert.Equal(0, vm.BoardLayout.LeftHandX);
        Assert.Equal(0, vm.BoardLayout.LeftHandY);
        Assert.Equal(0, vm.BoardLayout.RightHandX);
        Assert.Equal(0, vm.BoardLayout.RightHandY);
    }

    [Fact]
    public void StackedMode_StacksHalvesVertically()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService()) { BoardLayout = { IsStackedLayout = true } };
        var profile = new Go60Profile();

        // Stacked canvas should be roughly: each half's height + gap, instead of side-by-side.
        Assert.True(vm.BoardLayout.CanvasHeight > profile.CanvasHeight,
            $"Expected stacked height > profile height; got {vm.BoardLayout.CanvasHeight} vs {profile.CanvasHeight}");
        Assert.True(vm.BoardLayout.CanvasWidth < profile.CanvasWidth,
            $"Expected stacked width < profile width; got {vm.BoardLayout.CanvasWidth} vs {profile.CanvasWidth}");
    }

    [Fact]
    public void StackedMode_LeftOnTop_PutsRightBelow()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService())
        {
            BoardLayout = { IsStackedLayout = true, StackedTopHand = "Left" },
        };
        Assert.True(vm.BoardLayout.RightHandY > vm.BoardLayout.LeftHandY,
            $"Right should sit below Left; got LeftY={vm.BoardLayout.LeftHandY}, RightY={vm.BoardLayout.RightHandY}");
    }

    [Fact]
    public void StackedMode_RightOnTop_PutsLeftBelow()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService())
        {
            BoardLayout = { IsStackedLayout = true, StackedTopHand = "Right" },
        };
        Assert.True(vm.BoardLayout.LeftHandY > vm.BoardLayout.RightHandY,
            $"Left should sit below Right; got LeftY={vm.BoardLayout.LeftHandY}, RightY={vm.BoardLayout.RightHandY}");
    }

    [Fact]
    public void StackedMode_TopHandToggleSwapsWhichHandSitsLower()
    {
        // The two halves don't have to be equal-height (GO60's right thumb cluster
        // dips lower than the left), so absolute Y values won't be a literal swap —
        // just the *ordering* should flip.
        var vm = new MainWindowViewModel(new InMemorySettingsService()) { BoardLayout = { IsStackedLayout = true } };
        vm.BoardLayout.StackedTopHand = "Left";
        Assert.True(vm.BoardLayout.LeftHandY < vm.BoardLayout.RightHandY);

        vm.BoardLayout.StackedTopHand = "Right";
        Assert.True(vm.BoardLayout.RightHandY < vm.BoardLayout.LeftHandY);
    }

    [Fact]
    public void Go60Profile_TagsKeysWithHand()
    {
        var keys = new Go60Profile().Keys;
        // GO60: rows 0–47 alternate 6 left + 6 right. Index 0 = left, 6 = right.
        Assert.Equal(Hand.Left, keys[0].Hand);
        Assert.Equal(Hand.Right, keys[6].Hand);
        // Thumb-cluster boundary keys per the profile docstring.
        Assert.Equal(Hand.Left, keys[56].Hand);
        Assert.Equal(Hand.Right, keys[57].Hand);

        Assert.Equal(30, keys.Count(k => k.Hand == Hand.Left));
        Assert.Equal(30, keys.Count(k => k.Hand == Hand.Right));
    }

    [Fact]
    public void Glove80Profile_TagsKeysWithHand()
    {
        var keys = new Glove80Profile().Keys;
        Assert.Equal(Hand.Left, keys[0].Hand);
        // First right-hand F-key sits right after the 5 left F-keys.
        Assert.Equal(Hand.Right, keys[5].Hand);
        // Thumb-cluster boundary keys per the profile docstring.
        Assert.Equal(Hand.Left, keys[54].Hand);
        Assert.Equal(Hand.Right, keys[55].Hand);

        Assert.Equal(40, keys.Count(k => k.Hand == Hand.Left));
        Assert.Equal(40, keys.Count(k => k.Hand == Hand.Right));
    }

    [Fact]
    public void Settings_RoundTripStackedFields()
    {
        var settings = new InMemorySettingsService();
        var vm = new MainWindowViewModel(settings)
        {
            BoardLayout = { IsStackedLayout = true, StackedTopHand = "Right" },
        };
        Assert.True(settings.Current.StackedLayout);
        Assert.Equal("Right", settings.Current.StackedTopHand);

        // Round-trip: a new VM seeded from the persisted settings should reflect them.
        var vm2 = new MainWindowViewModel(settings);
        Assert.True(vm2.BoardLayout.IsStackedLayout);
        Assert.Equal("Right", vm2.BoardLayout.StackedTopHand);
    }

    [Fact]
    public void BuildKeysFromProfile_PartitionsKeysByHand()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService());
        Assert.Equal(60, vm.Keys.Count);
        Assert.Equal(30, vm.LeftKeys.Count);
        Assert.Equal(30, vm.RightKeys.Count);
        Assert.All(vm.LeftKeys, k => Assert.Equal(Hand.Left, k.Position.Hand));
        Assert.All(vm.RightKeys, k => Assert.Equal(Hand.Right, k.Position.Hand));
    }
}
