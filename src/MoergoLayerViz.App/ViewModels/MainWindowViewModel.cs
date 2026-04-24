using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private IReadOnlyList<UntrackableLayerSwitch> _untrackable = Array.Empty<UntrackableLayerSwitch>();

    // Reverse adjacency: <layer> → set of layers that can push this layer onto
    // the active stack (via &mo / &lt / &sl / &tog / signal macro). Used to
    // resolve `&trans` fall-through when viewing a layer statically.
    private Dictionary<int, HashSet<int>> _layerPredecessors = new();

    private IKeyEventSource? _keyEventSource;
    private HotkeyLayerTracker? _tracker;

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
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isAlwaysOnTop;
    [ObservableProperty] private bool _isLiveHighlightingEnabled;
    [ObservableProperty] private bool _isAutoLayerSwitchEnabled;
    [ObservableProperty] private bool _hasLayoutLoaded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveLayerTintColor))]
    private int _activeLayerIndex;

    /// <summary>Palette color for the active layer — used as the press-highlight pulse fill.</summary>
    public string ActiveLayerTintColor => LayerColorPalette.GetColor(ActiveLayerIndex);

    public ObservableCollection<KeyViewModel> Keys { get; } = new();
    public ObservableCollection<LayerViewModel> Layers { get; } = new();

    /// <summary>Canvas size for the current keyboard profile (drives BoardView's Canvas width/height).</summary>
    public double CanvasWidth => _profile.CanvasWidth;
    public double CanvasHeight => _profile.CanvasHeight;

    /// <summary>All keyboard profiles the user can switch between, for the picker flyout.</summary>
    public IReadOnlyList<IKeyboardProfile> AvailableKeyboards => KeyboardProfileRegistry.All;

    /// <summary>
    /// Currently selected profile. Bound to the picker button label and drives
    /// checkmark selection in the flyout.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyboardStatusHint))]
    private IKeyboardProfile _selectedKeyboard = null!;

    /// <summary>Right-aligned status-bar hint: "DisplayName · N keys".</summary>
    public string KeyboardStatusHint =>
        Loc.Instance.Format("Status_KeyboardHintFormat",
            SelectedKeyboard?.DisplayName ?? "", SelectedKeyboard?.KeyCount ?? 0);

    // --- Callbacks set by App.axaml.cs to bridge to the Window ---
    public Action? QuitRequested { get; set; }
    public Action? ShowWindowRequested { get; set; }
    public Action? ToggleWindowRequested { get; set; }
    public Func<Task>? LoadLayoutRequested { get; set; }
    public Func<Task>? CopyDiagnosticsRequested { get; set; }
    public Action? ShowAccessibilityPromptRequested { get; set; }

    private bool _accessibilityDialogShown;

    // --- Commands ---
    public IRelayCommand QuitCommand { get; }
    public IRelayCommand ShowCommand { get; }
    public IRelayCommand LoadLayoutCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand TogglePinCommand { get; }
    public IRelayCommand ToggleLiveHighlightingCommand { get; }
    public IRelayCommand ToggleAutoLayerSwitchCommand { get; }
    public IRelayCommand ResetLayerStateCommand { get; }
    public IRelayCommand OpenLogFolderCommand { get; }
    public IRelayCommand CopyDiagnosticsCommand { get; }
    public IRelayCommand<IKeyboardProfile> SelectKeyboardCommand { get; }

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

        QuitCommand = new RelayCommand(() => QuitRequested?.Invoke());
        ShowCommand = new RelayCommand(() => ShowWindowRequested?.Invoke());
        LoadLayoutCommand = new AsyncRelayCommand(async () =>
        {
            if (LoadLayoutRequested is not null) await LoadLayoutRequested();
        });
        RefreshCommand = new RelayCommand(() =>
        {
            var path = _settingsService.Load().LayoutJsonPath;
            if (!string.IsNullOrEmpty(path)) LoadLayoutFromPath(path);
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
        ResetLayerStateCommand = new RelayCommand(ResetLayerState);
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
                StatusMessage = $"Could not open log folder: {ex.Message}";
            }
        });
        CopyDiagnosticsCommand = new AsyncRelayCommand(async () =>
        {
            if (CopyDiagnosticsRequested is not null) await CopyDiagnosticsRequested();
        });
        SelectKeyboardCommand = new RelayCommand<IKeyboardProfile>(SelectKeyboard);

        BuildKeysFromProfile();
    }

    /// <summary>
    /// Post-startup entry point: load last-used layout, spin up the key-event
    /// hook, and set an opening status message.
    /// </summary>
    public void InitializeAsync()
    {
        var s = _settingsService.Load();
        if (!string.IsNullOrWhiteSpace(s.LayoutJsonPath) && File.Exists(s.LayoutJsonPath))
        {
            LoadLayoutFromPath(s.LayoutJsonPath);
        }
        else
        {
            StatusMessage = Loc.Instance["Status_NoLayoutLoaded"];
        }

        if (IsLiveHighlightingEnabled && !OperatingSystem.IsLinux())
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
                    OnPropertyChanged(nameof(CanvasWidth));
                    OnPropertyChanged(nameof(CanvasHeight));
                    PersistSetting(s => s with { Keyboard = matching.Id });
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

            _tracker?.UpdateTable(_signalTable);

            RebuildLayers();
            ApplyActiveLayer(0);
            HasLayoutLoaded = true;

            PersistSetting(s => s with { LayoutJsonPath = path });

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
            if (_untrackable.Count > 0)
            {
                baseMsg += " — " + Loc.Instance.Format("Status_UntrackableLayersFormat", _untrackable.Count);
            }
            StatusMessage = baseMsg;
            DiagnosticLog.Info("MainVM",
                $"Loaded '{path}' signalMacros={_signalMacros.Count} untrackable={_untrackable.Count}");
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Instance.Format("Status_LoadErrorFormat", ex.Message);
            DiagnosticLog.Error("MainVM", $"Load failed: {ex}");
        }
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
        foreach (var pos in _profile.Keys)
            Keys.Add(new KeyViewModel(pos));
    }

    private void SelectKeyboard(IKeyboardProfile? profile)
    {
        if (profile is null) return;
        if (string.Equals(profile.Id, _profile.Id, StringComparison.OrdinalIgnoreCase)) return;

        _profile = profile;
        SelectedKeyboard = profile;
        BuildKeysFromProfile();
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));

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
            _tracker?.UpdateTable(_signalTable);
            Layers.Clear();
            ActiveLayerIndex = 0;
            HasLayoutLoaded = false;
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitchedUnloaded", profile.DisplayName);
        }
        else if (_config is not null)
        {
            ApplyActiveLayer(ActiveLayerIndex);
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitched", profile.DisplayName);
        }
        else
        {
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitched", profile.DisplayName);
        }

        PersistSetting(s => s with { Keyboard = profile.Id });
        DiagnosticLog.Info("MainVM", $"Keyboard profile switched to {profile.Id}");
    }

    private void RebuildLayers()
    {
        Layers.Clear();
        if (_config is null) return;
        foreach (var layer in _config.Layers)
        {
            Layers.Add(new LayerViewModel(
                layer.Index,
                layer.Name,
                LayerColorPalette.GetColor(layer.Index),
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

        var signalByName = _signalMacros.ToDictionary(m => m.MacroName, StringComparer.Ordinal);
        var untrackableSet = new HashSet<(int layer, int key)>(_untrackable.Select(u => (u.LayerIndex, u.KeyIndex)));

        for (int i = 0; i < Keys.Count; i++)
        {
            // Resolve the effective binding by walking the predecessor graph:
            // `&trans` falls through to the layer that can activate this one
            // (recursively) until a non-transparent binding is found.
            var binding = ResolveEffectiveBinding(layer.Index, i);

            var isSignal = signalByName.TryGetValue(binding.Behavior, out var signalMacro);
            var targetLayer = ResolveTargetLayer(binding, isSignal ? signalMacro : null);
            var targetLayerName = targetLayer is int tl && tl >= 0 && tl < _config.Layers.Count
                ? _config.Layers[tl].Name
                : null;
            Keys[i].ApplyBinding(
                binding,
                isSignalMacro: isSignal,
                isUntrackable: untrackableSet.Contains((layer.Index, i)),
                targetLayer: targetLayer,
                targetLayerName: targetLayerName);
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
        _zmkLookup = new Dictionary<string, List<KeyViewModel>>(StringComparer.Ordinal);
        if (_config is null) return;
        var layerIdx = ActiveLayerIndex;
        if (layerIdx < 0 || layerIdx >= _config.Layers.Count) layerIdx = 0;
        for (int i = 0; i < Keys.Count; i++)
        {
            var binding = ResolveEffectiveBinding(layerIdx, i);
            var signal = signalByName.TryGetValue(binding.Behavior, out var s) ? s : null;
            var press = ExtractEmittedKeypress(binding, signal);
            if (press is null) continue;
            var key = BuildLookupKey(press.Value.Mods, press.Value.Code);
            if (!_zmkLookup.TryGetValue(key, out var list))
                _zmkLookup[key] = list = new List<KeyViewModel>();
            list.Add(Keys[i]);
        }
        DiagnosticLog.Debug("Highlight", $"lookup rebuilt layer={layerIdx} keys=[{string.Join(",", _zmkLookup.Keys)}]");
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
        if (signal is not null && b.Params.Count > signal.KeyParamIndex)
        {
            startIndex = signal.KeyParamIndex;
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
        var signalByName = signalMacros.ToDictionary(m => m.MacroName, StringComparer.Ordinal);

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
                    && b.Params.Count > sig.LayerParamIndex
                    && int.TryParse(b.Params[sig.LayerParamIndex], out var sigLayer))
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
    private static int? ResolveTargetLayer(KeyBinding binding, SignalMacro? signal)
    {
        if (signal is not null && binding.Params.Count > signal.LayerParamIndex
            && int.TryParse(binding.Params[signal.LayerParamIndex], out var signalLayer))
            return signalLayer;

        if ((binding.Behavior == "&to" || binding.Behavior == "&mo"
             || binding.Behavior == "&tog" || binding.Behavior == "&lt"
             || binding.Behavior == "&sl")
            && binding.Params.Count >= 1
            && int.TryParse(binding.Params[0], out var paramLayer))
            return paramLayer;

        return null;
    }

    private void ToggleLiveHighlighting()
    {
        IsLiveHighlightingEnabled = !IsLiveHighlightingEnabled;
        PersistSetting(s2 => s2 with { LiveKeyHighlighting = IsLiveHighlightingEnabled });

        if (IsLiveHighlightingEnabled && !OperatingSystem.IsLinux())
            StartKeyEventTracking();
        else
            StopKeyEventTracking();
    }

    private void StartKeyEventTracking()
    {
        if (_keyEventSource is not null) return;
        if (_hookProvider is null) return;
        try
        {
            var source = new SharpHookKeyEventSource(_hookProvider);
            source.HookFailed += OnHookFailed;
            _keyEventSource = source;
            _tracker = new HotkeyLayerTracker(_keyEventSource, _signalTable);
            _tracker.LayerChanged += OnLayerChangedFromHook;
            _tracker.KeyObserved += OnKeyObservedFromHook;
            _keyEventSource.Start();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("MainVM", $"StartKeyEventTracking failed: {ex.Message}");
            _keyEventSource?.Dispose();
            _keyEventSource = null;
            _tracker = null;
        }
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
        if (_tracker is not null)
        {
            _tracker.LayerChanged -= OnLayerChangedFromHook;
            _tracker.KeyObserved -= OnKeyObservedFromHook;
            _tracker.Dispose();
            _tracker = null;
        }
        if (_keyEventSource is SharpHookKeyEventSource sh)
            sh.HookFailed -= OnHookFailed;
        _keyEventSource?.Dispose();
        _keyEventSource = null;
    }

    private void OnLayerChangedFromHook(int layer)
    {
        if (!IsAutoLayerSwitchEnabled) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyActiveLayer(layer));
    }

    private void OnKeyObservedFromHook(KeyEvent ev)
    {
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
                    if (t.IsCanceled) return;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        lock (_pendingModHighlights) _pendingModHighlights.Remove(cts);
                        foreach (var vm in targets) PulseKeyPress(vm);
                        cts.Dispose();
                    });
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
