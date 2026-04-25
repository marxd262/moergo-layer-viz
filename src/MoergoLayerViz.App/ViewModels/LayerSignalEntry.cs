using CommunityToolkit.Mvvm.ComponentModel;

namespace MoergoLayerViz.App.ViewModels;

/// <summary>
/// One choice in a <see cref="LayerSignalEntry"/> picker. <see cref="Keycode"/>
/// is null for the "(none)" sentinel; otherwise it's a ZMK keycode like "F17".
/// Record equality lets ComboBox.SelectedItem two-way binding match by value
/// across rebuilds.
/// </summary>
public sealed record LayerSignalChoice(string DisplayName, string? Keycode);

/// <summary>
/// One row in the Settings window's per-layer signal-key list. Layers with an
/// auto-detected signal macro show a read-only label ("F16 (auto)") and the
/// picker is hidden — auto always wins. Layers without an auto mapping get a
/// ComboBox of unused F-keys.
/// </summary>
public partial class LayerSignalEntry : ObservableObject
{
    private readonly Action<int, string?> _setManual;

    public int Index { get; }
    public string DisplayName { get; }
    public string? AutoKeycode { get; }
    public string AutoDisplayText { get; }
    public bool IsAutoDetected => AutoKeycode is not null;
    public bool IsManualEditable => !IsAutoDetected;

    public IReadOnlyList<LayerSignalChoice> AvailableChoices { get; }

    [ObservableProperty]
    private LayerSignalChoice _selectedChoice;

    public LayerSignalEntry(
        int index,
        string displayName,
        string? autoKeycode,
        string autoDisplayText,
        string? currentManualKeycode,
        IReadOnlyList<LayerSignalChoice> availableChoices,
        Action<int, string?> setManual)
    {
        Index = index;
        DisplayName = displayName;
        AutoKeycode = autoKeycode;
        AutoDisplayText = autoDisplayText;
        AvailableChoices = availableChoices;
        _setManual = setManual;
        // Default to (none) — index 0 — if the persisted value isn't in the
        // current available set (e.g. the keycode collided with an auto entry
        // that arrived later). Caller guarantees AvailableChoices[0] is "(none)".
        _selectedChoice = availableChoices.FirstOrDefault(c => c.Keycode == currentManualKeycode)
                          ?? availableChoices[0];
    }

    partial void OnSelectedChoiceChanged(LayerSignalChoice value) =>
        _setManual(Index, value?.Keycode);
}
