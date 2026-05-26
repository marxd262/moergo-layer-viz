using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MoergoLayerViz.App.ViewModels;

namespace MoergoLayerViz.App.Views;

public partial class BoardView : UserControl
{
    private static readonly int[] EmptyComboNumbers = System.Array.Empty<int>();

    public BoardView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Routes a cap tap to the hosting <see cref="IBoardSurface"/>'s
    /// <c>OnKeyTapped</c> handler. Main window leaves the handler null so
    /// taps are inert; the exit-key picker provides a real handler that
    /// adds/removes the cap's index from the in-progress selection.
    /// </summary>
    private void OnKeyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c) return;
        if (c.DataContext is not KeyViewModel keyVm) return;
        if (DataContext is not IBoardSurface surface) return;
        var handler = surface.OnKeyTapped;
        if (handler is null) return;
        handler(keyVm.Position.Index);
        e.Handled = true;
    }

    /// <summary>
    /// Pointer enters a per-combo pill on a key → highlight that single
    /// combo's legend tile (and every participating-key pill across the
    /// board, via the combo overlay coordinator). Inert if the hosting
    /// surface isn't the main VM (e.g. the exit-key picker).
    /// </summary>
    private void OnComboBadgePointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c) return;
        if (c.DataContext is not KeyComboBadgeViewModel badge) return;
        if (TryGetMainVm(c) is not { } mainVm) return;
        mainVm.ComboOverlay.SetHighlightedCombos(new[] { badge.Number });
    }

    private void OnComboBadgePointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c) return;
        if (TryGetMainVm(c) is not { } mainVm) return;
        mainVm.ComboOverlay.SetHighlightedCombos(EmptyComboNumbers);
    }

    private static MainWindowViewModel? TryGetMainVm(Control source) =>
        source.FindAncestorOfType<MainWindow>()?.DataContext as MainWindowViewModel;
}
