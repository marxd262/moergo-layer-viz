using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoergoLayerViz.App.ViewModels;
using MoergoLayerViz.Core.Diagnostics;
using MoergoLayerViz.Core.Keymap;
using MoergoLayerViz.Core.Models;
using MoergoLayerViz.Core.Settings;

namespace MoergoLayerViz.App.Services;

/// <summary>
/// Owns the combo overlay's view-model state: the legend collection, per-key
/// combo wiring, the hover/sticky highlight state machine, and label-override
/// persistence. <see cref="MainWindowViewModel"/> hosts the top-level
/// <c>IsComboOverlayVisible</c> toggle and forwards keyboard-config changes
/// here via <see cref="Rebuild"/> / <see cref="Clear"/>.
///
/// Combos are config-scoped (not layer-scoped), so legend + per-key state are
/// rebuilt only at config load and at label-override save — never on a layer
/// change.
/// </summary>
public sealed partial class ComboOverlayCoordinator : ObservableObject
{
    private readonly ISettingsService _settings;
    private IReadOnlyList<KeyViewModel> _keys = System.Array.Empty<KeyViewModel>();
    private KeyboardConfig? _config;
    private LayerBindingResolver? _resolver;
    private string _profileId = "";
    private int? _stickyComboNumber;

    /// <summary>Numbered legend entries. Empty when no layout is loaded.</summary>
    public ObservableCollection<ComboViewModel> Combos { get; } = new();

    public ComboOverlayCoordinator(ISettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Rebuilds combos for a freshly-loaded keymap config. Pass a null
    /// <paramref name="config"/> to fully tear down the overlay state.
    /// </summary>
    public void Rebuild(KeyboardConfig? config, LayerBindingResolver? resolver, string profileId, IReadOnlyList<KeyViewModel> keys)
    {
        _config = config;
        _resolver = resolver;
        _profileId = profileId;
        _keys = keys;
        _stickyComboNumber = null;
        Build();
    }

    /// <summary>Clears legend + per-key state without changing the registered keys / profile.</summary>
    public void Clear()
    {
        _config = null;
        _resolver = null;
        _stickyComboNumber = null;
        Combos.Clear();
        foreach (var key in _keys) key.SetCombos(System.Array.Empty<MoergoCombo>(), _ => "");
    }

    private void Build()
    {
        Combos.Clear();
        foreach (var key in _keys) key.SetCombos(System.Array.Empty<MoergoCombo>(), _ => "");
        if (_config is null || _config.Combos.Count == 0) return;

        var byKey = new Dictionary<int, List<MoergoCombo>>();
        foreach (var combo in _config.Combos)
        {
            foreach (var keyIdx in combo.KeyPositions)
            {
                if (!byKey.TryGetValue(keyIdx, out var list))
                    byKey[keyIdx] = list = new List<MoergoCombo>();
                list.Add(combo);
            }
        }

        var overrides = LoadOverridesForProfile();
        // Participating-key labels are pulled from the base layer (index 0)
        // and frozen at config-load — they're the user's mental anchor for
        // "what physical key am I pressing", so they shouldn't shift when the
        // active layer changes.
        string LabelLookup(int idx)
        {
            if (_resolver is null || _config is null) return "";
            if (_config.LayerCount == 0) return "";
            if (idx < 0 || idx >= _config.Layers[0].Bindings.Count) return "";
            var binding = _resolver.ResolveEffectiveBinding(0, idx);
            var (main, _, _) = KeyLabelFormatter.FormatBinding(binding, null);
            return string.IsNullOrWhiteSpace(main) ? "" : main;
        }
        foreach (var combo in _config.Combos)
        {
            overrides.TryGetValue(combo.KeyPositionsKey, out var ov);
            Combos.Add(new ComboViewModel(combo, LabelLookup, ov));
        }

        foreach (var (keyIdx, keyCombos) in byKey)
        {
            if (keyIdx >= 0 && keyIdx < _keys.Count)
                _keys[keyIdx].SetCombos(keyCombos, LabelLookup);
        }
    }

    // --- highlight state machine ---

    /// <summary>
    /// Pointer-entered / pointer-exited on a legend tile. Null restores the
    /// sticky selection (if any) so a left-click highlight pins past pointer-exit.
    /// </summary>
    public void HoverCombo(int? number)
    {
        var n = number ?? _stickyComboNumber;
        SetHighlightedCombos(n is null ? System.Array.Empty<int>() : new[] { n.Value });
    }

    /// <summary>Left-click on a legend tile — toggles sticky highlight.</summary>
    public void ToggleStickyCombo(int number)
    {
        _stickyComboNumber = _stickyComboNumber == number ? null : number;
        SetHighlightedCombos(_stickyComboNumber is null ? System.Array.Empty<int>() : new[] { _stickyComboNumber.Value });
    }

    /// <summary>Drops sticky state and clears every highlight. Used when the overlay is hidden.</summary>
    public void ClearHighlights()
    {
        _stickyComboNumber = null;
        SetHighlightedCombos(System.Array.Empty<int>());
    }

    /// <summary>
    /// Direct setter — used by the on-key badge hover handler and internally by
    /// <see cref="HoverCombo"/> / <see cref="ToggleStickyCombo"/>. Empty
    /// <paramref name="numbers"/> clears every highlight.
    /// </summary>
    public void SetHighlightedCombos(IReadOnlyList<int> numbers)
    {
        var set = numbers is { Count: > 0 } ? new HashSet<int>(numbers) : null;
        foreach (var combo in Combos)
            combo.IsHighlighted = set is not null && set.Contains(combo.Number);
        foreach (var key in _keys)
        {
            bool any = false;
            foreach (var badge in key.ComboBadges)
            {
                var on = set is not null && set.Contains(badge.Number);
                badge.IsHighlighted = on;
                if (on) any = true;
            }
            key.IsComboKeyHighlighted = any;
        }
    }

    // --- label overrides ---

    /// <summary>
    /// Persists a per-combo label override (or clears it when
    /// <paramref name="label"/> is empty/whitespace) for the active keyboard
    /// profile, then rebuilds the legend so the new label is visible
    /// immediately.
    /// </summary>
    public void SetLabelOverride(string keyPositionsKey, string? label)
    {
        if (string.IsNullOrWhiteSpace(keyPositionsKey)) return;
        var profileId = _profileId;
        var trimmed = label?.Trim();

        Persist(s =>
        {
            var clone = new Dictionary<string, Dictionary<string, ComboLabelOverride>>();
            foreach (var (pid, perCombo) in s.ComboLabelOverrides)
                clone[pid] = new Dictionary<string, ComboLabelOverride>(perCombo);

            if (string.IsNullOrEmpty(trimmed))
            {
                if (clone.TryGetValue(profileId, out var inner))
                {
                    inner.Remove(keyPositionsKey);
                    if (inner.Count == 0) clone.Remove(profileId);
                }
            }
            else
            {
                if (!clone.TryGetValue(profileId, out var inner))
                    clone[profileId] = inner = new Dictionary<string, ComboLabelOverride>();
                inner[keyPositionsKey] = new ComboLabelOverride { MainLabel = trimmed };
            }
            return s with { ComboLabelOverrides = clone };
        });

        Build();
    }

    private Dictionary<string, ComboLabelOverride> LoadOverridesForProfile()
    {
        var s = _settings.Load();
        return s.ComboLabelOverrides.TryGetValue(_profileId, out var inner)
            ? new Dictionary<string, ComboLabelOverride>(inner)
            : new Dictionary<string, ComboLabelOverride>();
    }

    private void Persist(Func<UserSettings, UserSettings> update)
    {
        try
        {
            var s = _settings.Load();
            _settings.Save(update(s));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("ComboOverlay", $"Persist settings failed: {ex.Message}");
        }
    }

    /// <summary>Save the buffered <see cref="ComboViewModel.EditingLabel"/> as the override.</summary>
    [RelayCommand]
    private void SaveLabel(ComboViewModel? combo)
    {
        if (combo is null) return;
        SetLabelOverride(combo.KeyPositionsKey, combo.EditingLabel);
    }

    /// <summary>Clear the override for the given combo.</summary>
    [RelayCommand]
    private void ClearLabel(ComboViewModel? combo)
    {
        if (combo is null) return;
        SetLabelOverride(combo.KeyPositionsKey, null);
    }
}
