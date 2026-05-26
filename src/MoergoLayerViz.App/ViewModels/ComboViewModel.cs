using CommunityToolkit.Mvvm.ComponentModel;
using MoergoLayerViz.Core.Keymap;
using MoergoLayerViz.Core.Models;
using MoergoLayerViz.Core.Settings;

namespace MoergoLayerViz.App.ViewModels;

/// <summary>
/// One entry in the combo legend: a sequentially-numbered combo for the
/// loaded keyboard config, with its rendered label and the participating-key
/// labels (e.g. "Q + W"). Highlight state is bidirectional — hovering either
/// the badge on the board or this tile in the legend flips
/// <see cref="IsHighlighted"/> on the matching combo via
/// <see cref="MainWindowViewModel.SetHighlightedCombos"/>.
/// </summary>
public partial class ComboViewModel : ObservableObject
{
    public int Number { get; }
    public string ParticipatingKeysText { get; }
    public IReadOnlyList<int> KeyIndices { get; }

    /// <summary>
    /// Stable identity for the combo across keymap reloads — sorted,
    /// comma-joined key positions (e.g. <c>"12,13"</c>). Used as the
    /// dictionary key for persisted label overrides.
    /// </summary>
    public string KeyPositionsKey { get; }

    /// <summary>
    /// What the formatter would produce from the combo's binding alone,
    /// ignoring user overrides. Surfaced in the edit dialog as the
    /// "default" hint so the user can see what they're overriding.
    /// </summary>
    public string DefaultLabel { get; }

    /// <summary>Rendered label, override-aware. Observable so updates from the edit flyout flow without rebuilding the collection.</summary>
    [ObservableProperty] private string _label = "";

    /// <summary>Two-way-bound buffer for the label override TextBox. Seeded from <see cref="Label"/> at construction.</summary>
    [ObservableProperty] private string _editingLabel = "";

    [ObservableProperty] private bool _isHighlighted;

    public ComboViewModel(
        MoergoCombo combo,
        Func<int, string> labelLookup,
        ComboLabelOverride? overrideEntry = null)
    {
        Number = combo.Number;
        KeyIndices = combo.KeyPositions;
        KeyPositionsKey = combo.KeyPositionsKey;

        var (computedLabel, _, _) = KeyLabelFormatter.FormatBinding(combo.Binding, null);
        DefaultLabel = !string.IsNullOrEmpty(computedLabel) ? computedLabel : combo.Binding.Display;
        _label = !string.IsNullOrEmpty(overrideEntry?.MainLabel) ? overrideEntry!.MainLabel : DefaultLabel;
        _editingLabel = !string.IsNullOrEmpty(overrideEntry?.MainLabel) ? overrideEntry!.MainLabel : "";

        ParticipatingKeysText = string.Join(" + ", combo.KeyPositions.Select(idx =>
        {
            var lookup = labelLookup(idx);
            return string.IsNullOrWhiteSpace(lookup) ? idx.ToString() : lookup;
        }));
    }
}
