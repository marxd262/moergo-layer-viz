using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoergoLayerViz.App.Localization;
using MoergoLayerViz.App.Services;
using MoergoLayerViz.Core.Diagnostics;
using MoergoLayerViz.Core.Input;
using MoergoLayerViz.Core.Keymap;
using MoergoLayerViz.Core.Layout;
using MoergoLayerViz.Core.Models;
using MoergoLayerViz.Core.Settings;

namespace MoergoLayerViz.App.ViewModels;

/// <summary>
/// Top-level view model for <c>MainWindow</c>. Owns the loaded keymap, the
/// active layer, the live-key tracker, and persists the user's choice of
/// keyboard + last-loaded JSON path.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;

    private IKeyboardProfile _profile;
    private KeyboardConfig? _config;
    private IReadOnlyList<SignalMacro> _signalMacros = Array.Empty<SignalMacro>();
    private LayerSignalTable _signalTable = new(new Dictionary<string, SignalKeyMapping>());
    // Auto-detected mappings + the user's manual per-layer F-key bindings.
    // The live tracker uses this; the keymap renderer uses _signalTable
    // (auto-only) so manual mappings never affect how labels are drawn.
    private LayerSignalTable _mergedSignalTable = new(new Dictionary<string, SignalKeyMapping>());
    private IReadOnlyList<UntrackableLayerSwitch> _untrackable = Array.Empty<UntrackableLayerSwitch>();
    private string? _loadedLayoutPath;
    private string? _lastLoadError;
    // Last successful load's status text *without* the dynamic untrackable
    // suffix. Kept so we can recompose StatusMessage when the active layer
    // source flips (HID makes the untrackable warning irrelevant). Null when
    // the current StatusMessage is something else (error, transient, etc).
    private string? _loadStatusBase;

    // Reverse adjacency: <layer> → set of layers that can push this layer onto
    // the active stack (via &mo / &lt / &sl / &tog / signal macro). Used to
    // resolve `&trans` fall-through when viewing a layer statically.
    private Dictionary<int, HashSet<int>> _layerPredecessors = new();

    private IKeyEventSource? _keyEventSource;
    private HotkeyLayerTracker? _tracker;
    private LayerSourceCoordinator? _layerCoordinator;
    private string _layerSourceMode = LayerSourceCoordinator.ModeAuto;

    // Active-layer (modifier-set + keycode) → KeyViewModel(s) lookup, rebuilt
    // on every layer change so OnKeyObservedFromHook can flash the right
    // physical key. Key format: "shift+ctrl|N8" — sorted mod categories, then
    // '|', then the base keycode. Modifiers are folded to 4 categories
    // (shift/ctrl/alt/gui) so LS(...) and RS(...) collapse together; this
    // matches the OS, which reports "some shift was held" and doesn't
    // distinguish left/right for resulting characters.
    private Dictionary<string, List<KeyViewModel>> _zmkLookup = new(StringComparer.Ordinal);
    private readonly HashSet<string> _heldModifierCategories = new(StringComparer.Ordinal);
    private readonly Dictionary<KeyViewModel, CancellationTokenSource> _pressCts = new();
    private const int PressHighlightMs = 90;

    // Pending modifier-keypress highlights: when the firmware synthesizes a
    // Shift to produce a shifted symbol (e.g. pressing the `(` key on a
    // symbol layer), the OS sees Shift + N9 in the same tick. If we flashed
    // the modifier key immediately we'd light up the thumb-shift on every
    // shifted symbol — visual noise. Instead, defer a mod-key highlight by
    // ModifierGraceMs; if a non-modifier press arrives inside that window
    // we cancel it (treat it as synthesized). A physical shift sits
    // isolated for 50+ ms before the next keypress, so its highlight fires.
    private readonly List<CancellationTokenSource> _pendingModHighlights = new();
    private const int ModifierGraceMs = 25;

    // --- UI-bindable state ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessageFull))]
    private string _statusMessage = "";

    /// <summary>Tooltip text for the (often-truncated) status bar — full status + keyboard hint joined.</summary>
    public string StatusMessageFull => $"{StatusMessage}  ·  {KeyboardStatusHint}";

    /// <summary>
    /// Suffix appended to the keyboard status hint describing the active layer
    /// source ("via Raw HID (Go60 Left)" / "via signal macros"). Empty until
    /// the coordinator has resolved a source.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyboardStatusHint))]
    [NotifyPropertyChangedFor(nameof(StatusMessageFull))]
    private string _layerSourceHint = "";

    /// <summary>True while the HID source is the active layer source. Used by
    /// the renderer to hide pink "untrackable" overlays since every layer
    /// switch is reported by HID.</summary>
    [ObservableProperty] private bool _isHidSourceActive;
    [ObservableProperty] private bool _isAlwaysOnTop;
    [ObservableProperty] private bool _isLiveHighlightingEnabled;
    [ObservableProperty] private bool _isAutoLayerSwitchEnabled;
    [ObservableProperty] private bool _hasLayoutLoaded;
    [ObservableProperty] private string _toastMessage = "";
    [ObservableProperty] private bool _isToastVisible;

    // Cancels the auto-dismiss timer on a re-shown toast or manual dismiss.
    private CancellationTokenSource? _toastCts;
    private const int ToastDurationMs = 4000;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveLayerTintColor))]
    private int _activeLayerIndex;

    /// <summary>Palette color for the active layer — used as the press-highlight pulse fill.</summary>
    public string ActiveLayerTintColor => LayerColorPalette.GetColor(_profile.Id, ActiveLayerIndex);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoardBackground))]
    [NotifyPropertyChangedFor(nameof(TabBackground))]
    private double _backgroundOpacity;

    // Emitted in CSS-style #RRGGBBAA so HexColorToBrushConverter swaps the
    // alpha to the front for Avalonia's #AARRGGBB. The base #181825 matches
    // svalboard's port — kept identical so a fully-solid slider lands on the
    // same dark plum the original UI used.
    public string BoardBackground => $"#181825{(int)(BackgroundOpacity * 255):X2}";

    // Tabs always retain at least 40% alpha so their text stays readable
    // even at slider 0; svalboard's formula, ported verbatim.
    public string TabBackground
    {
        get
        {
            const int baseAlpha = 0x66;
            var alpha = Math.Min(255, baseAlpha + (int)(BackgroundOpacity * (255 - baseAlpha)));
            return $"#181825{alpha:X2}";
        }
    }

    partial void OnBackgroundOpacityChanged(double value) =>
        PersistSetting(s => s with { BackgroundOpacity = value });

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PressHighlightStrokeColor))]
    private string _pressHighlightColor = "#FFD60A";

    /// <summary>
    /// Rim color for the press dot — darkened version of <see cref="PressHighlightColor"/>
    /// so the dot reads against light layer fills. Multiplies each channel by 0.55
    /// to roughly mimic the original yellow→olive (#FFD60A → #8A6D00) pairing.
    /// </summary>
    public string PressHighlightStrokeColor
    {
        get
        {
            if (TryParseRgb(PressHighlightColor, out var r, out var g, out var b))
                return $"#{(int)(r * 0.55):X2}{(int)(g * 0.55):X2}{(int)(b * 0.55):X2}";
            return "#8A6D00";
        }
    }

    private static bool TryParseRgb(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrEmpty(hex)) return false;
        var s = hex.StartsWith('#') ? hex[1..] : hex;
        if (s.Length < 6) return false;
        return int.TryParse(s.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r)
            && int.TryParse(s.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g)
            && int.TryParse(s.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b);
    }

    partial void OnPressHighlightColorChanged(string value) =>
        PersistSetting(s => s with { PressHighlightColor = value });

    /// <summary>
    /// Global show/hide hotkey keycode (e.g. "F12"). Modifier handling lives
    /// in <see cref="UserSettings.HotkeyModifiers"/> and isn't user-editable
    /// today. Changing this raises <see cref="HotkeyKeyChanged"/> so the live
    /// <c>GlobalHotkeyService</c> rewires without restart, and bumps the
    /// signal-picker rebuild so the new hotkey is excluded from candidates.
    /// </summary>
    [ObservableProperty]
    private string _hotkeyKey = "F12";

    partial void OnHotkeyKeyChanged(string value)
    {
        PersistSetting(s => s with { HotkeyKey = value });
        HotkeyKeyChanged?.Invoke(value);
        // Same channel SettingsViewModel listens to for layer-signal picker rebuilds.
        ManualLayerSignalsChanged?.Invoke();
    }

    public event Action<string>? HotkeyKeyChanged;

    /// <summary>Read-through to the persisted modifier name. Not user-editable today; keeps the hotkey-rewire call site in App self-contained.</summary>
    public string HotkeyModifiers => _settingsService.Load().HotkeyModifiers;

    /// <summary>
    /// Sets or clears a per-layer color override for the currently active
    /// keyboard profile. Persists to <see cref="UserSettings.LayerColors"/>,
    /// updates the static palette, and refreshes both the layer-tab swatches
    /// (in place) and every key fill on the board.
    /// </summary>
    public void SetLayerColorOverride(int layerIndex, string? hex)
    {
        var profileId = _profile.Id;
        LayerColorPalette.SetOverride(profileId, layerIndex, hex);

        PersistSetting(s =>
        {
            var clone = new Dictionary<string, Dictionary<int, string>>();
            foreach (var (pid, perLayer) in s.LayerColors)
                clone[pid] = new Dictionary<int, string>(perLayer);

            if (string.IsNullOrWhiteSpace(hex))
            {
                if (clone.TryGetValue(profileId, out var inner))
                {
                    inner.Remove(layerIndex);
                    if (inner.Count == 0) clone.Remove(profileId);
                }
            }
            else
            {
                if (!clone.TryGetValue(profileId, out var inner))
                    clone[profileId] = inner = new Dictionary<int, string>();
                inner[layerIndex] = hex!;
            }
            return s with { LayerColors = clone };
        });

        // Repaint tab swatches in place (re-creating Layers would steal selection focus).
        foreach (var layer in Layers)
            layer.TabColor = LayerColorPalette.GetColor(profileId, layer.Index);
        // Re-resolve every key's fill — &lt / &mo / signal-macro keys reference
        // arbitrary layer colors, so changing layer 2's tint repaints layer 0's view too.
        if (_config is not null)
            ApplyActiveLayer(ActiveLayerIndex);
        OnPropertyChanged(nameof(ActiveLayerTintColor));
    }

    /// <summary>
    /// First auto-detected signal keycode that activates <paramref name="layerIndex"/>,
    /// or null if no signal macro maps to that layer. Used by Settings to label
    /// which layers are already covered by auto-tracking.
    /// </summary>
    public string? GetAutoSignalKeycodeForLayer(int layerIndex)
    {
        foreach (var (kc, m) in _signalTable.Mappings)
            if (m.TargetLayer == layerIndex) return kc;
        return null;
    }

    /// <summary>User's manual signal keycode for the given layer on the current profile, or null.</summary>
    public string? GetManualSignalKeycodeForLayer(int layerIndex)
    {
        var s = _settingsService.Load();
        return s.ManualLayerSignals.TryGetValue(_profile.Id, out var perLayer)
               && perLayer.TryGetValue(layerIndex, out var kc)
            ? kc
            : null;
    }

    /// <summary>
    /// All signal keycodes the live tracker currently considers — auto plus
    /// active manual mappings. Diagnostic surface for the Settings list to
    /// compute "which F-keys are still free".
    /// </summary>
    public IReadOnlyDictionary<string, SignalKeyMapping> EffectiveSignalMappings => _mergedSignalTable.Mappings;

    /// <summary>
    /// Sets or clears the user's manual signal-keycode binding for the given
    /// layer on the current profile. Auto-detected mappings always win, so
    /// this is a no-op (silently persisted but ineffective) for layers that
    /// already have an auto-detected signal macro. Persists, rebuilds the
    /// merged signal table, and pushes it into the live tracker so the change
    /// takes effect without restart.
    /// </summary>
    public void SetManualLayerSignal(int layerIndex, string? keycode)
    {
        var profileId = _profile.Id;
        PersistSetting(s =>
        {
            var clone = new Dictionary<string, Dictionary<int, string>>();
            foreach (var (pid, perLayer) in s.ManualLayerSignals)
                clone[pid] = new Dictionary<int, string>(perLayer);

            if (string.IsNullOrWhiteSpace(keycode))
            {
                if (clone.TryGetValue(profileId, out var inner))
                {
                    inner.Remove(layerIndex);
                    if (inner.Count == 0) clone.Remove(profileId);
                }
            }
            else
            {
                if (!clone.TryGetValue(profileId, out var inner))
                    clone[profileId] = inner = new Dictionary<int, string>();
                inner[layerIndex] = keycode!;
            }
            return s with { ManualLayerSignals = clone };
        });

        RebuildAndApplyMergedSignalTable();
    }

    /// <summary>
    /// Recomputes <see cref="_mergedSignalTable"/> from the auto-detected
    /// <see cref="_signalTable"/> plus the user's persisted manual mappings
    /// for the active profile, and pushes the result into the live tracker.
    /// </summary>
    private void RebuildAndApplyMergedSignalTable()
    {
        var merged = new Dictionary<string, SignalKeyMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kc, m) in _signalTable.Mappings) merged[kc] = m;

        // Layers already covered by auto-detection — manual entries for these
        // are silently ignored (auto wins, per the UX rule "when autoswitch
        // works, user can not override").
        var autoLayers = new HashSet<int>(_signalTable.Mappings.Values.Select(m => m.TargetLayer));

        var s = _settingsService.Load();
        if (s.ManualLayerSignals.TryGetValue(_profile.Id, out var perLayer))
        {
            foreach (var (layerIdx, keycode) in perLayer)
            {
                if (autoLayers.Contains(layerIdx)) continue;
                if (string.IsNullOrWhiteSpace(keycode)) continue;
                if (merged.ContainsKey(keycode)) continue;  // first writer wins for keycode dedup
                // Manual mappings target layers reached via &to/&tog which have
                // no release event, so toggle-on-press semantics fit better
                // than momentary hold.
                merged[keycode] = new SignalKeyMapping(keycode, layerIdx, IsMomentary: false, "manual");
            }
        }

        _mergedSignalTable = new LayerSignalTable(merged);
        _tracker?.UpdateTable(_mergedSignalTable);
        ManualLayerSignalsChanged?.Invoke();
    }

    /// <summary>
    /// Raised after the merged signal table is rebuilt. SettingsViewModel
    /// listens to refresh the per-layer picker rows when (a) a layout loads,
    /// (b) the keyboard profile changes, or (c) the user toggles a manual
    /// binding (which can free or claim an F-key for other layers).
    /// </summary>
    public event Action? ManualLayerSignalsChanged;

    public ObservableCollection<KeyViewModel> Keys { get; } = new();
    /// <summary>Left-hand subset of <see cref="Keys"/>. Bound separately so the stacked-layout renderer can translate the half independently.</summary>
    public ObservableCollection<KeyViewModel> LeftKeys { get; } = new();
    /// <summary>Right-hand subset of <see cref="Keys"/>. Bound separately so the stacked-layout renderer can translate the half independently.</summary>
    public ObservableCollection<KeyViewModel> RightKeys { get; } = new();
    public ObservableCollection<LayerViewModel> Layers { get; } = new();

    // Per-profile bounding boxes for each hand, recomputed on profile change.
    // Used to translate each half's container in stacked mode so the bounding
    // box starts at the canvas-edge margin.
    private (double MinX, double MinY, double MaxX, double MaxY) _leftBounds;
    private (double MinX, double MinY, double MaxX, double MaxY) _rightBounds;

    /// <summary>Margin around the bounding boxes in stacked mode.</summary>
    private const double StackedMargin = 30;
    /// <summary>Vertical gap between the two halves in stacked mode.</summary>
    private const double StackedGap = 60;

    /// <summary>When true, the two halves render stacked vertically instead of side-by-side. Persisted across launches.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanvasWidth))]
    [NotifyPropertyChangedFor(nameof(CanvasHeight))]
    [NotifyPropertyChangedFor(nameof(LeftHandX))]
    [NotifyPropertyChangedFor(nameof(LeftHandY))]
    [NotifyPropertyChangedFor(nameof(RightHandX))]
    [NotifyPropertyChangedFor(nameof(RightHandY))]
    private bool _isStackedLayout;

    partial void OnIsStackedLayoutChanged(bool value) =>
        PersistSetting(s => s with { StackedLayout = value });

    /// <summary>Which half ("Left"/"Right") sits on top in stacked mode. Ignored in horizontal mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftHandX))]
    [NotifyPropertyChangedFor(nameof(LeftHandY))]
    [NotifyPropertyChangedFor(nameof(RightHandX))]
    [NotifyPropertyChangedFor(nameof(RightHandY))]
    private string _stackedTopHand = "Left";

    partial void OnStackedTopHandChanged(string value) =>
        PersistSetting(s => s with { StackedTopHand = value });

    /// <summary>Canvas size for the current keyboard profile and layout mode (drives BoardView's Canvas width/height).</summary>
    public double CanvasWidth => IsStackedLayout
        ? Math.Max(_leftBounds.MaxX - _leftBounds.MinX, _rightBounds.MaxX - _rightBounds.MinX) + 2 * StackedMargin
        : _profile.CanvasWidth;

    public double CanvasHeight => IsStackedLayout
        ? (_leftBounds.MaxY - _leftBounds.MinY) + (_rightBounds.MaxY - _rightBounds.MinY) + StackedGap + 2 * StackedMargin
        : _profile.CanvasHeight;

    /// <summary>
    /// Per-hand drawing surface size, independent of layout mode. Each per-hand
    /// ItemsControl in BoardView binds Width/Height to these so it has a
    /// non-zero render box — keys position themselves absolutely within it
    /// using the original profile coordinates, and the surrounding Canvas.Left/
    /// Top translates the whole surface for stacked mode.
    /// </summary>
    public double BoardSurfaceWidth => _profile.CanvasWidth;
    public double BoardSurfaceHeight => _profile.CanvasHeight;

    /// <summary>X translation applied to the left-hand container.</summary>
    public double LeftHandX => IsStackedLayout ? StackedMargin - _leftBounds.MinX : 0;

    /// <summary>Y translation applied to the left-hand container. Goes below the right hand if "Right" is on top.</summary>
    public double LeftHandY => !IsStackedLayout
        ? 0
        : (StackedTopHand == "Right"
            ? StackedMargin + (_rightBounds.MaxY - _rightBounds.MinY) + StackedGap - _leftBounds.MinY
            : StackedMargin - _leftBounds.MinY);

    /// <summary>X translation applied to the right-hand container.</summary>
    public double RightHandX => IsStackedLayout ? StackedMargin - _rightBounds.MinX : 0;

    /// <summary>Y translation applied to the right-hand container. Goes below the left hand by default.</summary>
    public double RightHandY => !IsStackedLayout
        ? 0
        : (StackedTopHand == "Right"
            ? StackedMargin - _rightBounds.MinY
            : StackedMargin + (_leftBounds.MaxY - _leftBounds.MinY) + StackedGap - _rightBounds.MinY);

    [RelayCommand]
    private void ToggleStackedLayout() => IsStackedLayout = !IsStackedLayout;

    /// <summary>All keyboard profiles the user can switch between, for the picker flyout.</summary>
    public IReadOnlyList<IKeyboardProfile> AvailableKeyboards => KeyboardProfileRegistry.All;

    /// <summary>
    /// Currently selected profile. Bound to the picker button label and drives
    /// checkmark selection in the flyout.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyboardStatusHint))]
    [NotifyPropertyChangedFor(nameof(StatusMessageFull))]
    [NotifyPropertyChangedFor(nameof(ActiveLayerTintColor))]
    private IKeyboardProfile _selectedKeyboard = null!;

    /// <summary>Right-aligned status-bar hint: "DisplayName · N keys" with layer-source suffix when known.</summary>
    public string KeyboardStatusHint
    {
        get
        {
            var core = Loc.Instance.Format("Status_KeyboardHintFormat",
                SelectedKeyboard?.DisplayName ?? "", SelectedKeyboard?.KeyCount ?? 0);
            return string.IsNullOrEmpty(LayerSourceHint) ? core : $"{core}  ·  {LayerSourceHint}";
        }
    }

    // --- Callbacks set by App.axaml.cs to bridge to the Window ---
    public Action? QuitRequested { get; set; }
    public Action? ShowWindowRequested { get; set; }
    public Action? ToggleWindowRequested { get; set; }
    public Func<Task>? LoadLayoutRequested { get; set; }
    public Func<Task>? CopyDiagnosticsRequested { get; set; }
    public Action? ShowAccessibilityPromptRequested { get; set; }
    public Action? OpenSettingsRequested { get; set; }
    public Action? OpenGenerateSignalsRequested { get; set; }

    /// <summary>Path of the layout JSON the user currently has loaded, or null if none.</summary>
    public string? LoadedLayoutPath => _loadedLayoutPath;

    private bool _accessibilityDialogShown;

    // --- Commands ---
    public IRelayCommand QuitCommand { get; }
    public IRelayCommand ShowCommand { get; }
    public IRelayCommand LoadLayoutCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand TogglePinCommand { get; }
    public IRelayCommand ToggleLiveHighlightingCommand { get; }
    public IRelayCommand ToggleAutoLayerSwitchCommand { get; }
    public IRelayCommand OpenLogFolderCommand { get; }
    public IRelayCommand CopyDiagnosticsCommand { get; }
    public IRelayCommand<IKeyboardProfile> SelectKeyboardCommand { get; }
    public IRelayCommand DismissToastCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IRelayCommand OpenGenerateSignalsCommand { get; }

    private readonly SharpHookProvider? _hookProvider;

    public MainWindowViewModel(ISettingsService settingsService, SharpHookProvider? hookProvider = null)
    {
        _settingsService = settingsService;
        _hookProvider = hookProvider;
        var s = settingsService.Load();
        _profile = KeyboardProfileRegistry.TryResolve(s.Keyboard, out var p) ? p : new Go60Profile();
        _selectedKeyboard = _profile;
        _isAlwaysOnTop = s.AlwaysOnTop;
        _isLiveHighlightingEnabled = s.LiveKeyHighlighting;
        _isAutoLayerSwitchEnabled = s.AutoLayerSwitch;
        _backgroundOpacity = Math.Clamp(s.BackgroundOpacity, 0.0, 1.0);
        if (!string.IsNullOrWhiteSpace(s.PressHighlightColor))
            _pressHighlightColor = s.PressHighlightColor;
        if (!string.IsNullOrWhiteSpace(s.HotkeyKey))
            _hotkeyKey = s.HotkeyKey;
        _isStackedLayout = s.StackedLayout;
        _stackedTopHand = string.IsNullOrWhiteSpace(s.StackedTopHand) ? "Left" : s.StackedTopHand;
        _layerSourceMode = string.IsNullOrWhiteSpace(s.LayerSource) ? LayerSourceCoordinator.ModeAuto : s.LayerSource;
        // Seed the static palette with persisted per-keyboard, per-layer overrides
        // so the very first paint already reflects the user's customization.
        LayerColorPalette.SetOverrides(s.LayerColors);

        QuitCommand = new RelayCommand(() => QuitRequested?.Invoke());
        ShowCommand = new RelayCommand(() => ShowWindowRequested?.Invoke());
        LoadLayoutCommand = new AsyncRelayCommand(async () =>
        {
            if (LoadLayoutRequested is not null) await LoadLayoutRequested();
        });
        RefreshCommand = new RelayCommand(() =>
        {
            var paths = _settingsService.Load().LayoutJsonPaths;
            if (paths.TryGetValue(_profile.Id, out var path) && !string.IsNullOrEmpty(path))
                LoadLayoutFromPath(path);
        });
        TogglePinCommand = new RelayCommand(() =>
        {
            IsAlwaysOnTop = !IsAlwaysOnTop;
            PersistSetting(s2 => s2 with { AlwaysOnTop = IsAlwaysOnTop });
        });
        ToggleLiveHighlightingCommand = new RelayCommand(ToggleLiveHighlighting);
        ToggleAutoLayerSwitchCommand = new RelayCommand(() =>
        {
            IsAutoLayerSwitchEnabled = !IsAutoLayerSwitchEnabled;
            PersistSetting(s2 => s2 with { AutoLayerSwitch = IsAutoLayerSwitchEnabled });
            if (!IsAutoLayerSwitchEnabled)
                ResetLayerState();
        });
        OpenLogFolderCommand = new RelayCommand(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = DiagnosticLog.GetLogDirectory(),
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                _loadStatusBase = null;
                StatusMessage = $"Could not open log folder: {ex.Message}";
            }
        });
        CopyDiagnosticsCommand = new AsyncRelayCommand(async () =>
        {
            if (CopyDiagnosticsRequested is not null) await CopyDiagnosticsRequested();
        });
        SelectKeyboardCommand = new RelayCommand<IKeyboardProfile>(SelectKeyboard);
        DismissToastCommand = new RelayCommand(DismissToast);
        OpenSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke());
        OpenGenerateSignalsCommand = new RelayCommand(() => OpenGenerateSignalsRequested?.Invoke());

        BuildKeysFromProfile();
    }

    /// <summary>
    /// Post-startup entry point: load last-used layout, spin up the key-event
    /// hook, and set an opening status message.
    /// </summary>
    public void InitializeAsync()
    {
        var s = _settingsService.Load();
        if (TryGetStoredPathForProfile(s, _profile.Id, out var storedPath))
        {
            LoadLayoutFromPath(storedPath);
        }
        else
        {
            _loadStatusBase = null;
            StatusMessage = Loc.Instance["Status_NoLayoutLoaded"];
        }

        // Live tracking is no longer Linux-blocked: the HID source works
        // without any global hook, and StartKeyEventTracking() internally
        // skips SharpHook when _hookProvider is null (which it is on Linux).
        if (IsLiveHighlightingEnabled)
            StartKeyEventTracking();
    }

    public void LoadLayoutFromPath(string path)
    {
        try
        {
            var config = MoergoJsonLoader.LoadFromFile(path);
            var bindingCount = config.LayerCount > 0 ? config.Layers[0].Bindings.Count : 0;
            var keyCountMismatch = config.LayerCount > 0 && bindingCount != _profile.KeyCount;
            IKeyboardProfile? autoSwitchedTo = null;
            IKeyboardProfile? unmatchedProfile = null;
            if (keyCountMismatch)
            {
                var matching = KeyboardProfileRegistry.All
                    .FirstOrDefault(p => p.KeyCount == bindingCount);
                if (matching is not null)
                {
                    unmatchedProfile = _profile;
                    _profile = matching;
                    SelectedKeyboard = matching;
                    BuildKeysFromProfile();
                    PersistSetting(s => s with { Keyboard = matching.Id });
                    // Re-scope HID discovery — see SelectKeyboard for context.
                    _layerCoordinator?.SetActiveProfile(matching);
                    autoSwitchedTo = matching;
                    DiagnosticLog.Info("MainVM",
                        $"Auto-switched profile to {matching.Id} ({bindingCount} keys) on load");
                }
                else
                {
                    DiagnosticLog.Warn("MainVM",
                        $"Loaded layout has {bindingCount} keys but no profile matches; staying on {_profile.Id}");
                }
            }

            _config = config;
            _signalMacros = SignalMacroScanner.DetectSignalMacros(config);
            _signalTable = LayerSignalTable.Build(config, _signalMacros);
            _untrackable = SignalMacroScanner.FindUntrackableLayerSwitches(config, _signalMacros);
            _layerPredecessors = BuildLayerPredecessors(config, _signalMacros);

            RebuildAndApplyMergedSignalTable();

            RebuildLayers();
            ApplyActiveLayer(0);
            HasLayoutLoaded = true;
            _loadedLayoutPath = path;
            _lastLoadError = null;

            PersistSetting(s =>
            {
                var paths = new Dictionary<string, string>(s.LayoutJsonPaths) { [_profile.Id] = path };
                return s with { LayoutJsonPaths = paths };
            });

            var baseMsg = Loc.Instance.Format("Status_Loaded",
                Path.GetFileName(path), config.LayerCount);
            if (autoSwitchedTo is not null)
            {
                baseMsg += " — " + Loc.Instance.Format("Status_AutoSwitchedKeyboard",
                    autoSwitchedTo.DisplayName);
            }
            else if (keyCountMismatch)
            {
                baseMsg += " — " + Loc.Instance.Format("Status_LoadKeyCountMismatch",
                    bindingCount, _profile.DisplayName, _profile.KeyCount);
            }
            _loadStatusBase = baseMsg;
            StatusMessage = ComposeLoadStatus();
            DiagnosticLog.Info("MainVM",
                $"Loaded '{path}' signalMacros={_signalMacros.Count} untrackable={_untrackable.Count}");
        }
        catch (Exception ex)
        {
            _loadStatusBase = null;
            StatusMessage = Loc.Instance.Format("Status_LoadErrorFormat", ex.Message);
            _lastLoadError = $"{path}: {ex.GetType().Name}: {ex.Message}";
            DiagnosticLog.Error("MainVM", $"Load failed: {ex}");
            ShowToast(Loc.Instance.Format("Toast_LoadFailedFormat", Path.GetFileName(path), ex.Message));
        }
    }

    /// <summary>
    /// Shows a transient toast banner that auto-dismisses after
    /// <see cref="ToastDurationMs"/>. Re-entry cancels the previous timer so
    /// the new message gets the full display window. Click on the toast
    /// dismisses early via <see cref="DismissToastCommand"/>.
    /// </summary>
    public void ShowToast(string message)
    {
        _toastCts?.Cancel();
        _toastCts?.Dispose();
        var cts = new CancellationTokenSource();
        _toastCts = cts;

        ToastMessage = message;
        IsToastVisible = true;

        _ = Task.Delay(ToastDurationMs, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_toastCts == cts)
                {
                    IsToastVisible = false;
                    _toastCts = null;
                    cts.Dispose();
                }
            });
        }, TaskScheduler.Default);
    }

    private void DismissToast()
    {
        _toastCts?.Cancel();
        _toastCts?.Dispose();
        _toastCts = null;
        IsToastVisible = false;
    }

    /// <summary>
    /// Builds a snapshot of runtime state (active settings, loaded layout,
    /// signal-macro count, untrackable layer-switch list, last load error)
    /// for inclusion in <see cref="DiagnosticLog.CollectDiagnosticReport"/>.
    /// </summary>
    public string BuildDiagnosticsSnapshot()
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- Active Settings ---");
        try
        {
            var settings = _settingsService.Load();
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            sb.AppendLine(json);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(could not serialize settings: {ex.Message})");
        }
        sb.AppendLine();

        sb.AppendLine("--- Active Keyboard ---");
        sb.AppendLine($"Profile: {_profile.DisplayName} ({_profile.Id}), {_profile.KeyCount} keys");
        sb.AppendLine($"Loaded layout: {_loadedLayoutPath ?? "(none)"}");
        sb.AppendLine($"Last load error: {_lastLoadError ?? "(none)"}");
        sb.AppendLine($"Signal macros detected: {_signalMacros.Count}");
        sb.AppendLine($"Untrackable layer switches: {_untrackable.Count}");
        if (_untrackable.Count > 0)
        {
            foreach (var u in _untrackable)
            {
                var target = u.TargetLayer is int t ? t.ToString() : "?";
                sb.AppendLine($"  (layer {u.LayerIndex}, key index {u.KeyIndex}) {u.Behavior} {target}");
            }
        }

        return sb.ToString();
    }

    /// <summary>Gracefully stops live tracking; idempotent. Called on window close / quit.</summary>
    public void Shutdown()
    {
        StopKeyEventTracking();
    }

    // --- Internals ---

    private void BuildKeysFromProfile()
    {
        Keys.Clear();
        LeftKeys.Clear();
        RightKeys.Clear();

        double lMinX = double.PositiveInfinity, lMinY = double.PositiveInfinity;
        double lMaxX = double.NegativeInfinity, lMaxY = double.NegativeInfinity;
        double rMinX = double.PositiveInfinity, rMinY = double.PositiveInfinity;
        double rMaxX = double.NegativeInfinity, rMaxY = double.NegativeInfinity;

        foreach (var pos in _profile.Keys)
        {
            var vm = new KeyViewModel(pos);
            Keys.Add(vm);
            if (pos.Hand == Hand.Left)
            {
                LeftKeys.Add(vm);
                if (pos.X < lMinX) lMinX = pos.X;
                if (pos.Y < lMinY) lMinY = pos.Y;
                if (pos.X + pos.Width > lMaxX) lMaxX = pos.X + pos.Width;
                if (pos.Y + pos.Height > lMaxY) lMaxY = pos.Y + pos.Height;
            }
            else
            {
                RightKeys.Add(vm);
                if (pos.X < rMinX) rMinX = pos.X;
                if (pos.Y < rMinY) rMinY = pos.Y;
                if (pos.X + pos.Width > rMaxX) rMaxX = pos.X + pos.Width;
                if (pos.Y + pos.Height > rMaxY) rMaxY = pos.Y + pos.Height;
            }
        }

        _leftBounds = LeftKeys.Count > 0 ? (lMinX, lMinY, lMaxX, lMaxY) : (0, 0, 0, 0);
        _rightBounds = RightKeys.Count > 0 ? (rMinX, rMinY, rMaxX, rMaxY) : (0, 0, 0, 0);

        // Layout-derived properties depend on the freshly-computed bounds.
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(BoardSurfaceWidth));
        OnPropertyChanged(nameof(BoardSurfaceHeight));
        OnPropertyChanged(nameof(LeftHandX));
        OnPropertyChanged(nameof(LeftHandY));
        OnPropertyChanged(nameof(RightHandX));
        OnPropertyChanged(nameof(RightHandY));
    }

    private void SelectKeyboard(IKeyboardProfile? profile)
    {
        if (profile is null) return;
        if (string.Equals(profile.Id, _profile.Id, StringComparison.OrdinalIgnoreCase)) return;

        _profile = profile;
        SelectedKeyboard = profile;
        BuildKeysFromProfile();

        var layoutFits = _config is not null
            && _config.LayerCount > 0
            && _config.Layers[0].Bindings.Count == profile.KeyCount;

        if (_config is not null && !layoutFits)
        {
            _config = null;
            _signalMacros = Array.Empty<SignalMacro>();
            _signalTable = new LayerSignalTable(new Dictionary<string, SignalKeyMapping>());
            _untrackable = Array.Empty<UntrackableLayerSwitch>();
            _layerPredecessors = new Dictionary<int, HashSet<int>>();
            RebuildAndApplyMergedSignalTable();
            Layers.Clear();
            ActiveLayerIndex = 0;
            HasLayoutLoaded = false;
            _loadStatusBase = null;
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitchedUnloaded", profile.DisplayName);
        }
        else if (_config is not null)
        {
            RebuildAndApplyMergedSignalTable();
            ApplyActiveLayer(ActiveLayerIndex);
            _loadStatusBase = null;
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitched", profile.DisplayName);
        }
        else
        {
            _loadStatusBase = null;
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitched", profile.DisplayName);
        }

        PersistSetting(s => s with { Keyboard = profile.Id });
        DiagnosticLog.Info("MainVM", $"Keyboard profile switched to {profile.Id}");

        // Re-scope HID discovery to the new profile so a Go60 stops feeding
        // reports into a Glove80 layout (or vice versa). No-op when HID is
        // disabled or the source isn't running.
        _layerCoordinator?.SetActiveProfile(profile);

        // Auto-load whichever JSON the user last associated with this keyboard.
        // If the previously-loaded layout already fits, leave it alone.
        if (!HasLayoutLoaded
            && TryGetStoredPathForProfile(_settingsService.Load(), profile.Id, out var storedPath))
        {
            LoadLayoutFromPath(storedPath);
        }
    }

    /// <summary>
    /// Returns the persisted JSON path for the given profile if one exists and
    /// the file is still on disk. If the entry points at a file that has gone
    /// missing, logs a warning and removes the stale entry from settings so
    /// startup doesn't keep complaining about it.
    /// </summary>
    private bool TryGetStoredPathForProfile(UserSettings s, string profileId, out string path)
    {
        path = "";
        if (!s.LayoutJsonPaths.TryGetValue(profileId, out var stored) || string.IsNullOrWhiteSpace(stored))
            return false;
        if (File.Exists(stored))
        {
            path = stored;
            return true;
        }

        DiagnosticLog.Warn("MainVM", $"Stored layout for {profileId} is missing on disk: {stored}");
        PersistSetting(curr =>
        {
            var paths = new Dictionary<string, string>(curr.LayoutJsonPaths);
            paths.Remove(profileId);
            return curr with { LayoutJsonPaths = paths };
        });
        return false;
    }

    private void RebuildLayers()
    {
        Layers.Clear();
        if (_config is null) return;
        foreach (var layer in _config.Layers)
        {
            Layers.Add(new LayerViewModel(
                layer.Index,
                $"{layer.Index} : {layer.Name}",
                LayerColorPalette.GetColor(_profile.Id, layer.Index),
                SelectLayer));
        }
    }

    private void SelectLayer(int index) => ApplyActiveLayer(index);

    private void ApplyActiveLayer(int index)
    {
        if (_config is null) return;
        if (index < 0 || index >= _config.Layers.Count) return;

        ActiveLayerIndex = index;
        var layer = _config.Layers[index];

        // Includes hold-tap names whose hold side is a signal macro, so an
        // &ht_* layer binding resolves to its underlying SignalMacro.
        var signalByName = LayerSignalTable.BuildSignalLookup(_config, _signalMacros);
        var holdTapByName = _config.HoldTaps.ToDictionary(h => h.Name, StringComparer.Ordinal);
        var untrackableSet = new HashSet<(int layer, int key)>(_untrackable.Select(u => (u.LayerIndex, u.KeyIndex)));

        var combosByKey = new Dictionary<int, List<MoergoCombo>>();
        foreach (var combo in _config.Combos)
        {
            if (!combo.AppliesToLayer(layer.Index)) continue;
            foreach (var keyIdx in combo.KeyPositions)
            {
                if (!combosByKey.TryGetValue(keyIdx, out var list))
                    combosByKey[keyIdx] = list = new List<MoergoCombo>();
                list.Add(combo);
            }
        }

        for (int i = 0; i < Keys.Count; i++)
        {
            // Resolve the effective binding by walking the predecessor graph:
            // `&trans` falls through to the layer that can activate this one
            // (recursively) until a non-transparent binding is found.
            var binding = ResolveEffectiveBinding(layer.Index, i);

            var isSignal = signalByName.TryGetValue(binding.Behavior, out var signalMacro);
            holdTapByName.TryGetValue(binding.Behavior, out var holdTap);
            var targetLayer = ResolveTargetLayer(binding, isSignal ? signalMacro : null, holdTap);
            var targetLayerName = targetLayer is int tl && tl >= 0 && tl < _config.Layers.Count
                ? _config.Layers[tl].Name
                : null;
            Keys[i].ApplyBinding(
                binding,
                isSignalMacro: isSignal,
                // HID source reports every layer change directly, so the
                // pink "untrackable" warning is meaningless when it's active.
                isUntrackable: !IsHidSourceActive && untrackableSet.Contains((layer.Index, i)),
                targetLayer: targetLayer,
                targetLayerName: targetLayerName,
                profileId: _profile.Id,
                holdTap: holdTap,
                signal: isSignal ? signalMacro : null);
        }

        // Second pass — every key's label is now settled, so combo participants
        // can be named by their rendered label (Q + W) in the tooltip.
        string LabelLookup(int idx) => idx >= 0 && idx < Keys.Count ? Keys[idx].Label : "";
        for (int i = 0; i < Keys.Count; i++)
        {
            if (combosByKey.TryGetValue(i, out var keyCombos))
                Keys[i].SetCombos(keyCombos, LabelLookup);
        }

        for (int i = 0; i < Layers.Count; i++)
            Layers[i].IsSelected = Layers[i].Index == index;

        RebuildZmkLookup(signalByName);
    }

    /// <summary>
    /// Rebuilds the "which physical key emits which OS keycode on the
    /// currently displayed layer" map. Keyed off <see cref="ActiveLayerIndex"/>
    /// — the user wants the highlight to match what they're looking at.
    /// If an OS keycode isn't present on the displayed layer, it's a miss
    /// (no visible key to light up).
    /// </summary>
    private void RebuildZmkLookup(Dictionary<string, SignalMacro> signalByName)
    {
        // Build into a local then publish via a single reference assignment.
        // OnKeyObservedFromHook reads _zmkLookup from the hook thread; if we
        // populated the field in place, hook callbacks landing mid-rebuild
        // would observe an empty / half-built dict and miss highlights.
        var next = new Dictionary<string, List<KeyViewModel>>(StringComparer.Ordinal);
        if (_config is null)
        {
            _zmkLookup = next;
            return;
        }
        var layerIdx = ActiveLayerIndex;
        if (layerIdx < 0 || layerIdx >= _config.Layers.Count) layerIdx = 0;
        for (int i = 0; i < Keys.Count; i++)
        {
            var binding = ResolveEffectiveBinding(layerIdx, i);
            var signal = signalByName.TryGetValue(binding.Behavior, out var s) ? s : null;
            var press = ExtractEmittedKeypress(binding, signal);
            if (press is null) continue;
            var key = BuildLookupKey(press.Value.Mods, press.Value.Code);
            if (!next.TryGetValue(key, out var list))
                next[key] = list = new List<KeyViewModel>();
            list.Add(Keys[i]);
        }
        _zmkLookup = next;
        DiagnosticLog.Debug("Highlight", $"lookup rebuilt layer={layerIdx} keys=[{string.Join(",", next.Keys)}]");
    }

    /// <summary>
    /// Returns the (modifier-set, base-keycode) pair a binding emits on press,
    /// or null if the binding does not surface a keycode to the host.
    /// After <see cref="MoergoJsonLoader.FlattenParams"/>, modifier wrappers
    /// appear as flat prefix tokens (LS, LC, ...) before the innermost key at
    /// the last position. We also fold in the implicit Shift that ZMK's
    /// shifted-symbol aliases carry (LPAR, STAR, ...).
    /// </summary>
    private static (HashSet<string> Mods, string Code)? ExtractEmittedKeypress(KeyBinding b, SignalMacro? signal)
    {
        // Which prefix of b.Params counts as the "keycode slot". Signal
        // macros, &lt and &HRM_* prepend their own non-keycode params (layer
        // id, hold-modifier) which are NOT wrappers and must not be folded
        // into the mod set.
        int startIndex;
        if (signal is not null
            && signal.KeyParamIndex is int keyParamIdx
            && b.Params.Count > keyParamIdx)
        {
            // Routed signal macro — its keycode lives at this slot.
            startIndex = keyParamIdx;
        }
        else switch (b.Behavior)
        {
            case "&kp" when b.Params.Count >= 1: startIndex = 0; break;
            case "&lt" when b.Params.Count >= 2: startIndex = 1; break;
            default:
                if (b.Behavior.StartsWith("&HRM_", StringComparison.Ordinal) && b.Params.Count >= 2)
                    startIndex = 1;
                else
                    return null;
                break;
        }

        // Wrapper modifiers are every flat param in [startIndex .. count-2];
        // the last param is the innermost keycode.
        var mods = new HashSet<string>(StringComparer.Ordinal);
        for (int i = startIndex; i < b.Params.Count - 1; i++)
        {
            var cat = CategoryForWrapperPrefix(b.Params[i]);
            if (cat is not null) mods.Add(cat);
        }
        var (extraMods, code) = CanonicalizeKeycode(b.Params[^1]);
        foreach (var m in extraMods) mods.Add(m);
        return (mods, code);
    }

    /// <summary>
    /// Canonicalizes a ZMK keycode token, returning any modifiers the token
    /// itself implicitly carries (shifted-symbol aliases → Shift).
    /// </summary>
    private static (IEnumerable<string> Mods, string Code) CanonicalizeKeycode(string raw)
    {
        var s = raw.Trim();
        if (ZmkShiftedSymbols.TryGetValue(s, out var shiftedBase))
            return (new[] { "shift" }, shiftedBase);
        if (ZmkPlainAliases.TryGetValue(s, out var plain))
            return (Array.Empty<string>(), plain);
        return (Array.Empty<string>(), s);
    }

    /// <summary>
    /// Maps ZMK long-form aliases to the short canonical form that
    /// <see cref="SharpHookKeyEventSource"/> emits. Keymap JSON can carry
    /// either form (EQUALS vs EQUAL, LEFT_SHIFT vs LSHFT) depending on
    /// the Moergo editor's output. These aliases do not change the set of
    /// modifiers that will be emitted — they're pure rename aliases.
    /// </summary>
    private static readonly Dictionary<string, string> ZmkPlainAliases = new(StringComparer.Ordinal)
    {
        // Long-form modifier aliases
        ["LEFT_SHIFT"] = "LSHFT",       ["RIGHT_SHIFT"] = "RSHFT",
        ["LEFT_CONTROL"] = "LCTRL",     ["RIGHT_CONTROL"] = "RCTRL",
        ["LEFT_ALT"] = "LALT",          ["RIGHT_ALT"] = "RALT",
        ["LEFT_GUI"] = "LGUI",          ["RIGHT_GUI"] = "RGUI",
        ["LEFT_COMMAND"] = "LGUI",      ["RIGHT_COMMAND"] = "RGUI",
        ["LEFT_WIN"] = "LGUI",          ["RIGHT_WIN"] = "RGUI",
        ["LEFT_META"] = "LGUI",         ["RIGHT_META"] = "RGUI",

        // Punctuation long-form
        ["EQUALS"] = "EQUAL",
        ["SLASH"] = "FSLH",             ["FORWARD_SLASH"] = "FSLH",
        ["BACKSLASH"] = "BSLH",
        ["SEMICOLON"] = "SEMI",
        ["SINGLE_QUOTE"] = "SQT",       ["APOS"] = "SQT",       ["APOSTROPHE"] = "SQT",
        ["PERIOD"] = "DOT",
        ["LEFT_BRACKET"] = "LBKT",      ["RIGHT_BRACKET"] = "RBKT",

        // Edit / whitespace long-form
        ["BACKSPACE"] = "BSPC",
        ["ENTER"] = "RET",              ["RETURN"] = "RET",
        ["ESCAPE"] = "ESC",
        ["DELETE"] = "DEL",
        ["CAPSLOCK"] = "CAPS",          ["CAPS_LOCK"] = "CAPS",

        // Arrows / nav
        ["UP_ARROW"] = "UP",            ["DOWN_ARROW"] = "DOWN",
        ["LEFT_ARROW"] = "LEFT",        ["RIGHT_ARROW"] = "RIGHT",
        ["PAGE_UP"] = "PG_UP",          ["PAGE_DOWN"] = "PG_DN",
        ["INSERT"] = "INS",

        // Number long-form
        ["NUMBER_0"] = "N0",            ["NUMBER_1"] = "N1",
        ["NUMBER_2"] = "N2",            ["NUMBER_3"] = "N3",
        ["NUMBER_4"] = "N4",            ["NUMBER_5"] = "N5",
        ["NUMBER_6"] = "N6",            ["NUMBER_7"] = "N7",
        ["NUMBER_8"] = "N8",            ["NUMBER_9"] = "N9",

        // Keypad long-form → short form (keypad keys are not shift-wrapped)
        ["KP_NUMBER_0"] = "KP_N0",      ["KP_NUMBER_1"] = "KP_N1",
        ["KP_NUMBER_2"] = "KP_N2",      ["KP_NUMBER_3"] = "KP_N3",
        ["KP_NUMBER_4"] = "KP_N4",      ["KP_NUMBER_5"] = "KP_N5",
        ["KP_NUMBER_6"] = "KP_N6",      ["KP_NUMBER_7"] = "KP_N7",
        ["KP_NUMBER_8"] = "KP_N8",      ["KP_NUMBER_9"] = "KP_N9",
        ["KP_EQUALS"] = "KP_EQUAL",
        ["KP_ASTERISK"] = "KP_MULTIPLY",
        ["KP_PERIOD"] = "KP_DOT",
        ["KP_SLASH"] = "KP_DIVIDE",
    };

    /// <summary>
    /// Shifted-symbol aliases: these ZMK names implicitly include a Shift
    /// modifier. A binding like <c>&amp;kp LPAR</c> makes the firmware emit
    /// Shift+9, so the lookup entry must be keyed on the <em>shift+base</em>
    /// combo, not bare N9 (which is what the number key 9 emits). The base
    /// codes here are the unshifted US-layout counterparts.
    /// </summary>
    private static readonly Dictionary<string, string> ZmkShiftedSymbols = new(StringComparer.Ordinal)
    {
        ["EXCL"] = "N1",                ["EXCLAMATION"] = "N1",
        ["AT"] = "N2",                  ["AT_SIGN"] = "N2",
        ["HASH"] = "N3",                ["POUND"] = "N3",
        ["DLLR"] = "N4",                ["DOLLAR"] = "N4",
        ["PRCNT"] = "N5",               ["PERCENT"] = "N5",
        ["CARET"] = "N6",
        ["AMPS"] = "N7",                ["AMPERSAND"] = "N7",
        ["STAR"] = "N8",                ["ASTERISK"] = "N8",
        ["LPAR"] = "N9",                ["LEFT_PARENTHESIS"] = "N9",
        ["RPAR"] = "N0",                ["RIGHT_PARENTHESIS"] = "N0",
        ["LBRC"] = "LBKT",              ["LEFT_BRACE"] = "LBKT",
        ["RBRC"] = "RBKT",              ["RIGHT_BRACE"] = "RBKT",
        ["COLON"] = "SEMI",
        ["DQT"] = "SQT",                ["DOUBLE_QUOTES"] = "SQT",
        ["TILDE"] = "GRAVE",
        ["PIPE"] = "BSLH",
        ["QMARK"] = "FSLH",             ["QUESTION"] = "FSLH",
        ["UNDER"] = "MINUS",            ["UNDERSCORE"] = "MINUS",
        ["PLUS"] = "EQUAL",
        ["LT"] = "COMMA",               ["LESS_THAN"] = "COMMA",
        ["GT"] = "DOT",                 ["GREATER_THAN"] = "DOT",
    };

    /// <summary>
    /// Normalizes a modifier keycode (as emitted by the hook) to its
    /// left/right-agnostic category so the lookup matches bindings that
    /// wrapped in LS(...) when the user physically held RSHFT (and vice
    /// versa). Returns null for non-modifier keycodes.
    /// </summary>
    private static string? CategoryForModifier(string zmkCode) => zmkCode switch
    {
        "LSHFT" or "RSHFT" => "shift",
        "LCTRL" or "RCTRL" => "ctrl",
        "LALT" or "RALT" => "alt",
        "LGUI" or "RGUI" => "gui",
        _ => null,
    };

    /// <summary>
    /// Categorizes a ZMK modifier-wrapper prefix (LS/RS/LC/RC/LA/RA/LG/RG)
    /// into one of the four OS-visible categories. Returns null if the
    /// token is not a recognized wrapper — which also acts as a sanity
    /// stop when we're walking flattened params to build a binding's
    /// required-modifier set.
    /// </summary>
    private static string? CategoryForWrapperPrefix(string p) => p switch
    {
        "LS" or "RS" => "shift",
        "LC" or "RC" => "ctrl",
        "LA" or "RA" => "alt",
        "LG" or "RG" => "gui",
        _ => null,
    };

    private static string BuildLookupKey(IEnumerable<string> modCategories, string code)
    {
        var sorted = modCategories.OrderBy(m => m, StringComparer.Ordinal);
        return $"{string.Join("+", sorted)}|{code}";
    }


    /// <summary>
    /// Builds the reverse-adjacency map: for each layer, the set of layers
    /// that can push it onto the active stack. Uses the same stack-aware
    /// behaviors as ZMK — <c>&amp;mo / &amp;lt / &amp;sl / &amp;tog</c>
    /// and signal macros (all of which wrap <c>&amp;mo</c>). <c>&amp;to</c>
    /// is excluded because it replaces the default layer rather than
    /// stacking above it.
    /// </summary>
    private static Dictionary<int, HashSet<int>> BuildLayerPredecessors(
        KeyboardConfig config,
        IReadOnlyList<SignalMacro> signalMacros)
    {
        var result = new Dictionary<int, HashSet<int>>();
        // Hold-tap aliases included so &ht_* bindings register as predecessors
        // of the layer their wrapped signal macro activates.
        var signalByName = LayerSignalTable.BuildSignalLookup(config, signalMacros);

        for (int m = 0; m < config.Layers.Count; m++)
        {
            foreach (var b in config.Layers[m].Bindings)
            {
                int? target = null;

                if ((b.Behavior == "&mo" || b.Behavior == "&lt"
                     || b.Behavior == "&sl" || b.Behavior == "&tog")
                    && b.Params.Count >= 1 && int.TryParse(b.Params[0], out var bareLayer))
                {
                    target = bareLayer;
                }
                else if (signalByName.TryGetValue(b.Behavior, out var sig)
                    && sig.TryResolveTargetLayer(b, out var sigLayer))
                {
                    target = sigLayer;
                }

                if (target is int n && n >= 0 && n < config.Layers.Count && n != m)
                {
                    if (!result.TryGetValue(n, out var preds))
                        result[n] = preds = new HashSet<int>();
                    preds.Add(m);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Walks the predecessor graph from <paramref name="layerIdx"/> to find
    /// the binding that would actually fire at <paramref name="keyIdx"/>.
    /// If the binding at the given layer is <c>&amp;trans</c>, recursively
    /// checks each predecessor layer (the ones that can stack this layer
    /// on top of them) and returns the first non-transparent result.
    /// Base-layer (0) <c>&amp;trans</c> stays transparent.
    /// </summary>
    private KeyBinding ResolveEffectiveBinding(int layerIdx, int keyIdx)
        => ResolveEffectiveBinding(layerIdx, keyIdx, new HashSet<int>());

    private KeyBinding ResolveEffectiveBinding(int layerIdx, int keyIdx, HashSet<int> visited)
    {
        if (_config is null || !visited.Add(layerIdx)) return KeyBinding.Transparent;
        if (layerIdx < 0 || layerIdx >= _config.Layers.Count) return KeyBinding.Transparent;

        var layer = _config.Layers[layerIdx];
        var binding = keyIdx < layer.Bindings.Count ? layer.Bindings[keyIdx] : KeyBinding.Transparent;
        if (binding.Behavior != "&trans") return binding;

        // Try direct predecessors in index order — deterministic, and usually
        // puts the closer-to-base layers first.
        if (_layerPredecessors.TryGetValue(layerIdx, out var preds))
        {
            foreach (var p in preds.OrderBy(x => x))
            {
                var ft = ResolveEffectiveBinding(p, keyIdx, visited);
                if (ft.Behavior != "&trans") return ft;
            }
        }

        // Fallback: if we're not on base and base wasn't in the predecessor
        // chain (orphan layer with no recorded activation), fall through to
        // base directly so the label is at least meaningful.
        if (layerIdx != 0)
        {
            var ft = ResolveEffectiveBinding(0, keyIdx, visited);
            if (ft.Behavior != "&trans") return ft;
        }

        return binding;
    }

    /// <summary>
    /// For a key binding, returns which layer it activates when pressed
    /// (if any). Signal macros route through <see cref="SignalMacro.LayerParamIndex"/>;
    /// the bare ZMK layer-switch behaviors (<c>&amp;to / &amp;mo / &amp;tog /
    /// &amp;lt / &amp;sl</c>) read their first param directly. Returns null
    /// for non-layer-switching bindings.
    /// </summary>
    private static int? ResolveTargetLayer(KeyBinding binding, SignalMacro? signal, HoldTap? holdTap = null)
    {
        if (signal is not null && signal.TryResolveTargetLayer(binding, out var signalLayer))
            return signalLayer;

        if ((binding.Behavior == "&to" || binding.Behavior == "&mo"
             || binding.Behavior == "&tog" || binding.Behavior == "&lt"
             || binding.Behavior == "&sl")
            && binding.Params.Count >= 1
            && int.TryParse(binding.Params[0], out var paramLayer))
            return paramLayer;

        // Hold-tap whose hold side is itself a layer-switch (e.g. the user-
        // defined `&space_v3_TKZ` with bindings ["&mo", "&kp"]). The hold-side
        // params come first in the binding's param list, so the leading param
        // is the layer index.
        if (holdTap is not null
            && (holdTap.HoldBinding == "&to" || holdTap.HoldBinding == "&mo"
                || holdTap.HoldBinding == "&tog" || holdTap.HoldBinding == "&lt"
                || holdTap.HoldBinding == "&sl")
            && holdTap.HoldArity >= 1
            && binding.Params.Count >= 1
            && int.TryParse(binding.Params[0], out var holdLayer))
            return holdLayer;

        return null;
    }

    private void ToggleLiveHighlighting()
    {
        IsLiveHighlightingEnabled = !IsLiveHighlightingEnabled;
        PersistSetting(s2 => s2 with { LiveKeyHighlighting = IsLiveHighlightingEnabled });

        if (IsLiveHighlightingEnabled)
            StartKeyEventTracking();
        else
            StopKeyEventTracking();
    }

    private void StartKeyEventTracking()
    {
        if (_layerCoordinator is not null) return;
        // Re-arm the accessibility-prompt latch on every start so a later
        // failure (perms revoked at runtime, hook restart) can prompt again.
        _accessibilityDialogShown = false;

        HotkeyLayerTrackerLayerSource? hotkeyWrapper = null;
        if (_hookProvider is not null)
        {
            try
            {
                var source = new SharpHookKeyEventSource(_hookProvider);
                source.HookFailed += OnHookFailed;
                _keyEventSource = source;
                _tracker = new HotkeyLayerTracker(_keyEventSource, _mergedSignalTable);
                _tracker.KeyObserved += OnKeyObservedFromHook;
                _keyEventSource.Start();
                hotkeyWrapper = new HotkeyLayerTrackerLayerSource(_tracker);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("MainVM", $"SharpHook init failed: {ex.Message}");
                _keyEventSource?.Dispose();
                _keyEventSource = null;
                _tracker = null;
                hotkeyWrapper = null;
            }
        }

        // Raw HID is platform-agnostic and doesn't need accessibility perms,
        // so it spins up regardless of the SharpHook outcome above. The
        // profile filter scopes discovery to the user's selected keyboard
        // (both Moergo boards share VID:PID, so we'd otherwise latch onto
        // whichever is enumerated first).
        //
        // Per-OS impl: HidSharp's macOS backend doesn't see BLE-HoGP devices,
        // and on Windows the HoGP driver strips ZMK's vendor-defined FF60/61
        // collection so HidSharp can't see it over Bluetooth either. Each
        // non-portable backend goes around the OS HID stack at the layer that
        // works (IOKit on macOS, /dev/hidraw on Linux, WinRT GATT on Windows
        // for BLE alongside HidSharp for USB).
        ILayerSource hidSource;
#if WINDOWS
        hidSource = new WindowsHidCompositeLayerSource(_profile);
#else
        hidSource = OperatingSystem.IsMacOS()
            ? new MacRawHidLayerSource(_profile)
            : OperatingSystem.IsLinux()
                ? new LinuxRawHidLayerSource(_profile)
                : new RawHidLayerSource(_profile);
#endif

        _layerCoordinator = new LayerSourceCoordinator(hidSource, hotkeyWrapper, _layerSourceMode);
        _layerCoordinator.ActiveLayerChanged += OnActiveLayerChanged;
        _layerCoordinator.ActiveKeyPositionEvent += OnKeyPositionFromHid;
        _layerCoordinator.ActiveSourceChanged += OnActiveSourceChanged;
        _layerCoordinator.Start();
        // Initial label sync — the coordinator may already have settled the
        // active source before our subscription was attached above.
        OnActiveSourceChanged();
    }

    private void OnHookFailed(Exception ex)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (_accessibilityDialogShown) return;
        _accessibilityDialogShown = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowAccessibilityPromptRequested?.Invoke());
    }

    private void StopKeyEventTracking()
    {
        if (_layerCoordinator is not null)
        {
            _layerCoordinator.ActiveLayerChanged -= OnActiveLayerChanged;
            _layerCoordinator.ActiveKeyPositionEvent -= OnKeyPositionFromHid;
            _layerCoordinator.ActiveSourceChanged -= OnActiveSourceChanged;
            _layerCoordinator.Dispose();
            _layerCoordinator = null;
        }
        if (_tracker is not null)
        {
            _tracker.KeyObserved -= OnKeyObservedFromHook;
            _tracker.Dispose();
            _tracker = null;
        }
        if (_keyEventSource is SharpHookKeyEventSource sh)
            sh.HookFailed -= OnHookFailed;
        _keyEventSource?.Dispose();
        _keyEventSource = null;
        IsHidSourceActive = false;
        LayerSourceHint = "";
    }

    private void OnActiveLayerChanged(int layer)
    {
        if (!IsAutoLayerSwitchEnabled) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyActiveLayer(layer));
    }

    private void OnActiveSourceChanged()
    {
        if (_layerCoordinator is null) return;
        var hidActive = _layerCoordinator.IsHidActive;
        var label = _layerCoordinator.ActiveSourceLabel;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var flipped = IsHidSourceActive != hidActive;
            IsHidSourceActive = hidActive;
            LayerSourceHint = string.IsNullOrEmpty(label)
                ? ""
                : Loc.Instance.Format("Status_LayerSourceHintFormat", label);
            // Pink "untrackable" overlays are gated on !IsHidSourceActive; rebuild
            // the per-key state so the change takes effect immediately.
            if (flipped) ApplyActiveLayer(ActiveLayerIndex);
            // The "N layer switches not tracked" suffix only applies when
            // SharpHook is the source — HID reports every layer change, so
            // recompose to drop/restore that suffix on flips.
            if (flipped && _loadStatusBase is not null)
                StatusMessage = ComposeLoadStatus();
        });
    }

    private string ComposeLoadStatus()
    {
        var s = _loadStatusBase ?? "";
        if (_untrackable.Count > 0 && !IsHidSourceActive)
            s += " — " + Loc.Instance.Format("Status_UntrackableLayersFormat", _untrackable.Count);
        return s;
    }

    /// <summary>
    /// Press-highlight path for the HID source. Bypasses _zmkLookup entirely
    /// — the firmware reports the physical matrix position so we go straight
    /// to <see cref="Keys"/>[position]. No modifier-grace logic needed
    /// (HID never reports synthesized modifiers as separate events).
    /// </summary>
    private void OnKeyPositionFromHid(int position, bool pressed)
    {
        if (!pressed) return;
        if (position < 0 || position >= Keys.Count) return;
        var vm = Keys[position];
        Avalonia.Threading.Dispatcher.UIThread.Post(() => PulseKeyPress(vm));
    }

    /// <summary>Called by SettingsViewModel when the user picks a different layer source mode.</summary>
    public void SetLayerSourceMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        if (mode == _layerSourceMode) return;
        _layerSourceMode = mode;
        PersistSetting(s => s with { LayerSource = mode });
        _layerCoordinator?.SetMode(mode);
    }

    public string LayerSourceMode => _layerSourceMode;

    private void OnKeyObservedFromHook(KeyEvent ev)
    {
        // HID-position highlights take precedence whenever the HID source is
        // active — running both pipelines would double-pulse on every press.
        if (IsHidSourceActive) return;

        var modCat = CategoryForModifier(ev.Keycode);

        if (ev.Kind == KeyEventKind.Released)
        {
            if (modCat is not null) _heldModifierCategories.Remove(modCat);
            return;
        }

        // For modifier keypresses themselves, look up with NO modifiers held
        // — the binding `&kp LSHFT` has no wrappers and is keyed as "|LSHFT".
        // For all other keys, use the currently-held modifier set so the
        // lookup discriminates between (e.g.) `&kp N8` and `&kp LS(N8)`.
        var contextMods = modCat is not null ? Array.Empty<string>() : (IEnumerable<string>)_heldModifierCategories;
        var key = BuildLookupKey(contextMods, ev.Keycode);

        if (!_zmkLookup.TryGetValue(key, out var targets) || targets.Count == 0)
        {
            DiagnosticLog.Debug("Highlight", $"miss key={key} layer={ActiveLayerIndex} tableSize={_zmkLookup.Count}");
        }
        else
        {
            DiagnosticLog.Debug("Highlight", $"hit key={key} layer={ActiveLayerIndex} → {targets.Count} key(s)");
            if (modCat is not null)
            {
                // Defer modifier highlight — cancelled below if a companion
                // non-modifier press follows within ModifierGraceMs.
                var cts = new CancellationTokenSource();
                lock (_pendingModHighlights) _pendingModHighlights.Add(cts);
                _ = Task.Delay(ModifierGraceMs, cts.Token).ContinueWith(t =>
                {
                    // Remove inside the lock so the cancel path's foreach can
                    // never see a disposed instance. Dispose unconditionally —
                    // both the elapsed and cancelled branches need it, and the
                    // earlier cancel-path-leaks-CTS bug came from skipping it.
                    bool stillPending;
                    lock (_pendingModHighlights) stillPending = _pendingModHighlights.Remove(cts);
                    if (stillPending && !t.IsCanceled)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            foreach (var vm in targets) PulseKeyPress(vm);
                        });
                    }
                    cts.Dispose();
                }, TaskScheduler.Default);
            }
            else
            {
                // Real (non-modifier) keypress — cancel any pending mod
                // highlights; they were synthesized by the firmware.
                lock (_pendingModHighlights)
                {
                    foreach (var c in _pendingModHighlights) c.Cancel();
                    _pendingModHighlights.Clear();
                }
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    foreach (var vm in targets) PulseKeyPress(vm);
                });
            }
        }

        // Update held-mod state AFTER the lookup so a modifier key's own
        // press still matches its no-mod binding.
        if (modCat is not null) _heldModifierCategories.Add(modCat);
    }

    private void PulseKeyPress(KeyViewModel vm)
    {
        if (_pressCts.TryGetValue(vm, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }
        var cts = new CancellationTokenSource();
        _pressCts[vm] = cts;
        vm.IsPressed = true;

        _ = Task.Delay(PressHighlightMs, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_pressCts.TryGetValue(vm, out var stored) && stored == cts)
                {
                    vm.IsPressed = false;
                    _pressCts.Remove(vm);
                    cts.Dispose();
                }
            });
        }, TaskScheduler.Default);
    }

    private void ResetLayerState()
    {
        _tracker?.Reset();
        _heldModifierCategories.Clear();
        ApplyActiveLayer(0);
    }

    private void PersistSetting(Func<UserSettings, UserSettings> update)
    {
        try
        {
            var s = _settingsService.Load();
            _settingsService.Save(update(s));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("MainVM", $"Persist settings failed: {ex.Message}");
        }
    }
}
