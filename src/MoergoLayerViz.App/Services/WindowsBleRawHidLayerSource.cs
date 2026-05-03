#if WINDOWS
using System.Runtime.Versioning;
using MoergoLayerViz.Core.Diagnostics;
using MoergoLayerViz.Core.Input;
using MoergoLayerViz.Core.Layout;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace MoergoLayerViz.App.Services;

/// <summary>
/// Raw-HID layer source for **Windows over Bluetooth LE** via direct WinRT
/// GATT calls.
///
/// <para><b>Currently dormant on Windows.</b> The HoGP kernel driver claims
/// the standard HID GATT service (UUID 0x1812) exclusively and refuses even
/// <see cref="GattSharingMode.SharedReadAndWrite"/> opens with
/// <c>AccessDenied</c>. Empirically verified on Windows 10.0.26100; matches
/// reports in bleak #599 and the Nordic DevZone "HID Service access denied"
/// thread. There is no user-mode escape hatch on Windows for 0x1812.</para>
///
/// <para>The fix has to come from the firmware: <c>zzeneg/zmk-raw-hid</c>
/// currently registers its raw HID reports inside <c>BT_UUID_HIDS</c>
/// (i.e. 0x1812). Exposing them in parallel under a custom vendor service
/// UUID would side-step HoGP entirely, and this source would then need only
/// a UUID-constant swap to come back to life.</para>
///
/// <para>For now the loop probes once: it enumerates a matching device, opens
/// the HID service, and on the expected <c>AccessDenied</c> logs a single
/// explanatory warning and exits permanently. USB Raw HID still works through
/// <see cref="RawHidLayerSource"/>; SharpHook covers BLE in the meantime. The
/// composite source (<see cref="WindowsHidCompositeLayerSource"/>) keeps both
/// USB and this dormant BLE leg wired so that flipping the firmware UUID is a
/// one-line change here.</para>
///
/// <para>Approach when active: enumerate paired BLE devices, find ones matching
/// the active profile, open the HID GATT service, subscribe to notifications on
/// every Notify-capable Report characteristic (UUID 0x2A4D), and dispatch every
/// incoming payload through <see cref="RawHidProtocol"/>. The protocol parsers
/// reject non-FF60 reports by content (first byte != 0xFF / 0xF1), so
/// keyboard/consumer reports are filtered without us walking the Report Map to
/// identify the right Report ID — a deliberate v1 simplification.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WindowsBleRawHidLayerSource : ILayerSource
{
    // pid.codes shared VID/PID used by ZMK firmware. Mirrors the value in
    // Core's ZmkHidIds (which is internal to Core, hence the duplication).
    private const int ZmkVendorId = 0x16C0;
    private const int ZmkProductId = 0x27DB;

    private const int RescanDelayMs = 3000;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private volatile bool _connected;
    private volatile int _currentLayer;
    private volatile IKeyboardProfile? _profile;
    private string _sourceName = "Raw HID (BLE)";
    private readonly ManualResetEventSlim _rescan = new(false);

    public WindowsBleRawHidLayerSource(IKeyboardProfile? profile = null)
    {
        _profile = profile;
    }

    public event Action<int>? LayerChanged;
    public event Action<int, bool>? KeyPositionEvent;
    public event Action? ConnectionChanged;

    public bool IsConnected => _connected;
    public int CurrentLayer => _currentLayer;
    public string SourceName => _sourceName;

    public void SetProfile(IKeyboardProfile? profile)
    {
        _profile = profile;
        // Wake the discovery loop so it re-evaluates against the new filter.
        // Any active subscription is torn down inside RunLoop on the next
        // iteration; the rescan-event short-circuits the connected wait.
        _rescan.Set();
    }

    public void Start()
    {
        if (_runTask is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _runTask = Task.Run(() => RunLoop(ct));
        DiagnosticLog.Info("WinBleHid", "WindowsBleRawHidLayerSource started");
    }

    public void Stop()
    {
        if (_runTask is null) return;
        try { _cts?.Cancel(); } catch { }
        _rescan.Set();
        try { _runTask.Wait(2000); } catch { }
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

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            BluetoothLEDevice? device = null;
            GattDeviceService? hidService = null;
            var subscribed = new List<GattCharacteristic>();
            TypedEventHandler<BluetoothLEDevice, object>? onConn = null;

            try
            {
                device = await TryFindMatchingDevice(ct);
                if (device is null)
                {
                    await WaitForRescanOrTimeout(RescanDelayMs, ct);
                    continue;
                }

                var sr = await device.GetGattServicesForUuidAsync(
                    GattServiceUuids.HumanInterfaceDevice, BluetoothCacheMode.Uncached);
                if (sr.Status != GattCommunicationStatus.Success || sr.Services.Count == 0)
                {
                    DiagnosticLog.Info("WinBleHid",
                        $"Device '{device.Name}' has no HID GATT service (status={sr.Status}); skipping.");
                    await WaitForRescanOrTimeout(RescanDelayMs, ct);
                    continue;
                }
                hidService = sr.Services[0];

                // HoGP holds 0x1812 exclusively; SharedReadAndWrite is the WinRT
                // escape hatch for "another driver already owns this; let me
                // read alongside" but Windows refuses it for the HID service.
                // On AccessDenied we exit the loop permanently — retrying gives
                // the same answer every time and just spams the log. Any other
                // failure is treated as transient (rescan).
                var openStatus = await hidService.OpenAsync(GattSharingMode.SharedReadAndWrite);
                if (openStatus == GattOpenStatus.AccessDenied)
                {
                    DiagnosticLog.Warn("WinBleHid",
                        "Windows HoGP kernel driver refuses user-mode access to the BLE HID service " +
                        "(0x1812) — raw HID over BLE is unavailable on Windows. SharpHook fallback " +
                        "covers BLE; USB raw HID still works. This source is going dormant for the " +
                        "session. Tracking firmware-side fix: expose raw HID under a custom GATT " +
                        "service UUID in parallel with 0x1812.");
                    return;
                }
                if (openStatus != GattOpenStatus.Success && openStatus != GattOpenStatus.AlreadyOpened)
                {
                    DiagnosticLog.Warn("WinBleHid", $"hidService.OpenAsync → {openStatus}; will retry.");
                    await WaitForRescanOrTimeout(RescanDelayMs, ct);
                    continue;
                }

                var charsResult = await hidService.GetCharacteristicsForUuidAsync(
                    GattCharacteristicUuids.Report, BluetoothCacheMode.Uncached);
                if (charsResult.Status != GattCommunicationStatus.Success)
                {
                    DiagnosticLog.Warn("WinBleHid",
                        $"GetCharacteristics for Report failed: {charsResult.Status}");
                    await WaitForRescanOrTimeout(RescanDelayMs, ct);
                    continue;
                }

                foreach (var ch in charsResult.Characteristics)
                {
                    if (!ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)) continue;
                    var status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    if (status != GattCommunicationStatus.Success)
                    {
                        DiagnosticLog.Debug("WinBleHid",
                            $"CCCD write failed on a Report characteristic: {status}");
                        continue;
                    }
                    ch.ValueChanged += OnReportNotification;
                    subscribed.Add(ch);
                }

                if (subscribed.Count == 0)
                {
                    DiagnosticLog.Warn("WinBleHid",
                        $"Device '{device.Name}' exposes no Notify-capable Report characteristics; nothing to subscribe to.");
                    await WaitForRescanOrTimeout(RescanDelayMs, ct);
                    continue;
                }

                _sourceName = string.IsNullOrWhiteSpace(device.Name)
                    ? "Raw HID (BLE)"
                    : $"Raw HID ({device.Name}, BLE)";
                DiagnosticLog.Info("WinBleHid",
                    $"Connected: {_sourceName} — subscribed to {subscribed.Count} Report characteristic(s)");
                SetConnected(true);

                // Block until disconnect or cancel. WinRT exposes
                // ConnectionStatusChanged as the canonical disconnect signal;
                // we also bail on profile-change rescan so SetProfile takes
                // effect without waiting for a physical disconnect.
                var disconnected = new TaskCompletionSource();
                onConn = (s, _) =>
                {
                    if (s.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                        disconnected.TrySetResult();
                };
                device.ConnectionStatusChanged += onConn;

                while (!ct.IsCancellationRequested && !disconnected.Task.IsCompleted)
                {
                    _rescan.Reset();
                    var rescanTask = Task.Run(() => _rescan.Wait(ct));
                    var completed = await Task.WhenAny(disconnected.Task, rescanTask);
                    if (completed == rescanTask) break; // SetProfile or Stop
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                DiagnosticLog.Warn("WinBleHid", $"BLE loop error: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (onConn is not null && device is not null)
                    device.ConnectionStatusChanged -= onConn;
                foreach (var ch in subscribed)
                    ch.ValueChanged -= OnReportNotification;
                hidService?.Dispose();
                device?.Dispose();
                if (_connected)
                {
                    SetConnected(false);
                    DiagnosticLog.Info("WinBleHid", "Disconnected");
                }
            }

            await WaitForRescanOrTimeout(RescanDelayMs, ct);
        }
    }

    private async Task<BluetoothLEDevice?> TryFindMatchingDevice(CancellationToken ct)
    {
        var profile = _profile;
        DeviceInformationCollection devInfos;
        try
        {
            var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            devInfos = await DeviceInformation.FindAllAsync(selector);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Debug("WinBleHid", $"Paired-BLE enumeration failed: {ex.Message}");
            return null;
        }

        foreach (var info in devInfos)
        {
            if (ct.IsCancellationRequested) return null;
            BluetoothLEDevice? dev;
            try { dev = await BluetoothLEDevice.FromIdAsync(info.Id); }
            catch { dev = null; }
            if (dev is null) continue;

            // Profile filter: discriminate Go60 vs Glove80 by the BLE Name.
            // VID/PID are passed as the canonical ZMK pair so the profile's
            // first two checks pass; the productName check is what actually
            // discriminates the two boards.
            if (profile is not null && !profile.MatchesHidDevice(ZmkVendorId, ZmkProductId, dev.Name))
            {
                dev.Dispose();
                continue;
            }

            return dev;
        }

        return null;
    }

    private void OnReportNotification(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var buf = args.CharacteristicValue;
            if (buf is null || buf.Length == 0) return;
            var reader = DataReader.FromBuffer(buf);
            var bytes = new byte[buf.Length];
            reader.ReadBytes(bytes);

            // Multiple Report characteristics (keyboard / consumer / vendor)
            // are subscribed in parallel; RawHidProtocol's parsers reject
            // anything that isn't the FF60 layer-state (0xFF) or key-event
            // (0xF1) message by content, so we don't need a per-characteristic
            // Report ID match here.
            var layer = RawHidProtocol.TryParseLayerState(bytes);
            if (layer is int l)
            {
                _currentLayer = l;
                LayerChanged?.Invoke(l);
                return;
            }
            var key = RawHidProtocol.TryParseKeyEvent(bytes);
            if (key is { } k)
                KeyPositionEvent?.Invoke(k.Position, k.Pressed);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("WinBleHid", $"OnReportNotification: {ex.Message}");
        }
    }

    private async Task WaitForRescanOrTimeout(int ms, CancellationToken ct)
    {
        _rescan.Reset();
        try { await Task.Run(() => _rescan.Wait(ms, ct), ct); }
        catch (OperationCanceledException) { }
    }

    private void SetConnected(bool value)
    {
        if (_connected == value) return;
        _connected = value;
        ConnectionChanged?.Invoke();
    }
}

#endif
