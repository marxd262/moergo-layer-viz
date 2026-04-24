using System.IO;
using MoergoLayerViz.App.ViewModels;
using MoergoLayerViz.Core.Layout;
using MoergoLayerViz.Core.Settings;
using Xunit;

namespace MoergoLayerViz.Tests;

public class MainWindowViewModelKeyboardPickerTests
{
    private sealed class InMemorySettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new();
        public UserSettings Load() => Current;
        public void Save(UserSettings settings) => Current = settings;
    }

    [Fact]
    public void SelectKeyboard_UnloadsLayoutWhenKeyCountMismatches()
    {
        var settings = new InMemorySettingsService { Current = new UserSettings { Keyboard = "GO60" } };
        var vm = new MainWindowViewModel(settings);

        vm.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Go60.json"));
        Assert.True(vm.HasLayoutLoaded);
        Assert.Equal(60, vm.Keys.Count);

        vm.SelectKeyboardCommand.Execute(new Glove80Profile());

        Assert.False(vm.HasLayoutLoaded);
        Assert.Equal(80, vm.Keys.Count);
        Assert.Empty(vm.Layers);
        Assert.Equal("Glove80", settings.Current.Keyboard);
        Assert.Equal("Glove80", vm.SelectedKeyboard.Id);
    }

    [Fact]
    public void SelectKeyboard_SameProfileIsNoOp()
    {
        var settings = new InMemorySettingsService { Current = new UserSettings { Keyboard = "GO60" } };
        var vm = new MainWindowViewModel(settings);
        vm.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Go60.json"));

        var layersBefore = vm.Layers.Count;
        var keysBefore = vm.Keys.Count;

        vm.SelectKeyboardCommand.Execute(new Go60Profile());

        Assert.True(vm.HasLayoutLoaded);
        Assert.Equal(layersBefore, vm.Layers.Count);
        Assert.Equal(keysBefore, vm.Keys.Count);
        Assert.Equal("GO60", vm.SelectedKeyboard.Id);
    }

    [Fact]
    public void SelectKeyboard_WithNoLayoutJustSwapsProfileAndPersists()
    {
        var settings = new InMemorySettingsService { Current = new UserSettings { Keyboard = "GO60" } };
        var vm = new MainWindowViewModel(settings);

        Assert.Equal(60, vm.Keys.Count);

        vm.SelectKeyboardCommand.Execute(new Glove80Profile());

        Assert.Equal(80, vm.Keys.Count);
        Assert.False(vm.HasLayoutLoaded);
        Assert.Equal("Glove80", settings.Current.Keyboard);
    }

    [Fact]
    public void LoadLayout_AutoSwitchesProfileWhenBindingCountMatchesOtherProfile()
    {
        var settings = new InMemorySettingsService { Current = new UserSettings { Keyboard = "GO60" } };
        var vm = new MainWindowViewModel(settings);

        vm.LoadLayoutFromPath(Path.Combine(AppContext.BaseDirectory, "Glove80.json"));

        Assert.True(vm.HasLayoutLoaded);
        Assert.Equal("Glove80", vm.SelectedKeyboard.Id);
        Assert.Equal(80, vm.Keys.Count);
        Assert.Equal("Glove80", settings.Current.Keyboard);
    }

    [Fact]
    public void Constructor_SeedsSelectedKeyboardFromSettings()
    {
        var settings = new InMemorySettingsService { Current = new UserSettings { Keyboard = "Glove80" } };
        var vm = new MainWindowViewModel(settings);

        Assert.Equal("Glove80", vm.SelectedKeyboard.Id);
        Assert.Equal(80, vm.Keys.Count);
    }
}
