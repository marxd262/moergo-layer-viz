using MoergoLayerViz.Core.Diagnostics;
using MoergoLayerViz.Core.Keymap;

namespace MoergoLayerViz.Core.Input;

/// <summary>
/// Tracks the currently active layer by observing the OS-visible signal
/// keycodes emitted by the Moergo's user-defined layer-switch macros.
/// <para>
/// For momentary macros (<c>&amp;macro_pause_for_release</c>): the layer is
/// active only while the key is physically held. The base layer (0) is
/// restored on key-up.
/// </para>
/// <para>
/// For non-momentary macros (no pause-for-release): each press toggles to
/// the target layer; subsequent presses of other signal keys switch again.
/// </para>
/// </summary>
public sealed class HotkeyLayerTracker : IDisposable
{
    private readonly IKeyEventSource _source;
    private LayerSignalTable _table;
    private readonly Stack<int> _heldLayers = new();
    private int _currentLayer;

    /// <summary>Raised on the hook thread whenever the active layer changes.</summary>
    public event Action<int>? LayerChanged;

    /// <summary>Raised on the hook thread for every observed key event (for live-highlight UI).</summary>
    public event Action<KeyEvent>? KeyObserved;

    public HotkeyLayerTracker(IKeyEventSource source, LayerSignalTable table)
    {
        _source = source;
        _table = table;
        _currentLayer = 0;
        _source.KeyEvent += OnKey;
    }

    /// <summary>Which layer is currently active (0 = base).</summary>
    public int CurrentLayer => _currentLayer;

    /// <summary>
    /// Swap the active signal table — e.g. after the user loads a different
    /// layout JSON. Clears any held-layer state so the stack doesn't get
    /// stuck on a keycode that no longer maps.
    /// </summary>
    public void UpdateTable(LayerSignalTable table)
    {
        _table = table;
        _heldLayers.Clear();
        SetCurrent(0);
    }

    /// <summary>Reset to base layer (layer 0) and clear hold state. Thread-safe via simple assignments.</summary>
    public void Reset()
    {
        _heldLayers.Clear();
        SetCurrent(0);
    }

    private void OnKey(KeyEvent ev)
    {
        KeyObserved?.Invoke(ev);

        var mapping = _table.TryResolve(ev.Keycode);
        if (mapping is null) return;

        if (mapping.IsMomentary)
        {
            if (ev.Kind == KeyEventKind.Pressed)
            {
                _heldLayers.Push(mapping.TargetLayer);
                SetCurrent(mapping.TargetLayer);
            }
            else // Released
            {
                // Only pop if the top of the stack matches — guards against
                // out-of-order release events (e.g. key-up delivered for a
                // layer we already released).
                if (_heldLayers.Count > 0 && _heldLayers.Peek() == mapping.TargetLayer)
                {
                    _heldLayers.Pop();
                    SetCurrent(_heldLayers.Count > 0 ? _heldLayers.Peek() : 0);
                }
            }
        }
        else
        {
            // Non-momentary: only react on press, flip to the target layer.
            if (ev.Kind == KeyEventKind.Pressed)
            {
                SetCurrent(mapping.TargetLayer);
            }
        }
    }

    private void SetCurrent(int layer)
    {
        if (_currentLayer == layer) return;
        _currentLayer = layer;
        DiagnosticLog.Debug("LayerTracker", $"Active layer → {layer}");
        LayerChanged?.Invoke(layer);
    }

    public void Dispose()
    {
        _source.KeyEvent -= OnKey;
    }
}
