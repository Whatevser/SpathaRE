using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using SpathaRE.Core;
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
        // The original Spatha lists and exports the same F13-F24 combos as the Spatha X.
        Core.SpathaL701.L701Device.ComboLabel = u => Combos.Label(u);
        Core.SpathaL701.L701Device.ComboKeys = u => Combos.KeysOf(u);
        Core.SpathaL701.L701Device.AllocateCombo = Combos.Allocate;
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

            _tray.BalloonTipTitle = $"{(sender as SpathaRE.Core.Shared.IMouseDevice)?.Name}: profile {e.CurrentProfile + 1}";
            _tray.BalloonTipText = "Switched on the mouse";
            _tray.ShowBalloonTip(1500);
        });
    }

    // ---- hotkeys ---------------------------------------------------------

    public static App Instance => (App)Current;

    /// <summary>Every app hotkey: config key, what it does, default gesture.</summary>
    public static readonly (string Key, string Label, string Default)[] HotkeyDefs =
    {
        ("hotkey_profile_1", "Profile 1", "Ctrl+Alt+1"),
        ("hotkey_profile_2", "Profile 2", "Ctrl+Alt+2"),
        ("hotkey_profile_3", "Profile 3", "Ctrl+Alt+3"),
        ("hotkey_profile_4", "Profile 4", "Ctrl+Alt+4"),
        ("hotkey_profile_5", "Profile 5", "Ctrl+Alt+5"),
        ("hotkey_profile_6", "Profile 6 (original Spatha's Default)", "Ctrl+Alt+6"),
        ("hotkey_profile_next", "Next profile", "Ctrl+Alt+OemPlus"),
        ("hotkey_profile_prev", "Previous profile", "Ctrl+Alt+OemMinus"),
        ("hotkey_popup", "Show the tray popup", "Ctrl+Alt+M"),
    };

    public static string HotkeyGesture(string key) =>
        AppConfig.GetString(key, HotkeyDefs.First(h => h.Key == key).Default)!;

    /// <summary>
    /// Registers every hotkey from config.json (editable in Settings → Hotkeys).
    /// Missing keys fall back to the defaults above; "" disables one. Returns, per
    /// config key, whether Windows accepted it (false = taken by another app).
    /// </summary>
    public Dictionary<string, bool> RegisterHotkeys()
    {
        _hotkeys.UnregisterGroup("user");
        var result = new Dictionary<string, bool>();

        foreach (var (key, _, _) in HotkeyDefs)
        {
            string gesture = HotkeyGesture(key);
            if (gesture.Length == 0) continue;
            Action action = key switch
            {
                "hotkey_profile_next" => () => _ = MouseManager.CycleProfileAsync(1),
                "hotkey_profile_prev" => () => _ = MouseManager.CycleProfileAsync(-1),
                "hotkey_popup" => TogglePopup,
                _ => SetProfileAction(int.Parse(key["hotkey_profile_".Length..]) - 1),
            };
            result[key] = _hotkeys.Register(gesture, action);
        }
        LastHotkeyResults = result;
        return result;
    }

    public Dictionary<string, bool> LastHotkeyResults { get; private set; } = new();

    /// <summary>Releases the profile hotkeys while one is being re-recorded in Settings.</summary>
    public void SuspendHotkeys() => _hotkeys.UnregisterGroup("user");

    private static Action SetProfileAction(int profile) => () => _ = MouseManager.SetProfileAsync(profile);

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
