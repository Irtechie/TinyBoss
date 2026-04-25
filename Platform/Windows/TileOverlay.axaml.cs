using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Runtime.Versioning;
using static KittenHerder.Platform.Windows.TilingCoordinator;

namespace KittenHerder.Platform.Windows;

/// <summary>
/// Transparent fullscreen overlay showing tiling grid zones.
/// This is a dumb visual — all interaction (Tab/Escape/drag) is driven externally.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class TileOverlay : Window
{
    private static readonly IBrush ZoneBrush = new SolidColorBrush(Color.FromArgb(40, 100, 149, 237));
    private static readonly IBrush ZoneBorderBrush = new SolidColorBrush(Color.FromArgb(180, 100, 149, 237));
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromArgb(80, 50, 205, 50));
    private static readonly IBrush OccupiedBrush = new SolidColorBrush(Color.FromArgb(60, 255, 165, 0));

    private readonly Canvas _canvas;
    private int _gridSize = 4;
    private int _highlightedSlot = -1;
    private HashSet<int> _occupiedSlots = new();

    // Monitor working area in physical pixels (set before showing)
    private RECT _workArea;

    public TileOverlay()
    {
        AvaloniaXamlLoader.Load(this);
        _canvas = this.FindControl<Canvas>("ZoneCanvas")!;

        // Click-through: background clicks dismiss
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                DismissRequested?.Invoke();
        };
    }

    /// <summary>Fires when the overlay should be dismissed (click outside zones).</summary>
    public event Action? DismissRequested;

    /// <summary>Set the monitor working area and position the overlay to fill it.</summary>
    public void SetMonitorBounds(RECT workArea, RECT monitorBounds)
    {
        _workArea = workArea;

        // Position uses physical pixels in Avalonia
        Position = new PixelPoint(monitorBounds.Left, monitorBounds.Top);
        Width = monitorBounds.Width;
        Height = monitorBounds.Height;
    }

    /// <summary>Update which grid size to display.</summary>
    public void SetGridSize(int gridSize)
    {
        _gridSize = gridSize switch { 2 => 2, 6 => 6, _ => 4 };
        Redraw();
    }

    /// <summary>Update which slots are already occupied.</summary>
    public void SetOccupiedSlots(HashSet<int> occupied)
    {
        _occupiedSlots = occupied;
        Redraw();
    }

    /// <summary>Highlight a specific zone (during drag). -1 = none.</summary>
    public void HighlightZone(int slot)
    {
        if (_highlightedSlot == slot) return;
        _highlightedSlot = slot;
        Redraw();
    }

    /// <summary>Cycle grid: 2→4→6→2.</summary>
    public int CycleGridSize()
    {
        _gridSize = _gridSize switch { 2 => 4, 4 => 6, _ => 2 };
        Redraw();
        return _gridSize;
    }

    private void Redraw()
    {
        _canvas.Children.Clear();

        var bounds = TilingCoordinator.GetPaneBounds(nint.Zero, _gridSize, _workArea);
        // Convert physical pixel pane bounds to overlay-local coordinates
        var overlayLeft = (int)Position.X;
        var overlayTop = (int)Position.Y;

        foreach (var (slot, rect) in bounds)
        {
            var localLeft = rect.Left - overlayLeft;
            var localTop = rect.Top - overlayTop;
            var w = rect.Width;
            var h = rect.Height;

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

            // Slot number label
            var label = new TextBlock
            {
                Text = (slot + 1).ToString(),
                FontSize = 48,
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
}
