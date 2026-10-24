namespace SpathaRE.Core.Devices;

/// <summary>
/// The physical buttons both Spathas share, with one set of names for both
/// models. Each model numbers its slots differently; these tables translate.
/// Side buttons are numbered as on the button map: top row 1-2-3, bottom row
/// 4-5-6, front to back.
/// </summary>
public static class Buttons
{
    public static readonly (string Id, string Name)[] All =
    {
        ("left", "Left click"), ("right", "Right click"), ("wheel_click", "Wheel click"),
        ("back", "Back"), ("forward", "Forward"), ("dpi", "DPI button"),
        ("wheel_up", "Wheel up"), ("wheel_down", "Wheel down"),
        ("side1", "Side button 1"), ("side2", "Side button 2"), ("side3", "Side button 3"),
        ("side4", "Side button 4"), ("side5", "Side button 5"), ("side6", "Side button 6"),
    };

    /// <summary>Original Spatha: slot index -> button (factory keys 7 8 9 / 0 - = on the side grid).</summary>
    public static readonly string[] SpathaSlots =
    {
        "left", "right", "wheel_click", "back", "forward", "dpi",
        "side3", "side6", "side2", "side5", "side1", "side4",
        "wheel_up", "wheel_down",
    };

    /// <summary>Spatha X: slot index -> button.</summary>
    public static readonly string[] SpathaXSlots =
    {
        "left", "right", "wheel_click", "back", "forward", "dpi", "wheel_up", "wheel_down",
        "side1", "side2", "side3", "side4", "side5", "side6",
    };

    public static string[] SlotsFor(DeviceModel model) =>
        model == DeviceModel.SpathaX ? SpathaXSlots : SpathaSlots;

    public static string Name(string id) =>
        All.FirstOrDefault(b => b.Id == id).Name ?? id;

    public static string SlotName(DeviceModel model, int slot)
    {
        var slots = SlotsFor(model);
        return slot >= 0 && slot < slots.Length ? Name(slots[slot]) : $"Slot {slot}";
    }

    public static int SlotOf(DeviceModel model, string id) => Array.IndexOf(SlotsFor(model), id);

    /// <summary>
    /// Both mice switch profile on their own when you hold the DPI button and
    /// press a side button. Profile (0-based) -> that side button; profile 6 only
    /// exists on the original Spatha.
    /// </summary>
    public static string? ProfileSwitchButton(int profile) => profile switch
    {
        0 => "side1",
        1 => "side3",
        2 => "side6",
        3 => "side5",
        4 => "side4",
        5 => "side2",
        _ => null,
    };

    /// <summary>The device slots to press for a profile switch, DPI first. Empty when there is none.</summary>
    public static int[] ProfileSwitchSlots(DeviceModel model, int profile)
    {
        var side = ProfileSwitchButton(profile);
        if (side is null) return Array.Empty<int>();
        int dpi = SlotOf(model, "dpi"), s = SlotOf(model, side);
        return dpi < 0 || s < 0 ? Array.Empty<int>() : new[] { dpi, s };
    }

    // ---- keys: one list, one set of names for both mice ----

    /// <summary>Modifier key names by bit (1 = Left Ctrl ... 0x80 = Right Win), used when a button is a lone modifier.</summary>
    public static readonly string[] ModifierKeyNames =
        { "Left Ctrl", "Left Shift", "Left Alt", "Left Win", "Right Ctrl", "Right Shift", "Right Alt", "Right Win" };

    /// <summary>Short modifier names used inside a combination ("Ctrl+C").</summary>
    public static readonly string[] ModifierShortNames = { "Ctrl", "Shift", "Alt", "Win", "Ctrl", "Shift", "Alt", "Win" };

    /// <summary>The Keyboard list both mice show, in this order: HID usages (page 7).
    /// F13-F24 are missing on purpose: they are the combo keys.</summary>
    public static readonly byte[] KeyboardUsages = Enumerable.Range(0x04, 0x38 - 0x04 + 1)   // A-Z, 1-0, Enter..'/'
        .Concat(Enumerable.Range(0x39, 0x53 - 0x39 + 1))                                    // Caps, F1-F12, nav block, Num Lock
        .Concat(Enumerable.Range(0x54, 0x63 - 0x54 + 1))                                    // numpad
        .Concat(new[] { 0x65 })                                                             // Menu
        .Where(u => u != 0x32)                                                              // non-US # (no key on most layouts)
        .Select(u => (byte)u).ToArray();

    /// <summary>HID keyboard usage -> name, the same on both mice.</summary>
    public static string KeyName(int usage)
    {
        if (usage >= 0x04 && usage <= 0x1D) return ((char)('A' + usage - 0x04)).ToString();
        if (usage >= 0x1E && usage <= 0x26) return ((char)('1' + usage - 0x1E)).ToString();
        if (usage >= 0x3A && usage <= 0x45) return "F" + (usage - 0x3A + 1);
        if (usage >= 0x68 && usage <= 0x73) return "F" + (usage - 0x68 + 13);
        if (usage >= 0x59 && usage <= 0x61) return "Num " + (usage - 0x59 + 1);
        if (usage >= 0xE0 && usage <= 0xE7) return ModifierKeyNames[usage - 0xE0];
        return usage switch
        {
            0x27 => "0", 0x28 => "Enter", 0x29 => "Esc", 0x2A => "Backspace", 0x2B => "Tab",
            0x2C => "Space", 0x2D => "-", 0x2E => "=", 0x2F => "[", 0x30 => "]", 0x31 => "\\",
            0x33 => ";", 0x34 => "'", 0x35 => "`", 0x36 => ",", 0x37 => ".", 0x38 => "/",
            0x39 => "Caps Lock", 0x46 => "Print Screen", 0x47 => "Scroll Lock", 0x48 => "Pause",
            0x49 => "Insert", 0x4A => "Home", 0x4B => "Page Up", 0x4C => "Delete", 0x4D => "End",
            0x4E => "Page Down", 0x4F => "Right", 0x50 => "Left", 0x51 => "Down", 0x52 => "Up",
            0x53 => "Num Lock", 0x54 => "Num /", 0x55 => "Num *", 0x56 => "Num -", 0x57 => "Num +",
            0x58 => "Num Enter", 0x62 => "Num 0", 0x63 => "Num .", 0x65 => "Menu",
            _ => $"Key 0x{usage:X2}",
        };
    }

    /// <summary>
    /// "Profile 1" for every profile, unless the mouse stores a real name of its own
    /// (Armoury writes "Profile1" etc. on the original Spatha, which counts as no name).
    /// </summary>
    public static string ProfileName(int profile, string? stored)
    {
        string n = (stored ?? "").Trim();
        if (n.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(n, @"^profile\s*\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return $"Profile {profile + 1}";
        return n;
    }

    /// <summary>A combo label without the carrier key: "Alt+Tab (F13)" -> "Alt+Tab", for the button map.</summary>
    public static string WithoutCarrier(string label) =>
        System.Text.RegularExpressions.Regex.Replace(label, @"\s\(F(1[3-9]|2[0-4])\)$", "");

    public static string ProfileSwitchText(int profile) =>
        ProfileSwitchButton(profile) is string side ? $"DPI button + {Name(side)}" : "";
}
