using MoergoLayerViz.Core.Input;
using MoergoLayerViz.Core.Keymap;
using Xunit;

namespace MoergoLayerViz.Tests;

/// <summary>
/// Regressions for bugs surfaced in the 2026-04-27 hostile review:
/// (1) SignalMacroScanner mis-classifying fire-once macros as momentary when
/// an unrelated <c>&amp;macro_pause_for_release</c> appears later in the
/// bindings; (2) HotkeyLayerTracker corrupting the held-layer stack under
/// concurrent hook-thread events and UI-thread <c>UpdateTable</c> calls.
/// </summary>
public class HostileReviewRegressionTests
{
    [Fact]
    public void SignalMacroScanner_PressPhase_NotMomentary_WhenPauseForReleaseIsInLaterPhase()
    {
        // Press phase: &macro_press → &mo → &kp → &macro_release.
        // The scan should END at &macro_release (the next phase marker).
        // The dangling &macro_pause_for_release after that belongs to a
        // separate later phase and must NOT promote this macro to momentary.
        const string json = """
        {
          "keyboard": "go60",
          "layer_names": ["Base", "Sym"],
          "layers": [
            [ { "value": "&weird" } ],
            [ { "value": "&trans" } ]
          ],
          "macros": [
            {
              "name": "&weird",
              "description": "",
              "bindings": [
                { "value": "&macro_press" },
                { "value": "&mo", "params": [ { "value": 1 } ] },
                { "value": "&kp", "params": [ { "value": "F20" } ] },
                { "value": "&macro_release" },
                { "value": "&kp", "params": [ { "value": "F20" } ] },
                { "value": "&macro_pause_for_release" }
              ],
              "params": []
            }
          ]
        }
        """;

        var config = MoergoJsonLoader.LoadFromJson(json);
        var signals = SignalMacroScanner.DetectSignalMacros(config);

        var sig = Assert.Single(signals);
        Assert.False(sig.IsMomentary);
    }

    [Fact]
    public void SignalMacroScanner_PressPhase_IsMomentary_WhenPhaseEndsAtPauseForRelease()
    {
        // Canonical Moergo shape: &macro_press → &mo → &kp → &macro_pause_for_release.
        // endIdx points at &macro_pause_for_release ⇒ momentary.
        const string json = """
        {
          "keyboard": "go60",
          "layer_names": ["Base", "Sym"],
          "layers": [
            [ { "value": "&moHold" } ],
            [ { "value": "&trans" } ]
          ],
          "macros": [
            {
              "name": "&moHold",
              "description": "",
              "bindings": [
                { "value": "&macro_press" },
                { "value": "&mo", "params": [ { "value": 1 } ] },
                { "value": "&kp", "params": [ { "value": "F21" } ] },
                { "value": "&macro_pause_for_release" },
                { "value": "&kp", "params": [ { "value": "F21" } ] }
              ],
              "params": []
            }
          ]
        }
        """;

        var config = MoergoJsonLoader.LoadFromJson(json);
        var signals = SignalMacroScanner.DetectSignalMacros(config);

        var sig = Assert.Single(signals);
        Assert.True(sig.IsMomentary);
    }

    [Fact]
    public async Task HotkeyLayerTracker_ConcurrentKeyEventsAndUpdateTable_DoesNotThrow()
    {
        // Stress test: hook-thread KeyEvent floods (Pressed/Released for an
        // unmapped keycode) while a "UI thread" repeatedly swaps the signal
        // table. Pre-fix this could throw InvalidOperationException from a
        // partially-mutated Stack<int> or read torn _currentLayer values.
        var source = new FakeKeyEventSource();
        var emptyTable = new LayerSignalTable(new Dictionary<string, SignalKeyMapping>());
        var oneKeyTable = new LayerSignalTable(new Dictionary<string, SignalKeyMapping>
        {
            ["F20"] = new SignalKeyMapping("F20", 1, IsMomentary: true, SourceMacroName: "&t"),
        });

        using var tracker = new HotkeyLayerTracker(source, emptyTable);

        var stop = new CancellationTokenSource();
        Exception? failure = null;

        var floodTask = Task.Run(() =>
        {
            try
            {
                var rng = new Random(42);
                while (!stop.IsCancellationRequested)
                {
                    var kind = rng.Next(2) == 0 ? KeyEventKind.Pressed : KeyEventKind.Released;
                    source.Fire(new KeyEvent("F20", kind));
                }
            }
            catch (Exception ex) { failure = ex; }
        });

        var swapTask = Task.Run(() =>
        {
            try
            {
                var toggle = false;
                while (!stop.IsCancellationRequested)
                {
                    tracker.UpdateTable(toggle ? oneKeyTable : emptyTable);
                    toggle = !toggle;
                }
            }
            catch (Exception ex) { failure = ex; }
        });

        await Task.Delay(250);
        stop.Cancel();
        await Task.WhenAll(floodTask, swapTask).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(failure);
        // Reset must always converge to base layer regardless of held state.
        tracker.Reset();
        Assert.Equal(0, tracker.CurrentLayer);
    }

    [Fact]
    public void HotkeyLayerTracker_AutoRepeatPressEvents_DoNotStackHold()
    {
        // macOS auto-repeat fires multiple Pressed events without intervening
        // Released. The tracker must dedupe so a single Released returns to
        // base layer.
        var source = new FakeKeyEventSource();
        var table = new LayerSignalTable(new Dictionary<string, SignalKeyMapping>
        {
            ["F20"] = new SignalKeyMapping("F20", 2, IsMomentary: true, SourceMacroName: "&hold"),
        });
        using var tracker = new HotkeyLayerTracker(source, table);

        source.Fire(new KeyEvent("F20", KeyEventKind.Pressed));
        source.Fire(new KeyEvent("F20", KeyEventKind.Pressed));
        source.Fire(new KeyEvent("F20", KeyEventKind.Pressed));
        Assert.Equal(2, tracker.CurrentLayer);

        source.Fire(new KeyEvent("F20", KeyEventKind.Released));
        Assert.Equal(0, tracker.CurrentLayer);
    }

    [Fact]
    public void HotkeyLayerTracker_UpdateTable_PreservesHeldKeyAcrossRemap()
    {
        // User holds F20 (mapped to layer 1), then the layout JSON reloads and
        // F20 now maps to layer 2 in the new table. The physical key is still
        // down — current layer should follow the new mapping, and releasing
        // F20 must still pop back to base.
        var source = new FakeKeyEventSource();
        var tableA = new LayerSignalTable(new Dictionary<string, SignalKeyMapping>
        {
            ["F20"] = new SignalKeyMapping("F20", 1, IsMomentary: true, SourceMacroName: "&a"),
        });
        var tableB = new LayerSignalTable(new Dictionary<string, SignalKeyMapping>
        {
            ["F20"] = new SignalKeyMapping("F20", 2, IsMomentary: true, SourceMacroName: "&b"),
        });
        using var tracker = new HotkeyLayerTracker(source, tableA);

        source.Fire(new KeyEvent("F20", KeyEventKind.Pressed));
        Assert.Equal(1, tracker.CurrentLayer);

        tracker.UpdateTable(tableB);
        Assert.Equal(2, tracker.CurrentLayer);

        source.Fire(new KeyEvent("F20", KeyEventKind.Released));
        Assert.Equal(0, tracker.CurrentLayer);
    }

    [Fact]
    public void HotkeyLayerTracker_UpdateTable_DropsHeldKeyWhenUnmappedInNewTable()
    {
        // Held F20 → layer 1. New table has no F20 mapping. The held entry
        // is dropped, layer collapses to base, and the eventual Release is
        // a no-op (no stuck-layer state).
        var source = new FakeKeyEventSource();
        var tableA = new LayerSignalTable(new Dictionary<string, SignalKeyMapping>
        {
            ["F20"] = new SignalKeyMapping("F20", 1, IsMomentary: true, SourceMacroName: "&a"),
        });
        var emptyTable = new LayerSignalTable(new Dictionary<string, SignalKeyMapping>());
        using var tracker = new HotkeyLayerTracker(source, tableA);

        source.Fire(new KeyEvent("F20", KeyEventKind.Pressed));
        Assert.Equal(1, tracker.CurrentLayer);

        tracker.UpdateTable(emptyTable);
        Assert.Equal(0, tracker.CurrentLayer);

        source.Fire(new KeyEvent("F20", KeyEventKind.Released));
        Assert.Equal(0, tracker.CurrentLayer);
    }

    private sealed class FakeKeyEventSource : IKeyEventSource
    {
        public event Action<KeyEvent>? KeyEvent;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void Fire(KeyEvent ev) => KeyEvent?.Invoke(ev);
    }
}
