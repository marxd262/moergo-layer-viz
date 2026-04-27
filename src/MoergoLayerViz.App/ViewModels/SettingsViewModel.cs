using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MoergoLayerViz.App.Localization;
using MoergoLayerViz.Core.Settings;

namespace MoergoLayerViz.App.ViewModels;

/// <summary>
/// View model for the Settings window. Thin shell that forwards property
/// changes to <see cref="MainWindowViewModel"/> — the main VM owns the
/// state and persistence. Bindings here are TwoWay so live tweaks update
/// the board immediately.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly MainWindowViewModel _mainViewModel;
    private bool _disposed;

    public SettingsViewModel(ISettingsService settingsService, MainWindowViewModel mainViewModel)
    {
        _settingsService = settingsService;
        _mainViewModel = mainViewModel;
        _mainViewModel.PropertyChanged += OnMainPropertyChanged;
        _mainViewModel.Layers.CollectionChanged += OnLayersCollectionChanged;
        _mainViewModel.ManualLayerSignalsChanged += RebuildLayerEntries;
        RebuildLayerEntries();
    }

    /// <summary>
    /// Detaches the long-lived <see cref="MainWindowViewModel"/> event hooks
    /// so this VM (and its window's whole DataContext graph) becomes
    /// collectible. Settings reopens build a fresh instance every time, so
    /// without this every closed-then-reopened window leaked a zombie VM
    /// still firing on every property change of the main VM.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mainViewModel.PropertyChanged -= OnMainPropertyChanged;
        _mainViewModel.Layers.CollectionChanged -= OnLayersCollectionChanged;
        _mainViewModel.ManualLayerSignalsChanged -= RebuildLayerEntries;
    }

    public double BackgroundOpacity
    {
        get => _mainViewModel.BackgroundOpacity;
        set => _mainViewModel.BackgroundOpacity = value;
    }

    public string BackgroundOpacityPercent => $"{(int)(BackgroundOpacity * 100)}%";

    /// <summary>
    /// Press-highlight color exposed as Avalonia <see cref="Color"/> so the
    /// <c>ColorView</c> picker can bind directly. Round-trips through the
    /// main VM's hex string (single source of truth + persistence).
    /// </summary>
    public Color PressHighlightColor
    {
        get => Color.TryParse(_mainViewModel.PressHighlightColor, out var c) ? c : Colors.Yellow;
        set => _mainViewModel.PressHighlightColor = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
    }

    /// <summary>Hex string for the swatch preview Border in the picker button.</summary>
    public string PressHighlightColorHex => _mainViewModel.PressHighlightColor;

    /// <summary>Currently-bound global show/hide hotkey (e.g. "F12"). Two-way bound to the main VM, which persists + rewires the live service.</summary>
    public string HotkeyKey
    {
        get => _mainViewModel.HotkeyKey;
        set => _mainViewModel.HotkeyKey = value;
    }

    /// <summary>F-key choices offered for the global hotkey. Same rationale as the layer-key list (F13–F24 first, then F1–F12).</summary>
    public static IReadOnlyList<string> HotkeyKeyChoices { get; } =
        Enumerable.Range(13, 12).Select(n => $"F{n}")
            .Concat(Enumerable.Range(1, 12).Select(n => $"F{n}"))
            .ToArray();

    /// <summary>Top-hand picker choices for stacked layout. Mirrors svalboard's UX.</summary>
    public IReadOnlyList<string> AvailableTopHands { get; } = new[] { "Left", "Right" };

    /// <summary>Whether the stacked layout is enabled. Drives the IsEnabled state of the top-hand picker.</summary>
    public bool IsStackedLayout => _mainViewModel.IsStackedLayout;

    /// <summary>Which half ("Left"/"Right") sits on top in stacked mode. Two-way bound to the main VM, which persists.</summary>
    public string StackedTopHand
    {
        get => _mainViewModel.StackedTopHand;
        set => _mainViewModel.StackedTopHand = value;
    }

    /// <summary>Combined per-layer rows for the Layers tab: each row has a color picker + signal-key picker.</summary>
    public ObservableCollection<LayerSettingsEntry> LayerEntries { get; } = new();

    /// <summary>"Layers — {profile name}" — section heading scoped to active keyboard.</summary>
    [ObservableProperty]
    private string _layersHeader = "";

    /// <summary>F-keys offered to the user as manual signal-keycode candidates. F13–F24 are the host-unused range and lead the list; F1–F12 follow so a user who knows what they're doing can still bind one.</summary>
    private static readonly string[] CandidateSignalKeycodes =
        Enumerable.Range(13, 12).Select(n => $"F{n}")
            .Concat(Enumerable.Range(1, 12).Select(n => $"F{n}"))
            .ToArray();

    /// <summary>True iff a layout is loaded — toggles between the entries list and the placeholder hint.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLayersHint))]
    private bool _hasLayoutForLayers;

    /// <summary>Inverse of <see cref="HasLayoutForLayers"/> for XAML visibility (Avalonia's compiled-binding `!` is finicky in non-trivial templates).</summary>
    public bool ShowsLayersHint => !HasLayoutForLayers;

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.BackgroundOpacity))
        {
            OnPropertyChanged(nameof(BackgroundOpacity));
            OnPropertyChanged(nameof(BackgroundOpacityPercent));
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.PressHighlightColor))
        {
            OnPropertyChanged(nameof(PressHighlightColor));
            OnPropertyChanged(nameof(PressHighlightColorHex));
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.HotkeyKey))
        {
            OnPropertyChanged(nameof(HotkeyKey));
            // Hotkey changes shrink/grow the available F-key pool for layer signals.
            RebuildLayerEntries();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsStackedLayout))
        {
            OnPropertyChanged(nameof(IsStackedLayout));
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.StackedTopHand))
        {
            OnPropertyChanged(nameof(StackedTopHand));
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedKeyboard))
        {
            // Header + per-profile content (color overrides, manual signals)
            // are scoped to the active profile.
            RebuildLayerEntries();
        }
    }

    private void OnLayersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildLayerEntries();
    }

    /// <summary>
    /// Rebuilds the per-layer rows for the Layers tab. Each row composes a
    /// <see cref="LayerColorEntry"/> (color picker + reset) and a
    /// <see cref="LayerSignalEntry"/> (auto-detected label or manual F-key
    /// picker). Cheap to rebuild — layer counts are small, and entries are
    /// recreated rather than reused so the swatch colors stay in sync after
    /// Reset and the signal dropdowns re-derive their "taken" set.
    /// </summary>
    private void RebuildLayerEntries()
    {
        LayerEntries.Clear();
        var profile = _mainViewModel.SelectedKeyboard;
        var profileName = profile?.DisplayName ?? "";
        LayersHeader = string.Format(Loc.Instance["Settings_LayersFormat"], profileName);
        HasLayoutForLayers = _mainViewModel.Layers.Count > 0;
        if (!HasLayoutForLayers || profile is null) return;

        var noneLabel = Loc.Instance["Settings_LayerSignalsNone"];
        var autoFormat = Loc.Instance["Settings_LayerSignalsAutoFormat"];

        // Keycodes already taken by the merged effective table — auto entries
        // plus the active manual entries. We exclude these from any picker
        // except the one that owns it (added back below). The active global
        // hotkey is also excluded so the user can't accidentally bind a layer
        // to the same key that toggles the window.
        var taken = new HashSet<string>(_mainViewModel.EffectiveSignalMappings.Keys, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_mainViewModel.HotkeyKey))
            taken.Add(_mainViewModel.HotkeyKey);

        foreach (var layer in _mainViewModel.Layers)
        {
            var hex = LayerColorPalette.GetColor(profile.Id, layer.Index);
            var color = Color.TryParse(hex, out var c) ? c : Colors.Gray;
            var colorEntry = new LayerColorEntry(
                layer.Index,
                layer.DisplayName,
                color,
                _mainViewModel.SetLayerColorOverride,
                LayerColorPalette.GetDefaultColor);

            var auto = _mainViewModel.GetAutoSignalKeycodeForLayer(layer.Index);
            var manual = _mainViewModel.GetManualSignalKeycodeForLayer(layer.Index);
            var autoText = auto is not null ? string.Format(autoFormat, auto) : "";

            // Build per-row dropdown: "(none)" + every candidate that's free,
            // plus this layer's own current manual selection (so it's
            // selectable and shows the active value even if it's "taken").
            var choices = new List<LayerSignalChoice> { new(noneLabel, null) };
            foreach (var kc in CandidateSignalKeycodes)
            {
                var isMine = manual is not null && string.Equals(kc, manual, StringComparison.OrdinalIgnoreCase);
                if (isMine || !taken.Contains(kc))
                    choices.Add(new LayerSignalChoice(kc, kc));
            }

            var signalEntry = new LayerSignalEntry(
                layer.Index,
                layer.DisplayName,
                auto,
                autoText,
                manual,
                choices,
                _mainViewModel.SetManualLayerSignal);

            LayerEntries.Add(new LayerSettingsEntry(colorEntry, signalEntry));
        }
    }
}
