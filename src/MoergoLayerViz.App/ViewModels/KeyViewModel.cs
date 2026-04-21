using CommunityToolkit.Mvvm.ComponentModel;
using MoergoLayerViz.Core.Layout;
using MoergoLayerViz.Core.Models;

namespace MoergoLayerViz.App.ViewModels;

/// <summary>
/// Per-key bindable: position (from the profile) + display (label + highlight
/// fill derived from the active layer's binding).
/// </summary>
public partial class KeyViewModel : ObservableObject
{
    public KeyPosition Position { get; }

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _behavior = "";
    [ObservableProperty] private bool _isLayerSignalKey;
    [ObservableProperty] private bool _isPressed;
    [ObservableProperty] private bool _isUntrackableLayerSwitch;

    public KeyViewModel(KeyPosition position)
    {
        Position = position;
    }

    /// <summary>
    /// Pushes a new binding from the active layer into this view model.
    /// Driven by <see cref="MainWindowViewModel"/> on layer change.
    /// </summary>
    public void ApplyBinding(KeyBinding binding, bool isSignalMacro, bool isUntrackable)
    {
        Behavior = binding.Behavior;
        Label = FormatLabel(binding);
        IsLayerSignalKey = isSignalMacro;
        IsUntrackableLayerSwitch = isUntrackable;
    }

    private static string FormatLabel(KeyBinding b) => b.Behavior switch
    {
        "&trans" => "▽",
        "&none" => "",
        _ when b.Params.Count == 0 => b.Behavior.TrimStart('&'),
        _ => string.Join(' ', b.Params),
    };
}
