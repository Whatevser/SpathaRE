using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using MouseHud.Core;
using MouseHud.Hotkeys;
using MouseHud.Input;
using MouseHud.UI;
using Forms = System.Windows.Forms;

namespace MouseHud;

public partial class App : Application
{
    private Forms.NotifyIcon _tray = null!;
    private HotkeyManager _hotkeys = null!;
    private ComboDispatcher _combos = null!;
    private ProfilePopup? _popup;
    private static Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, "MouseHud-SingleInstance", out bool isFirst);
        if (!isFirst) { Shutdown(); return; }

        Logger.WriteLine("MouseHud starting");

        _tray = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "MouseHud",
            Icon = RenderTrayIcon(null, 0)
        };
        _tray.MouseClick += (_, ev) =>
        {
            if (ev.Button == Forms.MouseButtons.Left) TogglePopup();
        };
        BuildTrayMenu();

        MouseManager.DevicesChanged += (_, _) => Dispatcher.BeginInvoke(RefreshTray);
        MouseManager.BatteryChanged += (_, _) => Dispatcher.BeginInvoke(RefreshTray);
        MouseManager.ProfileChanged += OnProfileChanged;
        MouseManager.Start();

        _hotkeys = new HotkeyManager();
        RegisterHotkeys();

        // Multi-key bindings (Ctrl+C, Alt+Tab, ...) only send a carrier key from
        // the mouse; this replays them as real keystrokes.
        _combos = new ComboDispatcher(_hotkeys, a => Dispatcher.Invoke(a));
        _combos.Rebuild();
    }

    /// <summary>
    /// The mouse can switch profiles from its own button combo without telling
    /// the host, so MouseManager polls for it. Only surface a balloon when the
    /// change came from the device — echoing our own hotkeys would be noise.
    /// </summary>
    private void OnProfileChanged(object? sender, ProfileChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshTray();
            if (!e.External || !AppConfig.GetBool("notify_profile_change", true)) return;

            _tray.BalloonTipTitle = "Profile " + (e.CurrentProfile + 1);
            _tray.BalloonTipText = "Switched on the mouse";
            _tray.ShowBalloonTip(1500);
        });
    }

    // ---- hotkeys ---------------------------------------------------------

    /// <summary>
    /// Reads gestures from config.json. Missing keys fall back to the defaults
    /// below; set a key to "" to disable it.
    /// </summary>
    public void RegisterHotkeys()
    {
        _hotkeys.UnregisterGroup("user");

        for (int i = 1; i <= 5; i++)
        {
            int profile = i - 1;
            string gesture = AppConfig.GetString($"hotkey_profile_{i}", $"Ctrl+Alt+{i}")!;
            if (gesture.Length > 0)
                _hotkeys.Register(gesture, () => _ = MouseManager.SetProfileAsync(profile));
        }

        string next = AppConfig.GetString("hotkey_profile_next", "Ctrl+Alt+OemPlus")!;
        if (next.Length > 0) _hotkeys.Register(next, () => _ = MouseManager.CycleProfileAsync(1));

        string prev = AppConfig.GetString("hotkey_profile_prev", "Ctrl+Alt+OemMinus")!;
        if (prev.Length > 0) _hotkeys.Register(prev, () => _ = MouseManager.CycleProfileAsync(-1));

        string popup = AppConfig.GetString("hotkey_popup", "Ctrl+Alt+M")!;
        if (popup.Length > 0) _hotkeys.Register(popup, TogglePopup);
    }

    // ---- tray ------------------------------------------------------------

    private void BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => TogglePopup());
        menu.Items.Add("Settings…", null, (_, _) =>
        {
            _popup ??= new ProfilePopup();
            _popup.ShowSettings();
        });
        menu.Items.Add("Re-scan devices", null, (_, _) => Task.Run(MouseManager.DetectAll));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Edit config…", null, (_, _) =>
        {
            System.Diagnostics.Process.Start("explorer.exe", Logger.AppPath);
        });
        menu.Items.Add("Reload hotkeys", null, (_, _) => RegisterHotkeys());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
    }

    private void RefreshTray()
    {
        var d = MouseManager.Active;
        var old = _tray.Icon;

        if (d is null || !d.IsReady)
        {
            _tray.Icon = RenderTrayIcon(null, 0);
            _tray.Text = d is null ? "MouseHud — no device" : Truncate($"{d.Name} — asleep", 63);
        }
        else
        {
            _tray.Icon = RenderTrayIcon(d.Profile + 1, d.HasBattery ? d.Battery : -1);
            string battery = d.HasBattery && d.Battery >= 0 ? $" — {d.Battery}%{(d.Charging ? " ⚡" : "")}" : "";
            _tray.Text = Truncate($"{d.Name} — P{d.Profile + 1}{battery}", 63);
        }

        old?.Dispose();
        _popup?.Refresh();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>
    /// Draws the tray glyph at runtime: profile number, with a battery arc when
    /// the device reports one. Avoids shipping a pile of .ico variants.
    /// </summary>
    private static Icon RenderTrayIcon(int? profile, int battery)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            if (profile is null)
            {
                using var dim = new Pen(Color.FromArgb(140, 140, 140), 2.5f);
                g.DrawEllipse(dim, 5, 5, 22, 22);
                g.DrawLine(dim, 10, 10, 22, 22);
            }
            else
            {
                if (battery >= 0)
                {
                    Color arc = battery <= 15 ? Color.FromArgb(230, 80, 80)
                              : battery <= 35 ? Color.FromArgb(235, 175, 60)
                                              : Color.FromArgb(33, 196, 232);
                    using var track = new Pen(Color.FromArgb(70, 70, 70), 3f);
                    using var fill = new Pen(arc, 3f);
                    g.DrawArc(track, 3, 3, 26, 26, 0, 360);
                    g.DrawArc(fill, 3, 3, 26, 26, -90, (int)(360f * battery / 100f));
                }

                using var font = new Font("Segoe UI", 15, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(Color.White);
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(profile.Value.ToString(), font, brush, new RectangleF(0, 0, 32, 33), fmt);
            }
        }

        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { NativeMethods.DestroyIcon(h); }
    }

    // ---- popup -----------------------------------------------------------

    private void TogglePopup()
    {
        if (_popup is { IsVisible: true }) { _popup.Hide(); return; }

        _popup ??= new ProfilePopup();
        _popup.ShowNearTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.WriteLine("MouseHud exiting");
        _hotkeys?.Dispose();
        MouseManager.Stop();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr handle);
}
