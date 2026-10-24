using System.Text.RegularExpressions;
using SpathaRE.Core;
using SpathaRE.Core.Mouse;
using SpathaRE.Hotkeys;
using Keys = System.Windows.Forms.Keys;

namespace SpathaRE.Input;

/// <summary>
/// Makes multi-key mouse bindings actually work.
///
/// A combo button doesn't emit Ctrl+C itself: on both Spathas it sends only a
/// carrier key — F13 through F24, one per action code
/// 0x0068..0x0073. Something on the host has to catch that carrier and replay
/// the real keystrokes. Without this class, binding a side button to Copy makes
/// the mouse send F13 and nothing happens.
/// </summary>
public sealed class ComboDispatcher
{
    private const ushort FirstComboCode = 0x0068;
    private const string HotkeyGroup = "mousecombo";

    private readonly HotkeyManager _hotkeys;
    private readonly Action<Action> _toUiThread;

    public ComboDispatcher(HotkeyManager hotkeys, Action<Action> toUiThread)
    {
        _hotkeys = hotkeys;
        _toUiThread = toUiThread;

        MouseManager.BindingsChanged += (_, _) => Rebuild();
        MouseManager.DevicesChanged += (_, _) => Rebuild();
        MouseManager.ProfileChanged += (_, _) => Rebuild();
    }

    /// <summary>
    /// Registers a carrier hotkey for every combo action currently bound on the
    /// device. Only the ones in use are claimed — grabbing all twelve would take
    /// F13-F24 away from other apps for no reason.
    /// </summary>
    public void Rebuild() => _toUiThread(() =>
    {
        _hotkeys.UnregisterGroup(HotkeyGroup);

        // Both mice use the same twelve carriers: the Spatha X binds code 0x0068.., the
        // original Spatha the HID key F13.., which is the same number.
        var codes = new HashSet<ushort>();
        foreach (var d in MouseManager.Snapshot())
            foreach (ushort code in d.BoundCombos)
                if (AsusMouse.CombosByCode.ContainsKey(code)) codes.Add(code);

        foreach (ushort code in codes)
        {
            ushort captured = code;
            uint vk = (uint)(Keys.F13 + (code - FirstComboCode));
            bool ok = _hotkeys.RegisterRaw(0, vk, () => Dispatch(captured), HotkeyGroup);
            if (!ok)
                Logger.WriteLine($"Combo carrier F{13 + (code - FirstComboCode)} unavailable — " +
                                 "another app holds it, so that button will do nothing");
        }

        if (codes.Count > 0)
            Logger.WriteLine($"Combo dispatcher watching {codes.Count} carrier key(s)");
    });

    private void Dispatch(ushort code)
    {
        if (!AsusMouse.CombosByCode.TryGetValue(code, out var combo)) return;

        string command = combo.ResolveCommand();
        var keys = ParseCommand(command);

        if (keys.Count == 0)
        {
            Logger.WriteLine($"Combo '{combo.Label}' has no usable key sequence ('{command}')");
            return;
        }

        int hold = Math.Clamp(AppConfig.Get("combo_hold_ms", 10), 0, 500);

        // Off the UI thread: PressCombo sleeps between down and up, and blocking
        // the dispatcher thread would freeze the tray and stall the HID poller.
        Task.Run(() =>
        {
            try { KeyboardSynth.PressCombo(keys, hold); }
            catch (Exception e) { Logger.WriteLine($"Combo '{combo.Label}' failed: {e.Message}"); }
        });
    }

    /// <summary>
    /// Accepts either the raw hex form G-Helper uses ("0xA2 0x43") or a friendly
    /// gesture ("Ctrl+C", "Win+Shift+S"). Hex wins if the string parses as hex,
    /// so existing config values keep working unchanged.
    /// </summary>
    public static List<Keys> ParseCommand(string? command)
    {
        var result = new List<Keys>();
        if (string.IsNullOrWhiteSpace(command)) return result;

        var hex = ParseHexValues(command);
        if (hex.Count > 0)
        {
            foreach (int v in hex) result.Add((Keys)v);
            return result;
        }

        foreach (var raw in command.Split(new[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            Keys? mapped = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => Keys.LControlKey,
                "alt"               => Keys.LMenu,
                "shift"             => Keys.LShiftKey,
                "win" or "windows"  => Keys.LWin,
                "esc"               => Keys.Escape,
                "del"               => Keys.Delete,
                "ins"               => Keys.Insert,
                "pgup"              => Keys.PageUp,
                "pgdn"              => Keys.PageDown,
                _ => null
            };

            if (mapped is not null) { result.Add(mapped.Value); continue; }

            string name = part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : part;
            if (Enum.TryParse<Keys>(name, ignoreCase: true, out var key)) result.Add(key);
            else { Logger.WriteLine($"Unrecognised key '{part}' in combo command"); return new List<Keys>(); }
        }

        return result;
    }

    /// <summary>Readable form of a combo command, e.g. "Ctrl+Shift+Esc". Falls back to the text itself.</summary>
    public static string Describe(string? command)
    {
        var keys = ParseCommand(command);
        return keys.Count == 0 ? (command ?? "") : Describe(keys);
    }

    public static string Describe(IEnumerable<Keys> keys) => string.Join("+", keys.Select(KeyName).Distinct());

    public static string KeyName(Keys k)
    {
        if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
        if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num " + (k - Keys.NumPad0);
        return k switch
        {
            Keys.LControlKey or Keys.RControlKey or Keys.ControlKey => "Ctrl",
            Keys.LShiftKey or Keys.RShiftKey or Keys.ShiftKey => "Shift",
            Keys.LMenu or Keys.RMenu or Keys.Menu => "Alt",
            Keys.LWin or Keys.RWin => "Win",
            Keys.Escape => "Esc",
            Keys.Back => "Backspace",
            Keys.Return => "Enter",
            Keys.PageUp => "PageUp",
            Keys.PageDown => "PageDown",
            Keys.Capital => "CapsLock",
            Keys.Snapshot => "PrintScreen",
            Keys.Apps => "Menu",
            Keys.OemMinus => "-",
            Keys.Oemplus => "=",
            Keys.OemOpenBrackets => "[",
            Keys.OemCloseBrackets => "]",
            Keys.OemPipe => "\\",
            Keys.OemSemicolon => ";",
            Keys.OemQuotes => "'",
            Keys.Oemtilde => "`",
            Keys.Oemcomma => ",",
            Keys.OemPeriod => ".",
            Keys.OemQuestion => "/",
            _ => k.ToString(),
        };
    }

    /// <summary>Stored form of a key list: the hex virtual-key codes, which always parse back.</summary>
    public static string ToCommand(IEnumerable<Keys> keys) => string.Join(" ", keys.Select(k => $"0x{(int)k:X2}"));

    private static List<int> ParseHexValues(string input)
    {
        const string token = @"\b(0x[0-9A-Fa-f]{1,2}|[0-9A-Fa-f]{1,2})\b";
        if (!Regex.IsMatch(input.Trim(), $"^{token}(\\s+{token})*$")) return new List<int>();

        var values = new List<int>();
        foreach (Match m in Regex.Matches(input, token))
        {
            string s = m.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? m.Value[2..] : m.Value;
            values.Add(Convert.ToInt32(s, 16));
        }
        return values;
    }
}
