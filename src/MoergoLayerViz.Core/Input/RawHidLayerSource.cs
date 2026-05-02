using HidSharp;
using MoergoLayerViz.Core.Diagnostics;
using MoergoLayerViz.Core.Layout;

namespace MoergoLayerViz.Core.Input;

/// <summary>
/// Raw-HID layer source for **Windows**. Discovers any HID device exposing
/// usage page 0xFF60 / usage 0x61 (the convention used by the
/// <c>zmk-raw-hid</c> + <c>zmk-keypeek-layer-notifier</c> modules), opens
/// it, and parses 32-byte layer-state and key-event reports off a
/// background thread.
///
/// <para>HidSharp's macOS backend only enumerates legacy IOKit USB HID and
/// misses BLE-HoGP devices, so macOS uses
/// <see cref="MacRawHidLayerSource"/> instead. This type is selected for
/// Windows where SetupDi enumerates BLE and USB HID uniformly via the HID
/// class GUID.</para>
///
/// <para>Hot-plug: subscribes to <see cref="DeviceList.Local"/> changes so a
/// device appearing or disappearing wakes the read loop without waiting out
/// the reconnect backoff. All event invocations happen on the read thread
/// — subscribers must marshal to UI as needed.</para>
/// </summary>
public sealed class RawHidLayerSource : ILayerSource
{
    private const int ReconnectDelayMs = 2000;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private HidStream? _stream;
    private string _sourceName = "Raw HID";
    private volatile bool _connected;
    private volatile int _currentLayer;
    private readonly ManualResetEventSlim _rescan = new(false);
    // Volatile so the read thread sees profile swaps without locking.
    private volatile IKeyboardProfile? _profile;

    public RawHidLayerSource(IKeyboardProfile? profile = null)
    {
        _profile = profile;
    }

    /// <summary>
    /// Replaces the profile filter. The currently-open stream (if any) is
    /// closed so the read loop falls back to discovery and applies the new
    /// filter immediately. Called by the coordinator when the user picks a
    /// different keyboard.
    /// </summary>
    public void SetProfile(IKeyboardProfile? profile)
    {
        _profile = profile;
        try { _stream?.Close(); } catch { }
        _rescan.Set();
    }

    public event Action<int>? LayerChanged;
    public event Action<int, bool>? KeyPositionEvent;
    public event Action? ConnectionChanged;

    public bool IsConnected => _connected;
    public string SourceName => _sourceName;
    public int CurrentLayer => _currentLayer;

    public void Start()
    {
        if (_runTask is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        DeviceList.Local.Changed += OnDeviceListChanged;
        _runTask = Task.Run(() => RunLoop(ct));
        DiagnosticLog.Info("RawHid", "RawHidLayerSource started");
    }

    public void Stop()
    {
        if (_runTask is null) return;
        DeviceList.Local.Changed -= OnDeviceListChanged;
        try { _cts?.Cancel(); } catch { }
        try { _stream?.Close(); } catch { }
        _stream = null;
        try { _runTask.Wait(1000); } catch { }
        _runTask = null;
        _cts?.Dispose();
        _cts = null;
        SetConnected(false);
    }

    public void Dispose()
    {
        Stop();
        _rescan.Dispose();
    }

    private void OnDeviceListChanged(object? sender, DeviceListChangedEventArgs e)
    {
        _rescan.Set();
    }

    private void RunLoop(CancellationToken ct)
    {
        // +1 leaves room for the report-ID byte that some platforms prepend.
        var buffer = new byte[RawHidProtocol.ReportSize + 1];

        while (!ct.IsCancellationRequested)
        {
            HidDevice? device = TryFindDevice();
            if (device is null)
            {
                _rescan.Reset();
                try { _rescan.Wait(ReconnectDelayMs, ct); } catch (OperationCanceledException) { return; }
                continue;
            }

            HidStream? stream = null;
            try
            {
                stream = device.Open();
                _stream = stream;
                _stream.ReadTimeout = Timeout.Infinite;

                string? productName = null;
                try { productName = device.GetProductName(); } catch { /* not all devices support */ }
                _sourceName = string.IsNullOrWhiteSpace(productName)
                    ? "Raw HID"
                    : $"Raw HID ({productName})";
                DiagnosticLog.Info("RawHid", $"Connected: {_sourceName}");
                SetConnected(true);

                while (!ct.IsCancellationRequested)
                {
                    int n;
                    try
                    {
                        n = _stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Debug("RawHid", $"Read failed: {ex.Message}");
                        break;
                    }
                    if (n <= 0) continue;

                    // Strip the leading report-ID byte when the platform
                    // prepended one (n == ReportSize + 1). When the device has
                    // no report ID, n == ReportSize and the payload starts
                    // at offset 0.
                    int offset = (n == RawHidProtocol.ReportSize + 1) ? 1 : 0;
                    DispatchReport(buffer.AsSpan(offset, n - offset));
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn("RawHid", $"Open/read error: {ex.Message}");
            }
            finally
            {
                try { stream?.Close(); } catch { }
                _stream = null;
                if (_connected)
                {
                    SetConnected(false);
                    DiagnosticLog.Info("RawHid", "Disconnected");
                }
            }

            // Brief backoff before next discovery; rescan event short-circuits.
            _rescan.Reset();
            try { _rescan.Wait(ReconnectDelayMs, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void DispatchReport(ReadOnlySpan<byte> payload)
    {
        var layer = RawHidProtocol.TryParseLayerState(payload);
        if (layer is int l)
        {
            _currentLayer = l;
            LayerChanged?.Invoke(l);
            return;
        }
        var key = RawHidProtocol.TryParseKeyEvent(payload);
        if (key is { } k)
        {
            KeyPositionEvent?.Invoke(k.Position, k.Pressed);
        }
    }

    private void SetConnected(bool value)
    {
        if (_connected == value) return;
        _connected = value;
        ConnectionChanged?.Invoke();
    }

    private HidDevice? TryFindDevice()
    {
        IEnumerable<HidDevice> devices;
        try { devices = DeviceList.Local.GetHidDevices(); }
        catch (Exception ex)
        {
            DiagnosticLog.Debug("RawHid", $"GetHidDevices failed: {ex.Message}");
            return null;
        }

        var profile = _profile;
        foreach (var d in devices)
        {
            // Cheap filters first: VID/PID + product-name prefix via the
            // profile. Skips the expensive GetReportDescriptor() call for
            // every unrelated HID device on the host.
            if (profile is not null)
            {
                string? name = null;
                try { name = d.GetProductName(); } catch { /* not all devices expose a name */ }
                if (!profile.MatchesHidDevice(d.VendorID, d.ProductID, name))
                    continue;
            }
            if (HasRawHidUsage(d)) return d;
        }
        return null;
    }

    private static bool HasRawHidUsage(HidDevice device)
    {
        try
        {
            var desc = device.GetReportDescriptor();
            foreach (var item in desc.DeviceItems)
            {
                foreach (var usage in item.Usages.GetAllValues())
                {
                    uint page = (usage >> 16) & 0xFFFF;
                    uint id = usage & 0xFFFF;
                    if (page == RawHidProtocol.UsagePage && id == RawHidProtocol.UsageId)
                        return true;
                }
            }
        }
        catch
        {
            // Many HID devices refuse descriptor reads (permission denied,
            // exclusive-access, plain unsupported on the platform). These are
            // not our device — skip silently.
        }
        return false;
    }
}
