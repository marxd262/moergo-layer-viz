namespace MoergoLayerViz.Core.Models;

/// <summary>
/// A ZMK combo: pressing two or more keys at the same key positions fires
/// the wrapped binding instead of the underlying per-key bindings.
/// Combos are scoped per-keyboard-config (not per-layer) in this viewer —
/// any <c>layers</c> field in the source JSON is ignored.
/// </summary>
/// <param name="Number">1-based source-order number used for the legend and on-key badges.</param>
/// <param name="Name">User-given combo name from the Moergo editor (e.g. "F12").</param>
/// <param name="Description">Optional free-text description from the editor.</param>
/// <param name="KeyPositions">Key indices that must all be pressed for the combo to fire.</param>
/// <param name="Binding">The behavior the combo fires.</param>
public sealed record MoergoCombo(
    int Number,
    string Name,
    string Description,
    IReadOnlyList<int> KeyPositions,
    KeyBinding Binding)
{
    /// <summary>
    /// Canonical identity for this combo across keymap reloads: sorted-ascending,
    /// comma-joined key positions (e.g. <c>"12,13"</c>). Used as the dictionary
    /// key for persisted per-combo label overrides.
    /// </summary>
    public string KeyPositionsKey { get; } = string.Join(',', KeyPositions.OrderBy(p => p));
}
