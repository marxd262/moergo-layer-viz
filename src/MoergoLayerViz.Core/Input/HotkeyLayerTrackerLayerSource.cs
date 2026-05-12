using ZmkHidProtocol.Transport;

namespace MoergoLayerViz.Core.Input;

/// <summary>
/// Adapts the existing <see cref="HotkeyLayerTracker"/> (SharpHook + signal
/// macros) to the <see cref="ILayerSource"/> contract. This wrapper does not
/// own the tracker — it just forwards its events — so the tracker's lifetime
/// stays managed by the caller (MainWindowViewModel) and table swaps via
/// <see cref="HotkeyLayerTracker.UpdateTable"/> continue to work unchanged.
/// </summary>
public sealed class HotkeyLayerTrackerLayerSource : ILayerSource
{
    private readonly HotkeyLayerTracker _tracker;
    private bool _started;

    public HotkeyLayerTrackerLayerSource(HotkeyLayerTracker tracker)
    {
        _tracker = tracker;
        _tracker.LayerChanged += ForwardLayerChanged;
    }

    public event Action<int>? LayerChanged;

    /// <summary>Always null — SharpHook reports keycodes, not matrix positions.</summary>
    public event Action<int, bool>? KeyPositionEvent { add { } remove { } }

#pragma warning disable CS0067 // Synthetic source never reads raw HID reports.
    public event Action<ReadOnlyMemory<byte>>? ReportReceived;
#pragma warning restore CS0067

    public event Action? ConnectionChanged;

    public bool IsConnected => _started;

    public string SourceName => "SharpHook";

    public int CurrentLayer => _tracker.CurrentLayer;

    // SharpHook is keyboard-agnostic; the device matcher is irrelevant here.
    public void SetMatcher(IDeviceMatcher? matcher) { }

    public void Start()
    {
        if (_started) return;
        _started = true;
        ConnectionChanged?.Invoke();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        ConnectionChanged?.Invoke();
    }

    private void ForwardLayerChanged(int layer) => LayerChanged?.Invoke(layer);

    public void Dispose()
    {
        _tracker.LayerChanged -= ForwardLayerChanged;
    }
}
