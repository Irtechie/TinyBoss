using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Runtime.Versioning;
using static TinyBoss.Platform.Windows.TilingCoordinator;

namespace TinyBoss.Platform.Windows;

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
    private Dictionary<int, string> _slotAliases = new();

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

    /// <summary>Fires when a slot is right-clicked for rename. Arg: slot index.</summary>
    public event Action<int>? RenameRequested;

    /// <summary>Set the monitor working area and position the overlay to fill it.</summary>
    public void SetMonitorBounds(RECT workArea, RECT monitorBounds)
    {
        _workArea = workArea;

        // Position uses physical pixels; Width/Height use logical (DIP) units
        Position = new PixelPoint(monitorBounds.Left, monitorBounds.Top);
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;
        Width = monitorBounds.Width / scale;
        Height = monitorBounds.Height / scale;
    }

    /// <summary>Update which grid size to display.</summary>
    public void SetGridSize(int gridSize)
    {
        _gridSize = TilingCoordinator.NormalizeGridSize(gridSize);
        Redraw();
    }

    /// <summary>Update which slots are already occupied.</summary>
    public void SetOccupiedSlots(HashSet<int> occupied, Dictionary<int, string>? aliases = null)
    {
        _occupiedSlots = occupied;
        _slotAliases = aliases ?? new();
        Redraw();
    }

    /// <summary>Highlight a specific zone (during drag). -1 = none.</summary>
    public void HighlightZone(int slot)
    {
        if (_highlightedSlot == slot) return;
        _highlightedSlot = slot;
        Redraw();
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
}
