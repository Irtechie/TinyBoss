using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static TinyBoss.Platform.Windows.TilingCoordinator;

namespace TinyBoss.Platform.Windows;

/// <summary>
/// Transparent fullscreen overlay showing tiling grid zones.
/// Made click-through (WS_EX_TRANSPARENT) during drag so it doesn't steal focus
/// or interrupt the Win32 drag operation.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class TileOverlay : Window
{
    private static readonly IBrush ZoneBrush = new SolidColorBrush(Color.FromArgb(40, 100, 149, 237));
    private static readonly IBrush ZoneBorderBrush = new SolidColorBrush(Color.FromArgb(180, 100, 149, 237));
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromArgb(80, 50, 205, 50));
    private static readonly IBrush OccupiedBrush = new SolidColorBrush(Color.FromArgb(60, 255, 165, 0));
    private static readonly IBrush DockStripBrush = new SolidColorBrush(Color.FromArgb(200, 40, 40, 40));
    private static readonly IBrush DockStripBorder = new SolidColorBrush(Color.FromArgb(180, 100, 149, 237));

    private readonly Canvas _canvas;
    private int _gridSize = 4;
    private int _highlightedSlot = -1;
    private HashSet<int> _occupiedSlots = new();
    private Dictionary<int, string> _slotAliases = new();
    private bool _isDockStripMode = true;

    // Monitor working area in physical pixels (set before showing)
    private RECT _workArea;
    private RECT _monitorBounds;

    // Win32 extended style constants
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    public TileOverlay()
    {
        AvaloniaXamlLoader.Load(this);
        _canvas = this.FindControl<Canvas>("ZoneCanvas")!;

        // Click-through: background clicks dismiss (only when NOT in dock strip mode)
        PointerPressed += (_, e) =>
        {
            if (!_isDockStripMode && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                DismissRequested?.Invoke();
        };

        // Make the overlay non-activating after it's shown
        Opened += OnOverlayOpened;
    }

    private void OnOverlayOpened(object? sender, EventArgs e)
    {
        MakeClickThrough();
    }

    /// <summary>
    /// Sets WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW on this window
    /// so it doesn't interfere with Win32 drag operations.
    /// </summary>
    private void MakeClickThrough()
    {
        if (TryGetPlatformHandle() is { } handle)
        {
            var hwnd = handle.Handle;
            var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle);
        }
    }

    /// <summary>
    /// Removes WS_EX_TRANSPARENT so the overlay accepts input (for right-click rename, dismiss).
    /// Call when the overlay should be interactive (hotkey-triggered, not drag-triggered).
    /// </summary>
    public void MakeInteractive()
    {
        _isDockStripMode = false;
        if (TryGetPlatformHandle() is { } handle)
        {
            var hwnd = handle.Handle;
            var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            exStyle &= ~WS_EX_TRANSPARENT;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle);
        }
    }

    /// <summary>Fires when the overlay should be dismissed (click outside zones).</summary>
    public event Action? DismissRequested;

    /// <summary>Fires when a slot is right-clicked for rename. Arg: slot index.</summary>
    public event Action<int>? RenameRequested;

    /// <summary>Set the monitor working area and position the overlay to fill it.</summary>
    public void SetMonitorBounds(RECT workArea, RECT monitorBounds)
    {
        _workArea = workArea;
        _monitorBounds = monitorBounds;

        // Position uses physical pixels; Width/Height use logical (DIP) units
        Position = new PixelPoint(monitorBounds.Left, monitorBounds.Top);
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;
        Width = monitorBounds.Width / scale;
        Height = monitorBounds.Height / scale;
    }

    /// <summary>Start in dock strip mode — thin bar at top with TinyBoss icon.</summary>
    public void ShowAsDockStrip()
    {
        _isDockStripMode = true;
        DrawDockStrip();
    }

    /// <summary>Expand from dock strip to full overlay grid.</summary>
    public void ExpandToFullOverlay()
    {
        _isDockStripMode = false;
        Redraw();
    }

    /// <summary>Update which grid size to display.</summary>
    public void SetGridSize(int gridSize)
    {
        _gridSize = TilingCoordinator.NormalizeGridSize(gridSize);
        if (!_isDockStripMode)
            Redraw();
    }

    /// <summary>Update which slots are already occupied.</summary>
    public void SetOccupiedSlots(HashSet<int> occupied, Dictionary<int, string>? aliases = null)
    {
        _occupiedSlots = occupied;
        _slotAliases = aliases ?? new();
        if (!_isDockStripMode)
            Redraw();
    }

    /// <summary>Highlight a specific zone (during drag). -1 = none.</summary>
    public void HighlightZone(int slot)
    {
        if (_highlightedSlot == slot) return;
        _highlightedSlot = slot;
        if (!_isDockStripMode)
            Redraw();
    }

    /// <summary>Whether currently in dock strip mode (thin bar at top).</summary>
    public bool IsDockStripMode => _isDockStripMode;

    private void DrawDockStrip()
    {
        _canvas.Children.Clear();
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;
        var monW = _monitorBounds.Width / scale;

        // Dock strip: thin centered bar at top
        double stripW = Math.Min(200, monW * 0.15);
        double stripH = 48;
        double stripLeft = (monW - stripW) / 2;
        double stripTop = 8;

        var strip = new Rectangle
        {
            Width = stripW,
            Height = stripH,
            Fill = DockStripBrush,
            Stroke = DockStripBorder,
            StrokeThickness = 2,
            RadiusX = 12,
            RadiusY = 12,
        };
        Canvas.SetLeft(strip, stripLeft);
        Canvas.SetTop(strip, stripTop);
        _canvas.Children.Add(strip);

        // TinyBoss icon/label
        var label = new TextBlock
        {
            Text = "⊞ TinyBoss",
            FontSize = 16,
            Foreground = Brushes.White,
            Opacity = 0.9,
            FontWeight = FontWeight.SemiBold,
        };
        Canvas.SetLeft(label, stripLeft + stripW / 2 - 48);
        Canvas.SetTop(label, stripTop + 12);
        _canvas.Children.Add(label);
    }

    private void Redraw()
    {
        _canvas.Children.Clear();

        var bounds = TilingCoordinator.GetPaneBounds(nint.Zero, _gridSize, _workArea);
        // Physical pixel overlay origin
        var overlayLeft = (int)Position.X;
        var overlayTop = (int)Position.Y;
        // DPI scale: physical pixels → logical (DIP) for Avalonia Canvas
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;

        foreach (var (slot, rect) in bounds)
        {
            var localLeft = (rect.Left - overlayLeft) / scale;
            var localTop = (rect.Top - overlayTop) / scale;
            var w = rect.Width / scale;
            var h = rect.Height / scale;

            IBrush fill;
            if (slot == _highlightedSlot)
                fill = HighlightBrush;
            else if (_occupiedSlots.Contains(slot))
                fill = OccupiedBrush;
            else
                fill = ZoneBrush;

            var zone = new Rectangle
            {
                Width = w - 4,
                Height = h - 4,
                Fill = fill,
                Stroke = ZoneBorderBrush,
                StrokeThickness = 2,
                RadiusX = 8,
                RadiusY = 8,
            };

            Canvas.SetLeft(zone, localLeft + 2);
            Canvas.SetTop(zone, localTop + 2);
            _canvas.Children.Add(zone);

            // Right-click occupied zones to rename
            if (_occupiedSlots.Contains(slot))
            {
                var capturedSlot = slot;
                zone.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(zone).Properties.IsRightButtonPressed)
                    {
                        e.Handled = true;
                        RenameRequested?.Invoke(capturedSlot);
                    }
                };
            }

            // Slot number label (show alias if available)
            var labelText = _slotAliases.TryGetValue(slot, out var alias) && !string.IsNullOrEmpty(alias)
                ? alias : (slot + 1).ToString();
            var fontSize = labelText.Length > 3 ? 24.0 : 48.0;
            var label = new TextBlock
            {
                Text = labelText,
                FontSize = fontSize,
                Foreground = Brushes.White,
                Opacity = 0.5,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Canvas.SetLeft(label, localLeft + w / 2 - 16);
            Canvas.SetTop(label, localTop + h / 2 - 28);
            _canvas.Children.Add(label);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}
