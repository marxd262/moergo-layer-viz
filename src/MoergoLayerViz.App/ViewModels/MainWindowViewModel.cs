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

    private IKeyEventSource? _keyEventSource;
    private HotkeyLayerTracker? _tracker;

    // --- UI-bindable state ---
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isAlwaysOnTop;
    [ObservableProperty] private bool _isLiveHighlightingEnabled;
    [ObservableProperty] private bool _isAutoLayerSwitchEnabled;
    [ObservableProperty] private bool _hasLayoutLoaded;
    [ObservableProperty] private int _activeLayerIndex;

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

    public MainWindowViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
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

        var signalNames = new HashSet<string>(_signalMacros.Select(m => m.MacroName), StringComparer.Ordinal);
        var untrackableSet = new HashSet<(int layer, int key)>(_untrackable.Select(u => (u.LayerIndex, u.KeyIndex)));

        for (int i = 0; i < Keys.Count; i++)
        {
            var binding = i < layer.Bindings.Count ? layer.Bindings[i] : KeyBinding.Transparent;
            Keys[i].ApplyBinding(
                binding,
                isSignalMacro: signalNames.Contains(binding.Behavior),
                isUntrackable: untrackableSet.Contains((layer.Index, i)));
        }

        for (int i = 0; i < Layers.Count; i++)
            Layers[i].IsSelected = Layers[i].Index == index;
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
        try
        {
            _keyEventSource = new SharpHookKeyEventSource();
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

    private void StopKeyEventTracking()
    {
        if (_tracker is not null)
        {
            _tracker.LayerChanged -= OnLayerChangedFromHook;
            _tracker.KeyObserved -= OnKeyObservedFromHook;
            _tracker.Dispose();
            _tracker = null;
        }
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
        // Future: highlight the physical key when pressed. For now we just
        // keep the event plumbed so we can wire UI feedback without another
        // service roundtrip.
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
