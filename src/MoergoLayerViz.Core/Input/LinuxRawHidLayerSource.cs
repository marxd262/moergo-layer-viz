using System.Runtime.InteropServices;
using MoergoLayerViz.Core.Diagnostics;
using MoergoLayerViz.Core.Layout;

namespace MoergoLayerViz.Core.Input;

/// <summary>
/// Raw-HID layer source for **Linux**. Reads from <c>/dev/hidrawN</c> directly
/// — same transport-agnostic kernel interface that USB and Bluetooth HID
/// devices both expose.
///
/// <para>Discovery polls every <see cref="ReconnectDelayMs"/> milliseconds:
/// for each <c>/dev/hidraw*</c> node we ioctl <c>HIDIOCGRAWINFO</c> for
/// VID/PID, <c>HIDIOCGRAWNAME</c> for the product name (run through the
/// profile filter), and <c>HIDIOCGRDESC</c> for the report descriptor (which
/// we walk for the FF60/61 usage pair). Match → open + read loop.</para>
///
/// <para>Permissions: kernel hidraw nodes are root-by-default. ZMK's
/// upstream udev rules grant `0666` access to VID 0x16C0 / PID 0x27DB to
/// regular users; we surface a clear error in the log if open() returns
/// EACCES so the user knows what to fix.</para>
///
/// <para>Threading: a single background thread blocks on <c>read()</c> when
/// open and falls back to a polling loop for discovery. Events are raised
/// on that thread.</para>
/// </summary>
public sealed class LinuxRawHidLayerSource : ILayerSource
{
    private const int ReconnectDelayMs = 2000;
    private const int O_RDWR = 0x0002;
    private const int O_NONBLOCK = 0x0800;
    private const int EACCES = 13;
    private const int EAGAIN = 11;

    // ioctl request numbers — derived from the Linux <linux/hidraw.h> macros.
    // _IOR(type, nr, size) == (2 << 30) | (size << 16) | (type << 8) | nr
    // Values fixed for x86_64 / aarch64 (both use the asm-generic ioctl layout).
    private const int HID_MAX_DESCRIPTOR_SIZE = 4096;
    private const ulong HIDIOCGRDESCSIZE = (2UL << 30) | (4UL << 16) | ((ulong)'H' << 8) | 0x01;
    private const ulong HIDIOCGRDESC = (2UL << 30) | ((4UL + HID_MAX_DESCRIPTOR_SIZE) << 16) | ((ulong)'H' << 8) | 0x02;
    private const ulong HIDIOCGRAWINFO = (2UL << 30) | (8UL << 16) | ((ulong)'H' << 8) | 0x03;
    // HIDIOCGRAWNAME(len) — size baked in at the call site.
    private static ulong HIDIOCGRAWNAME(int len) =>
        (2UL << 30) | ((ulong)len << 16) | ((ulong)'H' << 8) | 0x04;

    [DllImport("libc", SetLastError = true)] private static extern int open(string pathname, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte[] buf, nuint count);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int ioctl(int fd, ulong request, IntPtr arg);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int ioctl(int fd, ulong request, ref int arg);

    [StructLayout(LayoutKind.Sequential)]
    private struct hidraw_devinfo
    {
        public uint bustype;
        public short vendor;
        public short product;
    }

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private volatile IKeyboardProfile? _profile;
    private volatile bool _connected;
    private volatile int _currentLayer;
    private string _sourceName = "Raw HID";
    private int _openFd = -1;
    // Wake the discovery loop on profile change instead of waiting out the
    // reconnect backoff.
    private readonly ManualResetEventSlim _rescan = new(false);

    public LinuxRawHidLayerSource(IKeyboardProfile? profile = null)
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
        // Force the read loop back into discovery so the new filter applies
        // immediately. Closing the fd makes the in-flight read() return -1.
        var fd = _openFd;
        if (fd >= 0) { try { close(fd); } catch { } }
        _rescan.Set();
    }

    public void Start()
    {
        if (_runTask is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _runTask = Task.Run(() => RunLoop(ct));
        DiagnosticLog.Info("LinuxRawHid", "LinuxRawHidLayerSource started");
    }

    public void Stop()
    {
        if (_runTask is null) return;
        try { _cts?.Cancel(); } catch { }
        var fd = _openFd;
        if (fd >= 0) { try { close(fd); } catch { } }
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

    private void RunLoop(CancellationToken ct)
    {
        var buffer = new byte[RawHidProtocol.ReportSize];
        while (!ct.IsCancellationRequested)
        {
            int fd = TryOpenMatchingDevice(out var product);
            if (fd < 0)
            {
                _rescan.Reset();
                try { _rescan.Wait(ReconnectDelayMs, ct); } catch (OperationCanceledException) { return; }
                continue;
            }

            _openFd = fd;
            _sourceName = string.IsNullOrWhiteSpace(product) ? "Raw HID" : $"Raw HID ({product})";
            DiagnosticLog.Info("LinuxRawHid", $"Connected: {_sourceName}");
            SetConnected(true);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    nint n = read(fd, buffer, (nuint)buffer.Length);
                    if (n <= 0)
                    {
                        int err = Marshal.GetLastPInvokeError();
                        if (err == EAGAIN) continue; // shouldn't happen on blocking fd, defensive
                        DiagnosticLog.Debug("LinuxRawHid", $"read() returned {n} (errno={err}) — closing");
                        break;
                    }
                    DispatchReport(new ReadOnlySpan<byte>(buffer, 0, (int)n));
                }
            }
            finally
            {
                _openFd = -1;
                try { close(fd); } catch { }
                if (_connected)
                {
                    DiagnosticLog.Info("LinuxRawHid", "Disconnected");
                    SetConnected(false);
                }
            }

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
        if (key is { } k) KeyPositionEvent?.Invoke(k.Position, k.Pressed);
    }

    private int TryOpenMatchingDevice(out string? product)
    {
        product = null;
        string[] nodes;
        try { nodes = Directory.GetFiles("/dev", "hidraw*"); }
        catch (Exception ex)
        {
            DiagnosticLog.Debug("LinuxRawHid", $"Enumerate /dev/hidraw* failed: {ex.Message}");
            return -1;
        }

        var profile = _profile;
        bool sawAccessDenied = false;
        foreach (var path in nodes)
        {
            int fd = open(path, O_RDWR);
            if (fd < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == EACCES) sawAccessDenied = true;
                continue;
            }

            try
            {
                if (!TryReadDevInfo(fd, out var info)) continue;
                var name = TryReadProductName(fd);
                if (profile is not null && !profile.MatchesHidDevice(info.vendor & 0xFFFF, info.product & 0xFFFF, name))
                    continue;
                if (!HasRawHidUsage(fd)) continue;

                product = name;
                int taken = fd;
                fd = -1; // prevent close in finally
                return taken;
            }
            finally
            {
                if (fd >= 0) try { close(fd); } catch { }
            }
        }

        if (sawAccessDenied)
            DiagnosticLog.Warn("LinuxRawHid",
                "Permission denied on one or more /dev/hidraw* nodes — install ZMK udev rules to grant user access (see https://zmk.dev/docs/development/setup/linux).");
        return -1;
    }

    private static bool TryReadDevInfo(int fd, out hidraw_devinfo info)
    {
        info = default;
        var size = Marshal.SizeOf<hidraw_devinfo>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (ioctl(fd, HIDIOCGRAWINFO, buf) < 0) return false;
            info = Marshal.PtrToStructure<hidraw_devinfo>(buf);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string? TryReadProductName(int fd)
    {
        const int len = 256;
        var buf = Marshal.AllocHGlobal(len);
        try
        {
            if (ioctl(fd, HIDIOCGRAWNAME(len), buf) < 0) return null;
            return Marshal.PtrToStringUTF8(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool HasRawHidUsage(int fd)
    {
        int descSize = 0;
        if (ioctl(fd, HIDIOCGRDESCSIZE, ref descSize) < 0) return false;
        if (descSize <= 0 || descSize > HID_MAX_DESCRIPTOR_SIZE) return false;

        // hidraw_report_descriptor layout: u32 size; u8 value[HID_MAX_DESCRIPTOR_SIZE]
        int totalBytes = 4 + HID_MAX_DESCRIPTOR_SIZE;
        var buf = Marshal.AllocHGlobal(totalBytes);
        try
        {
            Marshal.WriteInt32(buf, descSize);
            if (ioctl(fd, HIDIOCGRDESC, buf) < 0) return false;

            var bytes = new byte[descSize];
            Marshal.Copy(IntPtr.Add(buf, 4), bytes, 0, descSize);
            return DescriptorMatchesUsage(bytes, RawHidProtocol.UsagePage, RawHidProtocol.UsageId);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// Minimal HID report-descriptor walker: scans top-level Usage Page
    /// (0x05 / 0x06) and Usage (0x09 / 0x0A) items for the (page, usage)
    /// pair we care about. Doesn't fully parse the descriptor — we only
    /// need to know whether the requested usage appears anywhere.
    /// </summary>
    internal static bool DescriptorMatchesUsage(ReadOnlySpan<byte> descriptor, int targetPage, int targetUsage)
    {
        int currentPage = -1;
        int i = 0;
        while (i < descriptor.Length)
        {
            byte prefix = descriptor[i++];
            // Long item — rare in practice; skip safely.
            if (prefix == 0xFE)
            {
                if (i + 1 >= descriptor.Length) return false;
                int dataSize = descriptor[i];
                i += 2 + dataSize;
                continue;
            }

            int sizeCode = prefix & 0x03;
            int dataLen = sizeCode == 3 ? 4 : sizeCode;
            int tag = prefix & 0xFC;
            if (i + dataLen > descriptor.Length) return false;

            int data = 0;
            for (int b = 0; b < dataLen; b++)
                data |= descriptor[i + b] << (8 * b);
            i += dataLen;

            // Usage Page: 0x04 (short) or 0x05 (... actually 0x04 is the tag
            // bits without size; the real prefix bytes are 0x05/0x06/0x07).
            // We mask the size code, so tag values become 0x04 (Usage Page),
            // 0x08 (Usage local item), regardless of payload width.
            if (tag == 0x04) currentPage = data & 0xFFFF;
            else if (tag == 0x08 && currentPage == targetPage && (data & 0xFFFF) == targetUsage) return true;
        }
        return false;
    }

    private void SetConnected(bool value)
    {
        if (_connected == value) return;
        _connected = value;
        ConnectionChanged?.Invoke();
    }
}
