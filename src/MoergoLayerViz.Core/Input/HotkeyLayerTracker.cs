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
    // Hook callbacks fire on the libuiohook thread; UpdateTable / Reset /
    // CurrentLayer reads come from the UI thread. _gate serializes every
    // access to _heldLayers / _currentLayer / _table. LayerChanged is raised
    // OUTSIDE the lock so a slow UI handler can't stall the hook thread or
    // deadlock through a re-entrant call back into the tracker.
    private readonly IKeyEventSource _source;
    private readonly object _gate = new();
    private LayerSignalTable _table;
    private readonly Stack<HeldEntry> _heldLayers = new();
    private int _currentLayer;

    /// <summary>
    /// One physically-held signal key. Keyed by keycode so a table swap
    /// can re-resolve the (still-held) key against the new mapping.
    /// </summary>
    private readonly record struct HeldEntry(string Keycode, int TargetLayer);

    /// <summary>Raised whenever the active layer changes. Marshaling to the UI thread is the subscriber's responsibility.</summary>
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
    public int CurrentLayer
    {
        get { lock (_gate) return _currentLayer; }
    }

    /// <summary>
    /// Swap the active signal table — e.g. after the user loads a different
    /// layout JSON. Held entries are re-resolved against the new table:
    /// keycodes still mapped as momentary keep the hold (with the new target
    /// layer); keycodes that become unmapped or non-momentary are dropped.
    /// </summary>
    public void UpdateTable(LayerSignalTable table)
    {
        int? newLayer = null;
        lock (_gate)
        {
            _table = table;

            // Stack<T>.Reverse() yields bottom→top so we rebuild in original
            // press order without re-allocating a list per swap.
            var preserved = _heldLayers.Reverse().ToArray();
            _heldLayers.Clear();
            foreach (var held in preserved)
            {
                var m = table.TryResolve(held.Keycode);
                if (m is null || !m.IsMomentary) continue;
                _heldLayers.Push(new HeldEntry(held.Keycode, m.TargetLayer));
            }

            var target = _heldLayers.Count > 0 ? _heldLayers.Peek().TargetLayer : 0;
            if (_currentLayer != target)
            {
                _currentLayer = target;
                newLayer = target;
            }
        }
        if (newLayer is int n)
        {
            DiagnosticLog.Debug("LayerTracker", $"Active layer → {n} (table swap)");
            LayerChanged?.Invoke(n);
        }
    }

    /// <summary>Reset to base layer (layer 0) and clear hold state.</summary>
    public void Reset()
    {
        bool changed;
        lock (_gate)
        {
            _heldLayers.Clear();
            changed = _currentLayer != 0;
            _currentLayer = 0;
        }
        if (changed)
        {
            DiagnosticLog.Debug("LayerTracker", "Active layer → 0");
            LayerChanged?.Invoke(0);
        }
    }

    private void OnKey(KeyEvent ev)
    {
        KeyObserved?.Invoke(ev);

        int? newLayer = null;

        lock (_gate)
        {
            var mapping = _table.TryResolve(ev.Keycode);
            if (mapping is null) return;

            if (mapping.IsMomentary)
            {
                if (ev.Kind == KeyEventKind.Pressed)
                {
                    // Guard against OS auto-repeat: macOS fires fresh Pressed
                    // events at ~10Hz while a key is held (no intervening
                    // Released). Without this check, each repeat pushes another
                    // copy onto the stack and only the first release pops — so
                    // the layer stays "stuck" until we receive as many releases
                    // as pushes (which never happens for a single physical press).
                    // Dedup by keycode: matches the physical key, so two
                    // distinct keys that target the same layer both push.
                    if (_heldLayers.Count == 0 || _heldLayers.Peek().Keycode != ev.Keycode)
                    {
                        _heldLayers.Push(new HeldEntry(ev.Keycode, mapping.TargetLayer));
                        if (_currentLayer != mapping.TargetLayer)
                        {
                            _currentLayer = mapping.TargetLayer;
                            newLayer = mapping.TargetLayer;
                        }
                    }
                }
                else // Released
                {
                    // Only pop if the top of the stack matches — guards against
                    // out-of-order release events (e.g. key-up delivered for a
                    // layer we already released).
                    if (_heldLayers.Count > 0 && _heldLayers.Peek().Keycode == ev.Keycode)
                    {
                        _heldLayers.Pop();
                        var target = _heldLayers.Count > 0 ? _heldLayers.Peek().TargetLayer : 0;
                        if (_currentLayer != target)
                        {
                            _currentLayer = target;
                            newLayer = target;
                        }
                    }
                }
            }
            else
            {
                // Non-momentary: only react on press, flip to the target layer.
                if (ev.Kind == KeyEventKind.Pressed && _currentLayer != mapping.TargetLayer)
                {
                    _currentLayer = mapping.TargetLayer;
                    newLayer = mapping.TargetLayer;
                }
            }
        }

        if (newLayer is int n)
        {
            DiagnosticLog.Debug("LayerTracker", $"Active layer → {n}");
            LayerChanged?.Invoke(n);
        }
    }

    public void Dispose()
    {
        _source.KeyEvent -= OnKey;
    }
}
