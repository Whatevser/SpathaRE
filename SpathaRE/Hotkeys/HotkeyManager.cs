using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace SpathaRE.Hotkeys;

/// <summary>
/// Global hotkeys via RegisterHotKey. Thread-affine: every call must happen on
/// the UI thread, because UnregisterHotKey only frees the calling thread's
/// registrations and the OS tears them down when the thread exits.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<int, (Action Action, string Group)> _actions = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        // Message-only window: never shown, never focusable, survives popup churn.
        var parameters = new HwndSourceParameters("SpathaREHotkeys")
        {
            Width = 0,
            Height = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0x40000000,       // WS_CHILD — required for message-only
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var entry))
        {
            handled = true;
            try { entry.Action(); } catch (Exception e) { Logger.WriteLine("Hotkey action threw: " + e.Message); }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Register a gesture like "Ctrl+Alt+1" or "Shift+Win+F13". Returns false if the
    /// combo is unparseable or already claimed by another process.
    /// </summary>
    public bool Register(string gesture, Action action, string group = "user")
    {
        if (!TryParse(gesture, out uint mods, out uint vk))
        {
            Logger.WriteLine($"Hotkey '{gesture}' could not be parsed");
            return false;
        }

        return RegisterRaw(mods, vk, action, group, gesture);
    }

    /// <summary>
    /// Register by raw modifier mask and virtual-key code. Used for the F13-F24
    /// combo carrier keys, which have no gesture string.
    /// </summary>
    public bool RegisterRaw(uint modifiers, uint vk, Action action, string group, string? label = null)
    {
        int id = _nextId++;
        // MOD_NOREPEAT (0x4000) stops key-repeat from spamming profile switches.
        if (!RegisterHotKey(_source.Handle, id, modifiers | 0x4000, vk))
        {
            Logger.WriteLine($"Hotkey '{label ?? $"vk 0x{vk:X2}"}' rejected by the OS " +
                             $"(error {Marshal.GetLastWin32Error()}) — likely already taken");
            _nextId--;
            return false;
        }

        _actions[id] = (action, group);
        Logger.WriteLine($"Hotkey '{label ?? $"vk 0x{vk:X2}"}' registered [{group}]");
        return true;
    }

    /// <summary>
    /// Drop one group's registrations, leaving the others intact — combo carriers
    /// get rebuilt whenever bindings change, and that must not disturb the user's
    /// profile hotkeys.
    /// </summary>
    public void UnregisterGroup(string group)
    {
        foreach (var id in _actions.Where(kv => kv.Value.Group == group).Select(kv => kv.Key).ToList())
        {
            UnregisterHotKey(_source.Handle, id);
            _actions.Remove(id);
        }
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _nextId = 1;
    }

    public static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= 0x0002; continue;
                case "alt":               modifiers |= 0x0001; continue;
                case "shift":             modifiers |= 0x0004; continue;
                case "win" or "windows":  modifiers |= 0x0008; continue;
            }

            // Key name -> virtual key code. Uses WPF's Key enum so "F13", "NumPad1",
            // "OemTilde" etc. all work; VK is layout-independent by design.
            if (!Enum.TryParse<Key>(NormalizeKeyName(part), true, out var key)) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }

        return vk != 0;
    }

    private static string NormalizeKeyName(string s) =>
        s.Length == 1 && char.IsDigit(s[0]) ? "D" + s : s;   // "1" -> Key.D1

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
