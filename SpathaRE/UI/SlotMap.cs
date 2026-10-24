using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SpathaRE.Core.Devices;
using Path = System.Windows.Shapes.Path;

namespace SpathaRE.UI;

/// <summary>Maps art regions (original Spatha slot numbers) to a model's own slots.</summary>
public static class SlotMapLayout
{
    // Spatha X: 0-7 standard (6/7 = wheel up/down), 8-13 = side buttons 1-6.
    // The side grid on the art reads 1-2-3 / 4-5-6 = art slots 10-8-6 / 11-9-7.
    private static readonly Dictionary<int, int> SpathaX = new()
    {
        { 0, 0 }, { 1, 1 }, { 2, 2 }, { 3, 3 }, { 4, 4 }, { 5, 5 }, { 12, 6 }, { 13, 7 },
        { 10, 8 }, { 8, 9 }, { 6, 10 }, { 11, 11 }, { 9, 12 }, { 7, 13 },
    };

    public static int SlotFor(DeviceModel model, int artSlot) =>
        model == DeviceModel.SpathaX ? SpathaX.GetValueOrDefault(artSlot, -1) : artSlot;
}

/// <summary>
/// Armoury-style button map: line-art views of the mouse with clickable button
/// areas, leader lines and callouts that show each button's current action.
/// </summary>
public sealed class SlotMap : UserControl
{
    private const double BoxW = 124, BoxH = 38, TopPad = 10;

    private sealed class Part
    {
        public required Polygon Area;
        public required Path Leader;
        public required Ellipse Dot;
        public required Border Box;
        public required Path Notch;
        public required TextBlock Title;
        public required TextBlock Value;
    }

    private readonly Dictionary<int, Part> _parts = new();      // by device slot
    private readonly StackPanel _root = new() { Orientation = Orientation.Horizontal };
    private DeviceModel _model;
    private int _selected = -1;
    private int _hover = -1;

    public event EventHandler<int>? SlotSelected;
    public int SelectedSlot => _selected;

    private static Brush R(string key) => (Brush)Application.Current.Resources[key];
    private static readonly Brush BoxIdle = Frozen(Color.FromRgb(0x03, 0x07, 0x09));
    private static readonly Brush BoxLine = Frozen(Color.FromRgb(0x9F, 0xB2, 0xB9));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public SlotMap()
    {
        Content = new Viewbox { Child = _root, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
    }

    public void Configure(DeviceModel model, IReadOnlyList<ButtonSlot> slots)
    {
        _model = model;
        _parts.Clear();
        _root.Children.Clear();
        _selected = -1;
        var names = slots.ToDictionary(s => s.Slot, s => s.Name);
        _root.Children.Add(BuildView(SpathaArt.Top, names));
        _root.Children.Add(BuildView(SpathaArt.Side, names, leftMargin: 16));
    }

    private FrameworkElement BuildView(MapView v, Dictionary<int, string> names, double leftMargin = 0)
    {
        double rightBox = v.Regions.Where(r => !r.Left).Select(r => r.Box.X).DefaultIfEmpty(v.OffsetX + v.Width + 40).Max();
        double width = rightBox + BoxW + 8;
        double height = v.Height + TopPad * 2 + 8;

        var canvas = new Canvas { Width = width, Height = height, Background = Brushes.Transparent };

        var img = new Image
        {
            Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/{v.Image}")),
            Width = v.Width,
            Height = v.Height,
            Opacity = 0.9,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(img, v.OffsetX);
        Canvas.SetTop(img, TopPad);
        canvas.Children.Add(img);

        foreach (var r in v.Regions)
        {
            int slot = SlotMapLayout.SlotFor(_model, r.Slot);
            if (slot < 0 || !names.ContainsKey(slot)) continue;
            AddRegion(canvas, v, r, slot, names[slot]);
        }

        var card = new Border
        {
            Style = (Style)Application.Current.Resources["Card"],
            Margin = new Thickness(leftMargin, 0, 0, 0),
            Padding = new Thickness(10, 8, 10, 4),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = v.Title.ToUpperInvariant(), Style = (Style)Application.Current.Resources["Caption"], Margin = new Thickness(4, 0, 0, 2) },
                    canvas,
                },
            },
        };
        return card;
    }

    private void AddRegion(Canvas canvas, MapView v, MapRegion r, int slot, string name)
    {
        var accent = R("Accent");
        var points = new PointCollection(r.Points.Select(p => new Point(p.X + v.OffsetX, p.Y + TopPad)));
        var area = new Polygon
        {
            Points = points,
            Stroke = accent,
            StrokeThickness = 1,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = name,
        };

        double ax = r.Anchor.X + v.OffsetX, ay = r.Anchor.Y + TopPad;
        double bx = r.Box.X, by = r.Box.Y;
        double ex = r.Left ? bx + BoxW : bx, ey = by + BoxH / 2;
        double knee = r.Left ? ex + 14 : ex - 14;

        var leader = new Path
        {
            Data = Geometry.Parse(FormattableString.Invariant($"M{ex},{ey} L{knee},{ey} L{ax},{ay}")),
            Stroke = accent,
            StrokeThickness = 0.9,
            Opacity = 0.45,
            IsHitTestVisible = false,
        };
        var dot = new Ellipse { Width = 5, Height = 5, Fill = accent, IsHitTestVisible = false };
        Canvas.SetLeft(dot, ax - 2.5);
        Canvas.SetTop(dot, ay - 2.5);

        var title = new TextBlock
        {
            Text = name.ToUpperInvariant(),
            FontSize = 8.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = R("FgDim"),
            Margin = new Thickness(8, 4, 8, 0),
        };
        var value = new TextBlock
        {
            Text = "—",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = R("Fg"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 8, 4),
        };
        var box = new Border
        {
            Width = BoxW,
            Height = BoxH,
            Background = BoxIdle,
            BorderBrush = BoxLine,
            BorderThickness = new Thickness(0.8),
            CornerRadius = new CornerRadius(2),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new StackPanel { Children = { title, value } },
        };
        Canvas.SetLeft(box, bx);
        Canvas.SetTop(box, by);

        // Corner notch, as on Armoury's callouts.
        var notch = new Path
        {
            Data = Geometry.Parse(FormattableString.Invariant($"M{bx + BoxW - 10},{by} H{bx + BoxW} V{by + 10} Z")),
            Fill = BoxLine,
            IsHitTestVisible = false,
        };

        canvas.Children.Add(area);
        canvas.Children.Add(leader);
        canvas.Children.Add(dot);
        canvas.Children.Add(box);
        canvas.Children.Add(notch);

        var part = new Part { Area = area, Leader = leader, Dot = dot, Box = box, Notch = notch, Title = title, Value = value };
        _parts[slot] = part;

        foreach (UIElement e in new UIElement[] { area, box })
        {
            e.MouseEnter += (_, _) => { _hover = slot; Paint(slot); };
            e.MouseLeave += (_, _) => { if (_hover == slot) _hover = -1; Paint(slot); };
            e.MouseLeftButtonDown += (_, _) => Select(slot);
        }
        Paint(slot);
    }

    private void Paint(int slot)
    {
        if (!_parts.TryGetValue(slot, out var p)) return;
        bool sel = slot == _selected, hov = slot == _hover;
        var accent = R("Accent");
        p.Area.Opacity = sel ? 1 : hov ? 0.9 : 0.75;
        p.Area.Fill = new SolidColorBrush(((SolidColorBrush)accent).Color) { Opacity = sel ? 0.55 : hov ? 0.32 : 0.14 };
        p.Leader.Opacity = sel ? 1 : hov ? 0.8 : 0.45;
        p.Leader.StrokeThickness = sel ? 1.6 : 0.9;
        p.Box.BorderBrush = sel || hov ? accent : BoxLine;
        p.Box.Background = sel ? R("AccentSoft") : BoxIdle;
        p.Notch.Fill = sel || hov ? accent : BoxLine;
    }

    public void Select(int slot)
    {
        int old = _selected;
        _selected = slot;
        if (old >= 0) Paint(old);
        Paint(slot);
        SlotSelected?.Invoke(this, slot);
    }

    /// <summary>Updates the action text shown in every callout.</summary>
    public void SetLabels(Func<int, string?> labelFor)
    {
        foreach (var (slot, p) in _parts)
        {
            string text = labelFor(slot) ?? "—";
            p.Value.Text = text;
            p.Box.ToolTip = text;
        }
    }
}
