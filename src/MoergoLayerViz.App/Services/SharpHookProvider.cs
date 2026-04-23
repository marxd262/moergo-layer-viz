using MoergoLayerViz.Core.Diagnostics;
using SharpHook;

namespace MoergoLayerViz.App.Services;

/// <summary>
/// Process-wide owner of the single <see cref="SimpleGlobalHook"/>. libuiohook
/// (SharpHook's native backend) is a process-global singleton on macOS /
/// Windows — running two <c>SimpleGlobalHook</c> instances in the same
/// process causes the second <c>RunAsync()</c> to fault with a generic
/// <c>Failure (1)</c>. Both <see cref="GlobalHotkeyService"/> and
/// <see cref="SharpHookKeyEventSource"/> now subscribe to this provider
/// instead of each creating their own hook.
/// </summary>
public sealed class SharpHookProvider : IDisposable
{
    private readonly object _gate = new();
    private SimpleGlobalHook? _hook;
    private int _subscribers;

    public event EventHandler<KeyboardHookEventArgs>? KeyPressed;
    public event EventHandler<KeyboardHookEventArgs>? KeyReleased;

    /// <summary>Raised on a threadpool thread when the underlying hook faults.</summary>
    public event Action<Exception>? HookFailed;

    /// <summary>
    /// Called by each subscriber. The first call starts the underlying hook;
    /// subsequent calls are no-ops. Pair with <see cref="Release"/>.
    /// </summary>
    public void Acquire()
    {
        lock (_gate)
        {
            _subscribers++;
            if (_hook is not null) return;

            _hook = new SimpleGlobalHook();
            _hook.KeyPressed += OnKeyPressed;
            _hook.KeyReleased += OnKeyReleased;
            var runTask = _hook.RunAsync();
            runTask.ContinueWith(t =>
            {
                var ex = t.Exception?.GetBaseException() ?? new Exception("Global hook stopped unexpectedly");
                DiagnosticLog.Error("SharpHookProvider", $"Global hook faulted: {ex.Message}");
                HookFailed?.Invoke(ex);
            }, TaskContinuationOptions.OnlyOnFaulted);
            DiagnosticLog.Info("SharpHookProvider", "Global hook started");
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_subscribers == 0) return;
            _subscribers--;
            if (_subscribers > 0 || _hook is null) return;

            _hook.KeyPressed -= OnKeyPressed;
            _hook.KeyReleased -= OnKeyReleased;
            _hook.Dispose();
            _hook = null;
            DiagnosticLog.Info("SharpHookProvider", "Global hook stopped");
        }
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e) => KeyPressed?.Invoke(sender, e);
    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e) => KeyReleased?.Invoke(sender, e);

    public void Dispose()
    {
        lock (_gate)
        {
            _hook?.Dispose();
            _hook = null;
            _subscribers = 0;
        }
    }
}
