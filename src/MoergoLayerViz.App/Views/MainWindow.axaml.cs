using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MoergoLayerViz.App.Services;
using MoergoLayerViz.App.ViewModels;
using MoergoLayerViz.Core.Diagnostics;

namespace MoergoLayerViz.App.Views;

public partial class MainWindow : Window
{
    private bool? _barsOnBottom;
    private static readonly double ResizeEdge = OperatingSystem.IsWindows() ? 9 : 6;

    public MainWindow()
    {
        InitializeComponent();
        PositionChanged += OnPositionChanged;
        Opened += (_, _) =>
        {
            DiagnosticLog.Info("UI", "MainWindow.Opened event fired");
            ApplyTransparencyFallback();
            UpdateBarPosition();
        };

        if (OperatingSystem.IsMacOS())
            SystemDecorations = SystemDecorations.Full;

        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// Validates that window transparency is actually working and falls back gracefully.
    /// On some GPU configurations (especially NVIDIA on Windows), Avalonia reports transparency
    /// as available but the compositing doesn't render correctly, producing an invisible window.
    /// </summary>
    private void ApplyTransparencyFallback()
    {
        DiagnosticLog.Info("UI", $"Transparency: ActualTransparencyLevel={ActualTransparencyLevel}");

        if (ActualTransparencyLevel == WindowTransparencyLevel.None)
        {
            // Platform explicitly says no transparency — use solid background with system chrome.
            DiagnosticLog.Warn("UI", "Transparency: fallback to solid background + system decorations");
            Background = Avalonia.Media.SolidColorBrush.Parse(AppTheme.BgBaseHex);
            SystemDecorations = SystemDecorations.Full;
            ExtendClientAreaToDecorationsHint = false;
            return;
        }

        // On Windows, some NVIDIA drivers report Transparent as supported but the compositor
        // doesn't actually render the surface. A near-transparent background (alpha=1/255)
        // is invisible to humans but prevents the GPU from discarding the surface entirely.
        if (ActualTransparencyLevel == WindowTransparencyLevel.Transparent &&
            OperatingSystem.IsWindows())
        {
            DiagnosticLog.Info("UI", "Transparency: applied near-transparent safety background (Windows + Transparent)");
            Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromArgb(1, 0, 0, 0));

            // Kill the DWM-drawn border line that Windows renders around borderless
            // windows; without this the transparent body has clearly defined edges.
            // DWMWA_BORDER_COLOR=COLOR_NONE is Windows 11+; silently ignored on Win10.
            TryDisableDwmBorder();
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);

    private void TryDisableDwmBorder()
    {
        const int DWMWA_BORDER_COLOR = 34;
        const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            DiagnosticLog.Warn("UI", "DWM border: no HWND available, skipping");
            return;
        }

        try
        {
            uint colorNone = DWMWA_COLOR_NONE;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(uint));
            DiagnosticLog.Info("UI", $"DWM border: BORDER_COLOR=NONE hr=0x{hr:X8}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("UI", $"DWM border: P/Invoke failed: {ex.Message}");
        }
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => UpdateBarPosition();

    private void UpdateBarPosition()
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen is null) return;

        var windowMidY = Position.Y + (Height / 2);
        var screenMidY = screen.WorkingArea.Height / 2;
        var shouldBeBottom = windowMidY > screenMidY;

        if (shouldBeBottom == _barsOnBottom) return;
        _barsOnBottom = shouldBeBottom;

        // Move the whole bars panel to top or bottom of the outer grid
        Grid.SetRow(BarsPanel, shouldBeBottom ? 2 : 0);

        // Swap inner order: status bar always at the outer edge, combo legend
        // always at the inner edge (touching the board), layer tabs sandwiched.
        if (shouldBeBottom)
        {
            // Bottom: legend (row 0, touches board above), tabs (row 1), status (row 2).
            Grid.SetRow(ComboLegend, 0);
            Grid.SetRow(LayerTabs, 1);
            Grid.SetRow(StatusBar, 2);
            StatusBar.CornerRadius = new CornerRadius(0, 0, 8, 8);
        }
        else
        {
            // Top: status (row 0), tabs (row 1), legend (row 2, touches board below).
            Grid.SetRow(StatusBar, 0);
            Grid.SetRow(LayerTabs, 1);
            Grid.SetRow(ComboLegend, 2);
            StatusBar.CornerRadius = new CornerRadius(8, 8, 0, 0);
        }
    }

    /// <summary>
    /// Handles window drag (bars area) and resize (edges).
    /// SystemDecorations="None" removes OS title bar and resize handles — we replace both here.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Handled) return;

        var pos = e.GetPosition(this);

        var edge = GetResizeEdge(pos);
        if (edge.HasValue) { BeginResizeDrag(edge.Value, e); return; }

        // Only drag from the bars background, not from interactive controls
        var source = e.Source as Visual;
        while (source != null && source != this)
        {
            if (source is Button) return;
            source = source.GetVisualParent();
        }

        var barsHeight = BarsPanel.Bounds.Height;
        bool inBars = _barsOnBottom == true
            ? pos.Y > Bounds.Height - barsHeight
            : pos.Y < barsHeight;

        if (inBars)
            BeginMoveDrag(e);
    }

    /// <summary>
    /// Updates the cursor as the pointer enters / leaves a resize zone so the
    /// otherwise-invisible 6–9 px hot zones are discoverable. Suppressed when
    /// the OS draws its own chrome (macOS, Windows transparency fallback).
    /// </summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (SystemDecorations == SystemDecorations.Full) return;

        var edge = GetResizeEdge(e.GetPosition(this));
        Cursor = edge switch
        {
            WindowEdge.North     or WindowEdge.South     => new Cursor(StandardCursorType.SizeNorthSouth),
            WindowEdge.West      or WindowEdge.East      => new Cursor(StandardCursorType.SizeWestEast),
            WindowEdge.NorthWest                         => new Cursor(StandardCursorType.TopLeftCorner),
            WindowEdge.NorthEast                         => new Cursor(StandardCursorType.TopRightCorner),
            WindowEdge.SouthWest                         => new Cursor(StandardCursorType.BottomLeftCorner),
            WindowEdge.SouthEast                         => new Cursor(StandardCursorType.BottomRightCorner),
            _                                            => Cursor.Default,
        };
    }

    /// <summary>
    /// Hover on a combo tile in the legend → highlight the matching combo
    /// across the legend tile, every participating-key pill, and each
    /// participating key's press dot. Inert if the data context isn't the
    /// main VM (defensive — there's no other VM that owns this view).
    /// </summary>
    private void OnComboTilePointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c) return;
        if (c.DataContext is not ComboViewModel combo) return;
        if (DataContext is not MainWindowViewModel vm) return;
        vm.ComboOverlay.HoverCombo(combo.Number);
    }

    private void OnComboTilePointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.ComboOverlay.HoverCombo(null);
    }

    /// <summary>
    /// Left-click on a legend tile toggles a sticky selection — the highlight
    /// persists past pointer-exit so the user can study the participating keys
    /// without keeping the mouse pinned on the tile. Right-click opens the
    /// label-override flyout (wired via <c>Button.ContextFlyout</c>).
    /// </summary>
    private void OnComboTileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c) return;
        if (c.DataContext is not ComboViewModel combo) return;
        if (DataContext is not MainWindowViewModel vm) return;
        vm.ComboOverlay.ToggleStickyCombo(combo.Number);
    }

    private WindowEdge? GetResizeEdge(Point pos)
    {
        // `<` on the near edge and `>=` on the far edge keeps both hot zones
        // exactly ResizeEdge pixels wide. Using `>` symmetrically would leave
        // a 1px dead band at pos == Bounds.Width - ResizeEdge.
        bool left   = pos.X < ResizeEdge;
        bool right  = pos.X >= Bounds.Width - ResizeEdge;
        bool top    = pos.Y < ResizeEdge;
        bool bottom = pos.Y >= Bounds.Height - ResizeEdge;

        if (left  && top)    return WindowEdge.NorthWest;
        if (right && top)    return WindowEdge.NorthEast;
        if (left  && bottom) return WindowEdge.SouthWest;
        if (right && bottom) return WindowEdge.SouthEast;
        if (left)            return WindowEdge.West;
        if (right)           return WindowEdge.East;
        if (top)             return WindowEdge.North;
        if (bottom)          return WindowEdge.South;
        return null;
    }
}
