using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MoergoLayerViz.Core.Diagnostics;

namespace MoergoLayerViz.App.Views;

public partial class MainWindow : Window
{
    private bool? _barsOnBottom;
    private const double ResizeEdge = 6;

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
            Background = Avalonia.Media.SolidColorBrush.Parse("#1E1E2E");
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

        // Swap inner order: status bar always at the outer edge
        if (shouldBeBottom)
        {
            // Bottom: tabs first (row 0), status bar at very bottom (row 1)
            Grid.SetRow(LayerTabs, 0);
            Grid.SetRow(StatusBar, 1);
            StatusBar.CornerRadius = new CornerRadius(0, 0, 8, 8);
        }
        else
        {
            // Top: status bar at very top (row 0), tabs below (row 1)
            Grid.SetRow(StatusBar, 0);
            Grid.SetRow(LayerTabs, 1);
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
