#if WINDOWS
using MoergoLayerViz.Core.Input;
using MoergoLayerViz.Core.Layout;

namespace MoergoLayerViz.App.Services;

/// <summary>
/// Aggregates the HidSharp-based USB raw HID source with the WinRT GATT-based
/// BLE raw HID source on Windows, exposing them to the rest of the app as a
/// single <see cref="ILayerSource"/>.
///
/// <para>The two transports are mutually invisible to each other: HidSharp
/// (Windows HID class GUID) doesn't see ZMK's vendor-defined FF60/61
/// collection over BLE because the Windows HoGP driver strips it, and
/// <see cref="WindowsBleRawHidLayerSource"/> only enumerates BLE GATT
/// services. Running both in parallel and forwarding from whichever currently
/// has a connection means the user doesn't have to know which transport their
/// keyboard is on.</para>
/// </summary>
internal sealed class WindowsHidCompositeLayerSource : ILayerSource
{
    private readonly RawHidLayerSource _usb;
    private readonly WindowsBleRawHidLayerSource _ble;
    private volatile int _currentLayer;
    private string _sourceName = "Raw HID";

    public WindowsHidCompositeLayerSource(IKeyboardProfile? profile)
    {
        _usb = new RawHidLayerSource(profile);
        _ble = new WindowsBleRawHidLayerSource(profile);

        _usb.LayerChanged += OnChildLayer;
        _usb.KeyPositionEvent += OnChildKey;
        _usb.ConnectionChanged += OnChildConnectionChanged;
        _ble.LayerChanged += OnChildLayer;
        _ble.KeyPositionEvent += OnChildKey;
        _ble.ConnectionChanged += OnChildConnectionChanged;
    }

    public event Action<int>? LayerChanged;
    public event Action<int, bool>? KeyPositionEvent;
    public event Action? ConnectionChanged;

    public bool IsConnected => _usb.IsConnected || _ble.IsConnected;
    public int CurrentLayer => _currentLayer;
    public string SourceName => _sourceName;

    public void SetProfile(IKeyboardProfile? profile)
    {
        _usb.SetProfile(profile);
        _ble.SetProfile(profile);
    }

    public void Start()
    {
        _usb.Start();
        _ble.Start();
    }

    public void Stop()
    {
        _usb.Stop();
        _ble.Stop();
    }

    public void Dispose()
    {
        Stop();
        _usb.Dispose();
        _ble.Dispose();
    }

    private void OnChildLayer(int layer)
    {
        _currentLayer = layer;
        LayerChanged?.Invoke(layer);
    }

    private void OnChildKey(int position, bool pressed) =>
        KeyPositionEvent?.Invoke(position, pressed);

    private void OnChildConnectionChanged()
    {
        // BLE wins when both transports are live (USB stays plugged in for
        // charging while the keyboard talks BLE). Falling back to USB only
        // when BLE isn't connected matches the user's real-world workflow.
        _sourceName = _ble.IsConnected ? _ble.SourceName
            : _usb.IsConnected ? _usb.SourceName
            : "Raw HID";
        ConnectionChanged?.Invoke();
    }
}
#endif
