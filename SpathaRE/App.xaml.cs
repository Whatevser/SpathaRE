using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using SpathaRE.Core;
using SpathaRE.Hotkeys;
using SpathaRE.Input;
using SpathaRE.UI;
using Forms = System.Windows.Forms;

namespace SpathaRE;

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

        _instanceMutex = new Mutex(true, "SpathaRE-SingleInstance", out bool isFirst);
        if (!isFirst) { Shutdown(); return; }

        Logger.WriteLine("SpathaRE starting");

        _tray = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "SpathaRE",
            Icon = AppIcon.Tray(null, -1)
        };
        _tray.MouseClick += (_, ev) =>
        {
            if (ev.Button == Forms.MouseButtons.Left) TogglePopup();
        };
        BuildTrayMenu();

        MouseManager.DevicesChanged += (_, _) => Dispatcher.BeginInvoke(RefreshTray);
        MouseManager.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(RefreshTray);
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

            _tray.BalloonTipTitle = $"{(sender as SpathaRE.Core.Devices.IMouseDevice)?.Name}: profile {e.CurrentProfile + 1}";
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
        var d = MouseManager.Selected;
        var old = _tray.Icon;

        _tray.Icon = d is null || !d.IsReady
            ? AppIcon.Tray(null, -1)
            : AppIcon.Tray(d.Profile + 1, d.HasBattery ? d.Battery : -1);

        // One line per mouse; NotifyIcon allows 127 characters.
        var lines = MouseManager.Snapshot().Select(m =>
            !m.IsReady ? $"{m.Name}: asleep"
            : $"{m.Name}: P{m.Profile + 1}" + (m.HasBattery && m.Battery >= 0 ? $", {m.Battery}%{(m.Charging ? " ⚡" : "")}" : ""));
        string text = string.Join("\n", lines);
        _tray.Text = Truncate(text.Length == 0 ? "SpathaRE: no mouse" : text, 127);

        old?.Dispose();
        _popup?.Refresh();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // ---- popup -----------------------------------------------------------

    private void TogglePopup()
    {
        if (_popup is { IsVisible: true }) { _popup.Hide(); return; }

        _popup ??= new ProfilePopup();
        _popup.ShowNearTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.WriteLine("SpathaRE exiting");
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
