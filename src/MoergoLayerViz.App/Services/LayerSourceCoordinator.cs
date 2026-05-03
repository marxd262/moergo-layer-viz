using MoergoLayerViz.Core.Diagnostics;
using MoergoLayerViz.Core.Input;
using MoergoLayerViz.Core.Layout;

namespace MoergoLayerViz.App.Services;

/// <summary>
/// Decides which <see cref="ILayerSource"/> currently drives the UI's layer
/// state and key-press highlights, and hot-swaps between them as devices
/// connect / disconnect.
///
/// <para>The two underlying sources are kept warm in "Auto" mode so a HID
/// disconnect can fall back to SharpHook with no restart. The coordinator
/// owns the subscription wiring; the rest of the app subscribes to the
/// coordinator's <see cref="ActiveLayerChanged"/>,
/// <see cref="ActiveKeyPositionEvent"/>, and <see cref="ActiveSourceChanged"/>
/// events instead of touching the sources directly.</para>
/// </summary>
public sealed class LayerSourceCoordinator : IDisposable
{
    public const string ModeAuto = "Auto";
    public const string ModeRawHid = "RawHid";
    public const string ModeSharpHook = "SharpHook";

    private readonly ILayerSource? _hid;
    private readonly ILayerSource? _hotkey;
    private string _mode;
    private ILayerSource? _active;
    private bool _started;

    public LayerSourceCoordinator(ILayerSource? hid, ILayerSource? hotkey, string initialMode)
    {
        _hid = hid;
        _hotkey = hotkey;
        _mode = NormalizeMode(initialMode);
        if (_hid is not null) _hid.ConnectionChanged += OnSourceConnectionChanged;
        if (_hotkey is not null) _hotkey.ConnectionChanged += OnSourceConnectionChanged;
    }

    /// <summary>Raised on any source's thread when the active source emits a layer change.</summary>
    public event Action<int>? ActiveLayerChanged;

    /// <summary>Raised on the HID thread when the active source reports a physical key event. Only fires when the HID source is active.</summary>
    public event Action<int, bool>? ActiveKeyPositionEvent;

    /// <summary>Raised on the source's thread whenever the *identity* of the active source flips, or its connection state changes.</summary>
    public event Action? ActiveSourceChanged;

    /// <summary>The active source's name with a "(searching)" suffix when it isn't currently connected. Empty when no source is configured.</summary>
    public string ActiveSourceLabel { get; private set; } = "";

    /// <summary>True when the active source is the HID one — used to gate the pink "untrackable" overlays.</summary>
    public bool IsHidActive => _active is not null && ReferenceEquals(_active, _hid);

    public string Mode => _mode;

    public void Start()
    {
        if (_started) return;
        _started = true;
        ApplyModeToSources();
        ReevaluateActive(initialResync: false);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _hid?.Stop();
        _hotkey?.Stop();
        DetachActive();
    }

    public void Dispose()
    {
        Stop();
        if (_hid is not null) _hid.ConnectionChanged -= OnSourceConnectionChanged;
        if (_hotkey is not null) _hotkey.ConnectionChanged -= OnSourceConnectionChanged;
        _hid?.Dispose();
        _hotkey?.Dispose();
    }

    /// <summary>
    /// Tell the HID source which keyboard the user has selected. The HID
    /// source uses this to ignore reports from any *other* connected ZMK
    /// device (both Moergo boards share VID:PID, so without this filter a
    /// Go60 plugged in next to a Glove80 would feed Go60 layer indices into
    /// the Glove80 layout). Safe to call any time; closes and re-discovers
    /// in the background.
    /// </summary>
    public void SetActiveProfile(IKeyboardProfile? profile)
    {
        switch (_hid)
        {
#if WINDOWS
            case WindowsHidCompositeLayerSource composite: composite.SetProfile(profile); break;
#endif
            case RawHidLayerSource win: win.SetProfile(profile); break;
            case MacRawHidLayerSource mac: mac.SetProfile(profile); break;
            case LinuxRawHidLayerSource linux: linux.SetProfile(profile); break;
        }
    }

    /// <summary>Switch the user-selected mode (Auto / RawHid / SharpHook). Idempotent.</summary>
    public void SetMode(string mode)
    {
        var n = NormalizeMode(mode);
        if (n == _mode) return;
        _mode = n;
        if (_started)
        {
            ApplyModeToSources();
            ReevaluateActive(initialResync: true);
        }
    }

    private void ApplyModeToSources()
    {
        switch (_mode)
        {
            case ModeRawHid:
                _hotkey?.Stop();
                _hid?.Start();
                break;
            case ModeSharpHook:
                _hid?.Stop();
                _hotkey?.Start();
                break;
            default: // Auto
                _hid?.Start();
                _hotkey?.Start();
                break;
        }
    }

    private void OnSourceConnectionChanged() => ReevaluateActive(initialResync: true);

    private void ReevaluateActive(bool initialResync)
    {
        ILayerSource? next = _mode switch
        {
            ModeRawHid => _hid,
            ModeSharpHook => _hotkey,
            _ => (_hid?.IsConnected == true) ? _hid : _hotkey,
        };

        bool changed = !ReferenceEquals(next, _active);
        if (changed)
        {
            DetachActive();
            _active = next;
            if (_active is not null)
            {
                _active.LayerChanged += ForwardLayer;
                _active.KeyPositionEvent += ForwardKey;
            }
            DiagnosticLog.Info("LayerSrc",
                $"Active source → {_active?.SourceName ?? "(none)"} (connected={_active?.IsConnected ?? false}, mode={_mode})");
        }

        var newLabel = BuildLabel(_active);
        bool labelChanged = newLabel != ActiveSourceLabel;
        ActiveSourceLabel = newLabel;

        if (changed || labelChanged)
            ActiveSourceChanged?.Invoke();

        // Resync the displayed layer to whatever the (newly) active source
        // believes is current. Without this, a HID disconnect leaves the UI
        // showing the last HID-reported layer until the user presses a key.
        if (changed && initialResync && _active is not null)
            ActiveLayerChanged?.Invoke(_active.CurrentLayer);
    }

    private void DetachActive()
    {
        if (_active is null) return;
        _active.LayerChanged -= ForwardLayer;
        _active.KeyPositionEvent -= ForwardKey;
        _active = null;
    }

    private void ForwardLayer(int layer) => ActiveLayerChanged?.Invoke(layer);
    private void ForwardKey(int pos, bool pressed) => ActiveKeyPositionEvent?.Invoke(pos, pressed);

    private static string BuildLabel(ILayerSource? src)
    {
        if (src is null) return "";
        return src.IsConnected ? src.SourceName : $"{src.SourceName} (searching)";
    }

    private static string NormalizeMode(string? mode) => mode switch
    {
        ModeRawHid => ModeRawHid,
        ModeSharpHook => ModeSharpHook,
        _ => ModeAuto,
    };
}
