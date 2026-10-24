// Both mice: the twelve key combos (F13-F24) and the combo-text helpers.

using System.Text.RegularExpressions;
using SpathaRE.Core;
using SpathaRE.Core.Shared;
using Keys = System.Windows.Forms.Keys;

namespace SpathaRE.Input;

/// <summary>
/// The twelve key combos both mice share ("Alt+Tab (F13)" ... "Win+M (F24)").
///
/// A combo button makes the mouse send a spare key, F13-F24, and ComboDispatcher
/// types the real combination. The Spatha X binds code 0x0068-0x0073, the original
/// Spatha the HID keys F13-F24: the same numbers, so one table serves both. What
/// each combo types is stored in config as mouse_combo_F13 ... mouse_combo_F24
/// (hex virtual keys, or text like "Ctrl+C"); the defaults are G-Helper's.
/// </summary>
public static class Combos
{
    public const ushort First = 0x68, Last = 0x73;

    /// <param name="Code">Spatha X action code = HID usage of the spare key.</param>
    /// <param name="Name">G-Helper's name for the default ("Copy").</param>
    public sealed record Combo(ushort Code, string Name, string DefaultCommand)
    {
        public string ConfigKey => $"mouse_combo_{CarrierKey(Code)}";
        /// <summary>What it types now: the user's keys, else the default.</summary>
        public string Command => AppConfig.GetString(ConfigKey) ?? DefaultCommand;
        public bool IsCustom => AppConfig.GetString(ConfigKey) is not null;
    }

    private static string Hex(params Keys[] keys) => ToCommand(keys);

    public static readonly IReadOnlyList<Combo> All = new List<Combo>
    {
        new(0x68, "Alt + Tab",    Hex(Keys.LMenu,       Keys.Tab)),
        new(0x69, "Alt + F4",     Hex(Keys.LMenu,       Keys.F4)),
        new(0x6A, "Win + D",      Hex(Keys.LWin,        Keys.D)),
        new(0x6B, "Win + E",      Hex(Keys.LWin,        Keys.E)),
        new(0x6C, "Win + L",      Hex(Keys.LWin,        Keys.L)),
        new(0x6D, "Win + Tab",    Hex(Keys.LWin,        Keys.Tab)),
        new(0x6E, "Win + V",      Hex(Keys.LWin,        Keys.V)),
        new(0x6F, "Copy",         Hex(Keys.LControlKey, Keys.C)),
        new(0x70, "Paste",        Hex(Keys.LControlKey, Keys.V)),
        new(0x71, "Undo",         Hex(Keys.LControlKey, Keys.Z)),
        new(0x72, "Task Manager", Hex(Keys.LControlKey, Keys.LShiftKey, Keys.Escape)),
        new(0x73, "Minimize All", Hex(Keys.LWin,        Keys.M)),
    };

    private static readonly Dictionary<int, Combo> ByCode = All.ToDictionary(c => (int)c.Code);

    public static bool IsCarrier(int code) => code >= First && code <= Last;
    public static Combo? Get(int code) => ByCode.GetValueOrDefault(code);
    /// <summary>0x68 -> "F13".</summary>
    public static string CarrierKey(int code) => $"F{13 + (code - First)}";

    /// <summary>"Alt+Tab (F13)": the keys it types, then the spare key the mouse sends.</summary>
    public static string Label(int code) =>
        Get(code) is { } c ? $"{Describe(c.Command)} ({CarrierKey(code)})" : CarrierKey(code);

    /// <summary>The keys a combo types, as modifiers + HID keys (for export / import).</summary>
    public static PortableAction? KeysOf(int code) =>
        Get(code) is { } c ? KeyCodes.FromVirtualKeys(ParseCommand(c.Command).Select(k => (int)k)) : null;

    /// <summary>
    /// Import: the combo for a key combination. One that already types those keys,
    /// else the first one not in <paramref name="busy"/>, which is then set to type
    /// them (and noted in the report). Null when all twelve are taken.
    /// </summary>
    public static ushort? Allocate(PortableAction a, HashSet<ushort> busy, ImportReport report)
    {
        ushort? found = null;
        foreach (var c in All)
            if (KeysOf(c.Code)?.KeySignature == a.KeySignature) { found = c.Code; break; }
        var vks = KeyCodes.ToVirtualKeys(a);
        if (found is null && vks is not null)
            foreach (var c in All)
            {
                if (busy.Contains(c.Code)) continue;
                string old = Describe(c.Command);
                AppConfig.Set(c.ConfigKey, string.Join(" ", vks.Select(v => $"0x{v:X2}")));
                report.Note($"Combo {CarrierKey(c.Code)} now types {Describe(c.Command)} " +
                            $"(was {old}). Every button that uses {CarrierKey(c.Code)} types it too.");
                found = c.Code;
                break;
            }
        if (found is null) return null;
        busy.Add(found.Value);
        report.ReservedCarriers.Add(found.Value);
        return found;
    }

    // ---- combo text: "Ctrl+Shift+Esc" <-> key lists ----

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
