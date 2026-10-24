using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MouseHud.Core;
using Brush = System.Windows.Media.Brush;
using Forms = System.Windows.Forms;

namespace MouseHud.UI;

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
        var d = MouseManager.Active;
        ProfileList.Children.Clear();

        if (d is null)
        {
            DeviceName.Text = "No supported mouse";
            DeviceStatus.Text = "Plug in a Spatha or Spatha X, or wake it up.";
            BatteryBar.Visibility = Visibility.Collapsed;
            ProfileCaption.Visibility = Visibility.Collapsed;
            OpenSettings.IsEnabled = false;
            return;
        }

        OpenSettings.IsEnabled = true;
        DeviceName.Text = d.Name;

        if (!d.IsReady)
        {
            DeviceStatus.Text = $"{d.ConnectionLabel} · asleep or out of range";
            BatteryBar.Visibility = Visibility.Collapsed;
            ProfileCaption.Visibility = Visibility.Collapsed;
            return;
        }

        string battery = d.HasBattery && d.Battery >= 0 ? $" · {d.Battery}%{(d.Charging ? " charging" : "")}" : "";
        DeviceStatus.Text = d.ConnectionLabel + battery;

        if (d.HasBattery && d.Battery >= 0)
        {
            BatteryBar.Visibility = Visibility.Visible;
            BatteryFill.Width = 282 * d.Battery / 100.0;
            BatteryFill.Background = d.Battery <= 15 ? Res("Danger") : d.Battery <= 35 ? Res("Warn") : Res("Ok");
        }
        else BatteryBar.Visibility = Visibility.Collapsed;

        ProfileCaption.Visibility = d.ProfileCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < d.ProfileCount && d.ProfileCount > 1; i++)
        {
            int p = i;
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
            b.Click += async (_, _) => await MouseManager.SetProfileAsync(p);
            ProfileList.Children.Add(b);
        }
    }
}
