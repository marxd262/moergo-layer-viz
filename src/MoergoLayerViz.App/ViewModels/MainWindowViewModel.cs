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

    // Active-layer keycode → KeyViewModel(s) lookup, rebuilt on every layer
    // change so OnKeyObservedFromHook can flash the right physical key.
    private Dictionary<string, List<KeyViewModel>> _zmkLookup = new(StringComparer.Ordinal);
    private readonly Dictionary<KeyViewModel, CancellationTokenSource> _pressCts = new();
    private const int PressHighlightMs = 90;

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

    private readonly SharpHookProvider? _hookProvider;

    public MainWindowViewModel(ISettingsService settingsService, SharpHookProvider? hookProvider = null)
    {
        _settingsService = settingsService;
        _hookProvider = hookProvider;
        var s = settingsService.Load();
        _profile = KeyboardProfileRegistry.TryResolve(s.Keyboard, out var p) ? p : new Go60Profile();
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
            if (config.LayerCount > 0 && config.Layers[0].Bindings.Count != _profile.KeyCount)
            {
                DiagnosticLog.Warn("MainVM",
                    $"Loaded layout has {config.Layers[0].Bindings.Count} keys but profile {_profile.Id} expects {_profile.KeyCount} — visualization will clip/pad to profile");
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
    /// current layer" map. Covers <c>&amp;kp</c> (modifier wrappers stripped),
    /// <c>&amp;lt</c>, <c>&amp;HRM_*</c>, and signal macros. The emitted code
    /// must match the form produced by
    /// <see cref="SharpHookKeyEventSource"/> — i.e. the raw ZMK keycode
    /// string, not the display glyph.
    /// </summary>
    private void RebuildZmkLookup(Dictionary<string, SignalMacro> signalByName)
    {
        _zmkLookup = new Dictionary<string, List<KeyViewModel>>(StringComparer.Ordinal);
        if (_config is null) return;
        var layer = _config.Layers[ActiveLayerIndex];
        for (int i = 0; i < Keys.Count; i++)
        {
            var binding = ResolveEffectiveBinding(layer.Index, i);
            var signal = signalByName.TryGetValue(binding.Behavior, out var s) ? s : null;
            var code = ExtractEmittedZmkKeycode(binding, signal);
            if (code is null) continue;
            if (!_zmkLookup.TryGetValue(code, out var list))
                _zmkLookup[code] = list = new List<KeyViewModel>();
            list.Add(Keys[i]);
        }
    }

    /// <summary>
    /// Returns the OS-visible ZMK keycode a binding emits on press, or null
    /// if the binding does not surface a keycode to the host.
    /// </summary>
    private static string? ExtractEmittedZmkKeycode(KeyBinding b, SignalMacro? signal)
    {
        if (signal is not null && b.Params.Count > signal.KeyParamIndex)
            return StripModifierWrappers(b.Params[signal.KeyParamIndex]);

        switch (b.Behavior)
        {
            case "&kp" when b.Params.Count >= 1:
                return StripModifierWrappers(b.Params[0]);
            case "&lt" when b.Params.Count >= 2:
                return StripModifierWrappers(b.Params[1]);
        }

        if (b.Behavior.StartsWith("&HRM_", StringComparison.Ordinal) && b.Params.Count == 2)
            return StripModifierWrappers(b.Params[1]);

        return null;
    }

    /// <summary>
    /// Strips one or more layers of ZMK modifier wrappers (<c>LS(...)</c>,
    /// <c>LC(...)</c>, <c>LA(...)</c>, <c>LG(...)</c>, R* equivalents) to
    /// reveal the base keycode. <c>LS(LBKT)</c> → <c>LBKT</c>.
    /// </summary>
    private static readonly string[] ModWrappers = { "LS(", "LC(", "LA(", "LG(", "RS(", "RC(", "RA(", "RG(" };

    private static string StripModifierWrappers(string code)
    {
        var s = code.Trim();
        while (s.Length > 4 && s[^1] == ')' && Array.Exists(ModWrappers, w => s.StartsWith(w, StringComparison.Ordinal)))
            s = s.Substring(3, s.Length - 4).Trim();
        return s;
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
        if (ev.Kind != KeyEventKind.Pressed) return;
        if (!_zmkLookup.TryGetValue(ev.Keycode, out var targets) || targets.Count == 0) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var vm in targets)
                PulseKeyPress(vm);
        });
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
