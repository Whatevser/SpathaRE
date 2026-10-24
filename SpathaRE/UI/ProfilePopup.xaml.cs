using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpathaRE.Core;
using Brush = System.Windows.Media.Brush;
using Forms = System.Windows.Forms;

namespace SpathaRE.UI;

/// <summary>Tray popup: battery, quick profile switch, and the way into the settings window.</summary>
public partial class ProfilePopup : Window
{
    private SettingsWindow? _settings;

    public ProfilePopup()
    {
        InitializeComponent();
        Deactivated += (_, _) => { if (_settings is null || !_settings.IsActive) Hide(); };

        MouseManager.DevicesChanged += (_, _) => Dispatcher.BeginInvoke(Refresh);
        MouseManager.ProfileChanged += (_, _) => Dispatcher.BeginInvoke(Refresh);
        MouseManager.BatteryChanged += (_, _) => Dispatcher.BeginInvoke(Refresh);

        OpenSettings.Click += (_, _) => ShowSettings();
    }

    public void ShowSettings()
    {
        if (_settings is null || !_settings.IsLoaded)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        else
        {
            if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
            _settings.Activate();
        }
        Hide();
    }

    public void ShowNearTray()
    {
        MouseManager.RefreshBatteryNow();
        Refresh();
        Show();

        // WinForms' Screen reports physical pixels; convert with this window's DPI scale.
        var wa = Forms.Screen.PrimaryScreen!.WorkingArea;
        var source = PresentationSource.FromVisual(this);
        double sx = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double sy = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        UpdateLayout();
        Left = (wa.Right / sx) - ActualWidth - 4;
        Top = (wa.Bottom / sy) - ActualHeight - 4;
        Activate();
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public void Refresh()
    {
        Devices.Children.Clear();
        var all = MouseManager.Snapshot();
        OpenSettings.IsEnabled = all.Count > 0;

        if (all.Count == 0)
        {
            Devices.Children.Add(new TextBlock { Text = "No supported mouse", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("Fg") });
            Devices.Children.Add(new TextBlock { Text = "Plug in a Spatha or Spatha X, or wake it up.", Style = (Style)Application.Current.Resources["Muted"], Margin = new Thickness(0, 2, 0, 12) });
            return;
        }

        foreach (var d in all)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            block.Children.Add(new TextBlock { Text = d.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("Fg") });

            string status = !d.IsReady ? $"{d.ConnectionLabel} · asleep or out of range"
                : d.ConnectionLabel + (d.HasBattery && d.Battery >= 0 ? $" · {d.Battery}%{(d.Charging ? " charging" : "")}" : "");
            block.Children.Add(new TextBlock { Text = status, Style = (Style)Application.Current.Resources["Muted"], Margin = new Thickness(0, 2, 0, 0) });

            if (d.IsReady && d.HasBattery && d.Battery >= 0)
            {
                var bar = new Grid { Height = 5, Margin = new Thickness(0, 8, 0, 0) };
                bar.Children.Add(new Border { Background = Res("SurfaceHi"), CornerRadius = new CornerRadius(3) });
                bar.Children.Add(new Border
                {
                    Background = d.Battery <= 15 ? Res("Danger") : d.Battery <= 35 ? Res("Warn") : Res("Ok"),
                    CornerRadius = new CornerRadius(3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 282 * d.Battery / 100.0,
                });
                block.Children.Add(bar);
            }

            if (d.IsReady && d.ProfileCount > 1)
            {
                var profiles = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                for (int i = 0; i < d.ProfileCount; i++)
                {
                    int p = i;
                    var dev = d;
                    var b = new Button
                    {
                        Style = (Style)Application.Current.Resources["ProfileButton"],
                        Content = (i + 1).ToString(),
                        ToolTip = d.ProfileName(i),
                    };
                    if (d.Profile == i)
                    {
                        b.Background = Res("AccentSoft");
                        b.BorderBrush = Res("Accent");
                    }
                    b.Click += async (_, _) => await MouseManager.SetProfileAsync(dev, p);
                    profiles.Children.Add(b);
                }
                block.Children.Add(profiles);
            }
            Devices.Children.Add(block);
        }
    }
}
