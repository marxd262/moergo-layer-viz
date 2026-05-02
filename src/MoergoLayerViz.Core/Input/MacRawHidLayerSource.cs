using System.Runtime.InteropServices;
using MoergoLayerViz.Core.Diagnostics;
using MoergoLayerViz.Core.Layout;

namespace MoergoLayerViz.Core.Input;

/// <summary>
/// Raw-HID layer source for **macOS**. Uses Apple's IOHIDManager via direct
/// P/Invoke into <c>IOKit.framework</c> + <c>CoreFoundation.framework</c>.
///
/// <para>HidSharp's macOS backend traverses the legacy <c>IOHIDDevice</c>
/// IOKit class which only contains USB-attached HID devices. BLE-HoGP devices
/// land under <c>AppleUserHIDDevice</c> and are invisible to that path. The
/// IOHIDManager API enumerates both transports uniformly, so this source
/// covers USB *and* Bluetooth on macOS.</para>
///
/// <para>Threading model: a dedicated background thread owns a CFRunLoop;
/// the IOHIDManager is scheduled onto it, and matching/removal/input-report
/// callbacks fire on that thread. <see cref="LayerChanged"/>,
/// <see cref="KeyPositionEvent"/>, and <see cref="ConnectionChanged"/> are
/// raised on the same thread — subscribers must marshal to UI.</para>
///
/// <para>No special entitlements or TCC prompts are required: usage page
/// 0xFF60 is treated as a generic (non-keyboard) HID device, so Input
/// Monitoring is not involved.</para>
/// </summary>
public sealed class MacRawHidLayerSource : ILayerSource
{
    // --- IOHIDManager / CoreFoundation P/Invoke surface ---
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // CFRunLoop default mode + return codes we care about.
    private static readonly IntPtr kCFRunLoopDefaultMode = CFStringCreate("kCFRunLoopDefaultMode");
    private const uint kIOReturnSuccess = 0;
    private const uint kIOHIDOptionsTypeNone = 0;

    [DllImport(IOKit)] private static extern IntPtr IOHIDManagerCreate(IntPtr allocator, uint options);
    [DllImport(IOKit)] private static extern void IOHIDManagerSetDeviceMatching(IntPtr manager, IntPtr matchingDict);
    [DllImport(IOKit)] private static extern uint IOHIDManagerOpen(IntPtr manager, uint options);
    [DllImport(IOKit)] private static extern uint IOHIDManagerClose(IntPtr manager, uint options);
    [DllImport(IOKit)] private static extern void IOHIDManagerScheduleWithRunLoop(IntPtr manager, IntPtr runLoop, IntPtr mode);
    [DllImport(IOKit)] private static extern void IOHIDManagerUnscheduleFromRunLoop(IntPtr manager, IntPtr runLoop, IntPtr mode);
    [DllImport(IOKit)] private static extern void IOHIDManagerRegisterDeviceMatchingCallback(IntPtr manager, IOHIDDeviceCallback callback, IntPtr context);
    [DllImport(IOKit)] private static extern void IOHIDManagerRegisterDeviceRemovalCallback(IntPtr manager, IOHIDDeviceCallback callback, IntPtr context);

    [DllImport(IOKit)] private static extern uint IOHIDDeviceOpen(IntPtr device, uint options);
    [DllImport(IOKit)] private static extern uint IOHIDDeviceClose(IntPtr device, uint options);
    [DllImport(IOKit)] private static extern void IOHIDDeviceRegisterInputReportCallback(IntPtr device, IntPtr report, nint reportLength, IOHIDReportCallback callback, IntPtr context);
    [DllImport(IOKit)] private static extern IntPtr IOHIDDeviceGetProperty(IntPtr device, IntPtr key);
    [DllImport(IOKit)] private static extern IntPtr IOHIDManagerCopyDevices(IntPtr manager);

    [DllImport(CoreFoundation)] private static extern nint CFSetGetCount(IntPtr theSet);
    [DllImport(CoreFoundation)] private static extern void CFSetGetValues(IntPtr theSet, IntPtr[] values);

    [DllImport(CoreFoundation)] private static extern IntPtr CFRunLoopGetCurrent();
    [DllImport(CoreFoundation)] private static extern void CFRunLoopRun();
    [DllImport(CoreFoundation)] private static extern void CFRunLoopStop(IntPtr runLoop);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);
    [DllImport(CoreFoundation)] private static extern IntPtr CFNumberCreate(IntPtr alloc, int type, ref int value);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryCreate(IntPtr alloc, IntPtr[] keys, IntPtr[] values, nint numValues, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
    [DllImport(CoreFoundation)] private static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, nint bufferSize, uint encoding);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetLength(IntPtr theString);
    [DllImport(CoreFoundation)] private static extern bool CFNumberGetValue(IntPtr number, int type, out int value);

    private const int kCFNumberSInt32Type = 3;
    private const uint kCFStringEncodingUTF8 = 0x08000100;
    // CFDictionary callback symbols live in CoreFoundation; we look them up
    // once via dlsym since they're exported as data, not functions.
    private static readonly IntPtr kCFTypeDictionaryKeyCallBacks = DlsymCF("kCFTypeDictionaryKeyCallBacks");
    private static readonly IntPtr kCFTypeDictionaryValueCallBacks = DlsymCF("kCFTypeDictionaryValueCallBacks");

    [DllImport("libdl.dylib")] private static extern IntPtr dlopen(string path, int flags);
    [DllImport("libdl.dylib")] private static extern IntPtr dlsym(IntPtr handle, string symbol);
    private const int RTLD_NOW = 2;

    private static IntPtr DlsymCF(string symbol)
    {
        var h = dlopen(CoreFoundation, RTLD_NOW);
        return h == IntPtr.Zero ? IntPtr.Zero : dlsym(h, symbol);
    }

    private static IntPtr CFStringCreate(string s) =>
        CFStringCreateWithCString(IntPtr.Zero, s, kCFStringEncodingUTF8);

    // Callback delegate signatures from IOKit.
    private delegate void IOHIDDeviceCallback(IntPtr context, uint result, IntPtr sender, IntPtr device);
    private delegate void IOHIDReportCallback(IntPtr context, uint result, IntPtr sender, uint reportType, uint reportID, IntPtr report, nint reportLength);

    // --- ILayerSource state ---
    private volatile IKeyboardProfile? _profile;
    private volatile bool _connected;
    private volatile int _currentLayer;
    private string _sourceName = "Raw HID";
    private Thread? _thread;
    private IntPtr _runLoop;
    private IntPtr _manager;
    // Pinned delegates so the GC doesn't reclaim them while IOKit holds a
    // C function pointer to them. Lifetime = source's; cleared in Stop.
    private IOHIDDeviceCallback? _matchCb;
    private IOHIDDeviceCallback? _removalCb;
    private IOHIDReportCallback? _reportCb;
    // Open devices keyed by IOHIDDeviceRef. We track them so removal callback
    // can free per-device buffers. Buffer is the report-input scratch IOKit
    // writes into.
    private readonly Dictionary<IntPtr, IntPtr> _deviceBuffers = new();
    private readonly object _gate = new();

    public MacRawHidLayerSource(IKeyboardProfile? profile = null)
    {
        _profile = profile;
    }

    public event Action<int>? LayerChanged;
    public event Action<int, bool>? KeyPositionEvent;
    public event Action? ConnectionChanged;

    public bool IsConnected => _connected;
    public int CurrentLayer => _currentLayer;
    public string SourceName => _sourceName;

    /// <summary>
    /// Replaces the profile filter. The IOHIDManager match dictionary already
    /// scopes by usage page, so a profile change just updates which currently
    /// open devices are still considered ours; we close non-matching ones on
    /// the run-loop thread.
    /// </summary>
    public void SetProfile(IKeyboardProfile? profile)
    {
        _profile = profile;
        // Trigger a re-evaluation: walk open devices on the run-loop thread
        // and close any whose product name no longer matches. New matches
        // arrive via the matching callback as before.
        var rl = _runLoop;
        if (rl != IntPtr.Zero) PerformOnRunLoop(ReevaluateOpenDevices);
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(RunLoopThread) { IsBackground = true, Name = "MacRawHid" };
        _thread.Start();
        DiagnosticLog.Info("MacRawHid", "MacRawHidLayerSource started");
    }

    public void Stop()
    {
        var t = _thread;
        if (t is null) return;
        _thread = null;
        // Tell the run-loop thread to clean up + exit.
        if (_runLoop != IntPtr.Zero) CFRunLoopStop(_runLoop);
        try { t.Join(2000); } catch { }
        SetConnected(false);
    }

    public void Dispose() => Stop();

    private void RunLoopThread()
    {
        try
        {
            _runLoop = CFRunLoopGetCurrent();
            _manager = IOHIDManagerCreate(IntPtr.Zero, kIOHIDOptionsTypeNone);
            if (_manager == IntPtr.Zero)
            {
                DiagnosticLog.Error("MacRawHid", "IOHIDManagerCreate returned NULL");
                return;
            }

            // Match by HID *usage* page+id only. VID/PID would be redundant
            // (the 0xFF60/0x61 usage is unique to zmk-raw-hid in practice)
            // and excluding VID/PID lets the same code work if Moergo ever
            // ships a different VID for new boards. Product-name prefix is
            // applied after the device shows up via the profile filter.
            IntPtr matchDict = BuildUsageMatchDictionary(RawHidProtocol.UsagePage, RawHidProtocol.UsageId);
            IOHIDManagerSetDeviceMatching(_manager, matchDict);
            CFRelease(matchDict);

            _matchCb = OnDeviceMatching;
            _removalCb = OnDeviceRemoval;
            _reportCb = OnInputReport;
            IOHIDManagerRegisterDeviceMatchingCallback(_manager, _matchCb, IntPtr.Zero);
            IOHIDManagerRegisterDeviceRemovalCallback(_manager, _removalCb, IntPtr.Zero);
            IOHIDManagerScheduleWithRunLoop(_manager, _runLoop, kCFRunLoopDefaultMode);

            var openResult = IOHIDManagerOpen(_manager, kIOHIDOptionsTypeNone);
            if (openResult != kIOReturnSuccess)
            {
                DiagnosticLog.Warn("MacRawHid", $"IOHIDManagerOpen returned 0x{openResult:X8}");
                // Continue anyway — IOHIDManager will still deliver matching
                // events for devices we can later open per-device.
            }

            CFRunLoopRun();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("MacRawHid", $"Run loop thread crashed: {ex}");
        }
        finally
        {
            try
            {
                CleanupOpenDevices();
                if (_manager != IntPtr.Zero)
                {
                    IOHIDManagerUnscheduleFromRunLoop(_manager, _runLoop, kCFRunLoopDefaultMode);
                    IOHIDManagerClose(_manager, kIOHIDOptionsTypeNone);
                    CFRelease(_manager);
                    _manager = IntPtr.Zero;
                }
            }
            catch { /* shutdown — swallow */ }
            _runLoop = IntPtr.Zero;
            _matchCb = null;
            _removalCb = null;
            _reportCb = null;
        }
    }

    private static IntPtr BuildUsageMatchDictionary(int usagePage, int usage)
    {
        var pageKey = CFStringCreate("DeviceUsagePage");
        var usageKey = CFStringCreate("DeviceUsage");
        var pageVal = CFNumberCreate(IntPtr.Zero, kCFNumberSInt32Type, ref usagePage);
        var usageVal = CFNumberCreate(IntPtr.Zero, kCFNumberSInt32Type, ref usage);
        var keys = new[] { pageKey, usageKey };
        var values = new[] { pageVal, usageVal };
        var dict = CFDictionaryCreate(IntPtr.Zero, keys, values, 2,
            kCFTypeDictionaryKeyCallBacks, kCFTypeDictionaryValueCallBacks);
        CFRelease(pageKey); CFRelease(usageKey);
        CFRelease(pageVal); CFRelease(usageVal);
        return dict;
    }

    private void OnDeviceMatching(IntPtr context, uint result, IntPtr sender, IntPtr device)
    {
        try
        {
            int vid = ReadIntProperty(device, "VendorID");
            int pid = ReadIntProperty(device, "ProductID");
            string? product = ReadStringProperty(device, "Product");

            var profile = _profile;
            if (profile is not null && !profile.MatchesHidDevice(vid, pid, product))
            {
                DiagnosticLog.Debug("MacRawHid",
                    $"Skipping non-matching device VID={vid:X4} PID={pid:X4} Product='{product}'");
                return;
            }

            var openResult = IOHIDDeviceOpen(device, kIOHIDOptionsTypeNone);
            if (openResult != kIOReturnSuccess)
            {
                DiagnosticLog.Warn("MacRawHid",
                    $"IOHIDDeviceOpen failed (0x{openResult:X8}) for VID={vid:X4} PID={pid:X4} Product='{product}'");
                return;
            }

            // IOKit writes incoming reports into a buffer we own; one per device.
            var buffer = Marshal.AllocHGlobal(RawHidProtocol.ReportSize);
            lock (_gate) _deviceBuffers[device] = buffer;
            IOHIDDeviceRegisterInputReportCallback(device, buffer, RawHidProtocol.ReportSize, _reportCb!, device);

            _sourceName = string.IsNullOrWhiteSpace(product) ? "Raw HID" : $"Raw HID ({product})";
            DiagnosticLog.Info("MacRawHid", $"Connected: {_sourceName}");
            SetConnected(true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("MacRawHid", $"OnDeviceMatching threw: {ex}");
        }
    }

    private void OnDeviceRemoval(IntPtr context, uint result, IntPtr sender, IntPtr device)
    {
        try
        {
            ClosePerDevice(device);
            // IsConnected reflects "any of our devices is open".
            bool any;
            lock (_gate) any = _deviceBuffers.Count > 0;
            if (!any)
            {
                DiagnosticLog.Info("MacRawHid", "Disconnected");
                SetConnected(false);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("MacRawHid", $"OnDeviceRemoval threw: {ex}");
        }
    }

    // Reused per-callback to avoid allocating a 32-byte array per report.
    // The run loop is single-threaded, so no synchronization is needed.
    private readonly byte[] _reportScratch = new byte[64];

    private void OnInputReport(IntPtr context, uint result, IntPtr sender, uint reportType, uint reportID, IntPtr report, nint reportLength)
    {
        if (reportLength <= 0) return;
        try
        {
            int len = Math.Min((int)reportLength, _reportScratch.Length);
            Marshal.Copy(report, _reportScratch, 0, len);
            DispatchReport(new ReadOnlySpan<byte>(_reportScratch, 0, len));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("MacRawHid", $"OnInputReport: {ex.Message}");
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
        if (key is { } k) KeyPositionEvent?.Invoke(k.Position, k.Pressed);
    }

    private void ReevaluateOpenDevices()
    {
        var profile = _profile;
        IntPtr[] toClose;
        lock (_gate) toClose = _deviceBuffers.Keys.ToArray();
        foreach (var device in toClose)
        {
            int vid = ReadIntProperty(device, "VendorID");
            int pid = ReadIntProperty(device, "ProductID");
            string? product = ReadStringProperty(device, "Product");
            if (profile is not null && !profile.MatchesHidDevice(vid, pid, product))
            {
                DiagnosticLog.Info("MacRawHid",
                    $"Closing stale device after profile change: Product='{product}'");
                ClosePerDevice(device);
            }
        }

        // IOHIDManager only fires the matching callback once per device, so
        // a device we previously skipped (or closed under a different profile)
        // won't be re-offered when the profile changes back. Walk the currently
        // attached devices ourselves and run the open path for any new matches.
        if (_manager != IntPtr.Zero)
        {
            var set = IOHIDManagerCopyDevices(_manager);
            if (set != IntPtr.Zero)
            {
                try
                {
                    int count = (int)CFSetGetCount(set);
                    if (count > 0)
                    {
                        var devices = new IntPtr[count];
                        CFSetGetValues(set, devices);
                        foreach (var device in devices)
                        {
                            bool alreadyOpen;
                            lock (_gate) alreadyOpen = _deviceBuffers.ContainsKey(device);
                            if (!alreadyOpen)
                                OnDeviceMatching(IntPtr.Zero, kIOReturnSuccess, IntPtr.Zero, device);
                        }
                    }
                }
                finally { CFRelease(set); }
            }
        }

        bool any;
        lock (_gate) any = _deviceBuffers.Count > 0;
        if (!any) SetConnected(false);
    }

    private void ClosePerDevice(IntPtr device)
    {
        IntPtr buffer = IntPtr.Zero;
        lock (_gate)
        {
            if (_deviceBuffers.TryGetValue(device, out buffer))
                _deviceBuffers.Remove(device);
        }
        // Unregister callback by passing a null delegate isn't well-defined —
        // closing the device is enough; IOKit stops delivering reports.
        try { IOHIDDeviceClose(device, kIOHIDOptionsTypeNone); } catch { }
        if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
    }

    private void CleanupOpenDevices()
    {
        IntPtr[] all;
        lock (_gate) all = _deviceBuffers.Keys.ToArray();
        foreach (var d in all) ClosePerDevice(d);
    }

    private void SetConnected(bool value)
    {
        if (_connected == value) return;
        _connected = value;
        ConnectionChanged?.Invoke();
    }

    // CFRunLoop is single-threaded; to mutate IOKit state from outside the
    // loop we'd normally use CFRunLoopPerformBlock, but for our needs (rare
    // profile-change triggered close) just doing the work synchronously on
    // the calling thread is fine since the operations we touch (open/close,
    // dictionary mutation under _gate) are individually thread-safe.
    private static void PerformOnRunLoop(Action action) => action();

    private static int ReadIntProperty(IntPtr device, string key)
    {
        var keyRef = CFStringCreate(key);
        try
        {
            var val = IOHIDDeviceGetProperty(device, keyRef);
            if (val == IntPtr.Zero) return 0;
            return CFNumberGetValue(val, kCFNumberSInt32Type, out int n) ? n : 0;
        }
        finally { CFRelease(keyRef); }
    }

    private static string? ReadStringProperty(IntPtr device, string key)
    {
        var keyRef = CFStringCreate(key);
        try
        {
            var val = IOHIDDeviceGetProperty(device, keyRef);
            if (val == IntPtr.Zero) return null;
            // CFStringGetLength returns code-unit length; UTF-8 encode worst-case 4x.
            var len = (int)CFStringGetLength(val);
            if (len <= 0) return "";
            var buf = new byte[len * 4 + 1];
            return CFStringGetCString(val, buf, buf.Length, kCFStringEncodingUTF8)
                ? System.Text.Encoding.UTF8.GetString(buf, 0, Array.IndexOf(buf, (byte)0))
                : null;
        }
        finally { CFRelease(keyRef); }
    }
}
