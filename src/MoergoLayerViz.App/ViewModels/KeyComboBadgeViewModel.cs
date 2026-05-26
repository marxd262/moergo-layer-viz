using CommunityToolkit.Mvvm.ComponentModel;

namespace MoergoLayerViz.App.ViewModels;

/// <summary>
/// One numbered pill rendered on a key when the combo overlay is on. A key
/// in N combos gets N badges (capped at <see cref="MaxBadgesPerKey"/> — extra
/// combos are still listed in the per-key tooltip). Each badge maps 1:1 to a
/// combo, so hovering it highlights only that combo's legend tile.
/// </summary>
public partial class KeyComboBadgeViewModel : ObservableObject
{
    /// <summary>
    /// Cap on the rendered badges per key — extra combo numbers are dropped
    /// from the strip (still listed in the tooltip).
    /// </summary>
    public const int MaxBadgesPerKey = 3;

    public int Number { get; }

    [ObservableProperty] private bool _isHighlighted;

    public KeyComboBadgeViewModel(int number) => Number = number;
}
