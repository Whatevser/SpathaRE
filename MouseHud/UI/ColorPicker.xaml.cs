using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Drawing = System.Drawing;
// This class exposes a property called Color of type System.Drawing.Color, which
// shadows the type name inside the class body. Alias the WPF one explicitly.
using MediaColor = System.Windows.Media.Color;

namespace MouseHud.UI;

/// <summary>
/// Compact RGB picker. WPF ships no colour dialog, and the WinForms one is a
/// modal that would block the HID poll loop, so this stays inline.
/// </summary>
public partial class ColorPicker : UserControl
{
    private bool _suppress;
    private Drawing.Color _color = Drawing.Color.Red;

    /// <summary>Fires continuously while dragging — throttle before writing to the device.</summary>
    public event EventHandler<Drawing.Color>? ColorChanged;

    private static readonly Drawing.Color[] Presets =
    {
        Drawing.Color.Red, Drawing.Color.OrangeRed, Drawing.Color.Orange, Drawing.Color.Yellow,
        Drawing.Color.Lime, Drawing.Color.SpringGreen, Drawing.Color.Cyan, Drawing.Color.DeepSkyBlue,
        Drawing.Color.Blue, Drawing.Color.BlueViolet, Drawing.Color.Magenta, Drawing.Color.DeepPink,
        Drawing.Color.White, Drawing.Color.FromArgb(255, 200, 16, 80),
    };

    public ColorPicker()
    {
        InitializeComponent();

        RSlider.ValueChanged += (_, _) => OnSlider();
        GSlider.ValueChanged += (_, _) => OnSlider();
        BSlider.ValueChanged += (_, _) => OnSlider();

        HexBox.LostFocus += (_, _) => ApplyHex();
        HexBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) ApplyHex(); };

        BuildSwatches();
        SetColor(_color, notify: false);
    }

    private void BuildSwatches()
    {
        var items = new List<UIElement>();
        foreach (var c in Presets)
        {
            var b = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(MediaColor.FromRgb(c.R, c.G, c.B)),
                BorderBrush = (Brush)Application.Current.Resources["LineHi"],
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}"
            };
            var captured = c;
            b.MouseLeftButtonDown += (_, _) => SetColor(captured, notify: true);
            items.Add(b);
        }
        Swatches.ItemsSource = items;
    }

    public Drawing.Color Color
    {
        get => _color;
        set => SetColor(value, notify: false);
    }

    public void SetColor(Drawing.Color c, bool notify)
    {
        _color = c;
        _suppress = true;
        RSlider.Value = c.R; GSlider.Value = c.G; BSlider.Value = c.B;
        _suppress = false;
        Render();
        if (notify) ColorChanged?.Invoke(this, _color);
    }

    private void OnSlider()
    {
        if (_suppress) return;
        _color = Drawing.Color.FromArgb((int)RSlider.Value, (int)GSlider.Value, (int)BSlider.Value);
        Render();
        ColorChanged?.Invoke(this, _color);
    }

    private void ApplyHex()
    {
        var s = HexBox.Text.Trim().TrimStart('#');
        if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            SetColor(Drawing.Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF), notify: true);
        else
            Render();   // reject silently, snap the box back to the real value
    }

    private void Render()
    {
        Preview.Background = new SolidColorBrush(MediaColor.FromRgb(_color.R, _color.G, _color.B));
        HexBox.Text = $"#{_color.R:X2}{_color.G:X2}{_color.B:X2}";
        RVal.Text = _color.R.ToString();
        GVal.Text = _color.G.ToString();
        BVal.Text = _color.B.ToString();
    }
}
