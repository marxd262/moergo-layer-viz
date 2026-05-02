namespace MoergoLayerViz.Core.Input;

/// <summary>
/// A source that reports the keyboard's currently active layer. Two
/// implementations exist: one driven by SharpHook + signal-macro keycodes
/// (<see cref="HotkeyLayerTrackerLayerSource"/>) and one driven by Raw HID
/// reports from a modified ZMK firmware
/// (<c>RawHidLayerSource</c> in the same namespace).
///
/// <para>Implementations may invoke events on any thread. Subscribers that
/// touch UI state must marshal to their own UI thread.</para>
/// </summary>
public interface ILayerSource : IDisposable
{
    /// <summary>Raised whenever the active layer changes (highest-set-bit semantics for HID).</summary>
    event Action<int>? LayerChanged;

    /// <summary>
    /// Raised on physical key press / release events when the source can
    /// report them by matrix position. Only the Raw HID source raises this;
    /// the SharpHook adapter leaves it null. <c>position</c> is the firmware's
    /// matrix index (matches Moergo's row-major JSON binding order on the
    /// supported boards).
    /// </summary>
    event Action<int, bool>? KeyPositionEvent;

    /// <summary>True when the source is currently delivering events (e.g. HID device is open).</summary>
    bool IsConnected { get; }

    /// <summary>
    /// The layer the source last reported (or 0 if it has reported nothing
    /// yet). Used by <c>LayerSourceCoordinator</c> to resync the displayed
    /// layer immediately when a source change happens — without it the UI
    /// would stay stale until the next press.
    /// </summary>
    int CurrentLayer { get; }

    /// <summary>Short human-readable name shown in the status bar / diagnostics.</summary>
    string SourceName { get; }

    /// <summary>Raised whenever <see cref="IsConnected"/> changes.</summary>
    event Action? ConnectionChanged;

    /// <summary>Start delivering events (idempotent).</summary>
    void Start();

    /// <summary>Stop delivering events but keep the object usable for a subsequent Start().</summary>
    void Stop();
}
