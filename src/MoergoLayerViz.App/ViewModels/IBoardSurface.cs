using System.Collections.ObjectModel;

namespace MoergoLayerViz.App.ViewModels;

/// <summary>
/// Minimal property surface that <c>BoardView</c> binds against. Implemented
/// by <see cref="MainWindowViewModel"/> (the main keyboard render) and
/// <see cref="ExitKeyPickerViewModel"/> (the auto-switch exit-key picker
/// popup) so the same XAML renders both without forking the control.
///
/// <para>BoardView's compiled bindings target this interface; the picker VM
/// supplies a no-binding-context <see cref="KeyViewModel"/> list (empty
/// labels, no combo earmarks) and a click handler via
/// <see cref="OnKeyTapped"/>, while the main VM leaves the click handler
/// null so taps in the main window are inert.</para>
/// </summary>
public interface IBoardSurface
{
    ObservableCollection<KeyViewModel> LeftKeys { get; }
    ObservableCollection<KeyViewModel> RightKeys { get; }

    /// <summary>Canvas geometry + stacked-mode offsets the BoardView binds against.</summary>
    BoardLayoutViewModel BoardLayout { get; }

    /// <summary>Press-highlight fill/stroke colors for the per-key press dot.</summary>
    BoardStyleViewModel BoardStyle { get; }

    /// <summary>
    /// Optional per-cap click handler. When non-null, <c>BoardView</c>'s
    /// code-behind invokes this with the tapped <see cref="KeyViewModel"/>'s
    /// <c>Position.Index</c>. The main VM leaves it null so the main window's
    /// rendering stays read-only; the picker VM provides the handler.
    /// </summary>
    Action<int>? OnKeyTapped { get; }
}
