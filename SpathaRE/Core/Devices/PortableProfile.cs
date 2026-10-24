using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpathaRE.Core.Devices;

/// <summary>
/// A model-neutral profile: what each physical button does and the DPI stages.
/// Both Spathas export to it and import from it, which is how a Spatha profile
/// ends up on a Spatha X and the other way round. Lighting and the other
/// settings are deliberately left out: the two models store them too
/// differently to move safely.
/// </summary>
public sealed class PortableProfile
{
    public string? Name { get; set; }
    /// <summary>0-based onboard profile it was exported from.</summary>
    public int Index { get; set; }
    public List<int> Dpi { get; set; } = new();
    /// <summary>Button id (see <see cref="Buttons"/>) -> action.</summary>
    public Dictionary<string, PortableAction> Buttons { get; set; } = new();
}

/// <summary>
/// One button function. Type is one of: mouse (Button = left/right/middle/back/forward),
/// wheel (Button = up/down), dpi, disabled, key (Modifiers + Keys as HID usages),
/// media (Media = play_pause/stop/next/previous/mute/volume_up/volume_down),
/// side (Button = side1..side6), double_click, target_focus, raw (Raw + Model).
/// </summary>
public sealed class PortableAction
{
    public string Type { get; set; } = "disabled";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Button { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public byte Modifiers { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<byte>? Keys { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Media { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Raw { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Model { get; set; }
    /// <summary>What the source app showed; informational only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Label { get; set; }

    public static PortableAction Mouse(string b) => new() { Type = "mouse", Button = b };
    public static PortableAction Wheel(string dir) => new() { Type = "wheel", Button = dir };
    public static PortableAction Simple(string type) => new() { Type = type };
    public static PortableAction Key(byte mods, IEnumerable<byte> keys) =>
        new() { Type = "key", Modifiers = mods, Keys = keys.Where(k => k != 0).ToList() };
    public static PortableAction OfMedia(string m) => new() { Type = "media", Media = m };

    /// <summary>Number of keys pressed together (modifiers included).</summary>
    [JsonIgnore]
    public int KeyCount => System.Numerics.BitOperations.PopCount(Modifiers) + (Keys?.Count ?? 0);

    /// <summary>Same function, ignoring labels and encoding details.</summary>
    public bool SameAs(PortableAction o) =>
        Type == o.Type && Button == o.Button && Media == o.Media && Raw == o.Raw
        && (Type != "key" || KeySignature == o.KeySignature);

    /// <summary>Canonical text used to compare two key actions.</summary>
    [JsonIgnore]
    public string KeySignature => $"{Modifiers:X2}:{string.Join(",", (Keys ?? new()).OrderBy(k => k))}";
}

/// <summary>The file written by Export: one profile or a whole set.</summary>
public sealed class ProfileFile
{
    public const string FormatName = "SpathaRE profiles";

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = 1;
    /// <summary>Which mouse it came from ("Spatha" or "SpathaX").</summary>
    public string Model { get; set; } = "";
    public List<PortableProfile> Profiles { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ProfileFile Parse(string json)
    {
        var f = JsonSerializer.Deserialize<ProfileFile>(json, Options)
                ?? throw new InvalidDataException("Empty file.");
        if (f.Format != FormatName) throw new InvalidDataException("Not a SpathaRE profile file.");
        if (f.Profiles.Count == 0) throw new InvalidDataException("The file holds no profiles.");
        return f;
    }
}

/// <summary>What an import did, for the summary shown afterwards.</summary>
public sealed class ImportReport
{
    public List<string> Notes { get; } = new();
    public int Written { get; set; }
    /// <summary>Spatha X combo slots (F13-F24 carriers) given a new key combination during this import.</summary>
    public HashSet<ushort> ReservedCarriers { get; } = new();
    public void Note(string s) => Notes.Add(s);
}

/// <summary>HID keyboard usages (what the mice send) and Windows virtual keys (what SpathaRE replays).</summary>
public static class KeyCodes
{
    /// <summary>Modifier bit (1 LCtrl ... 0x80 RWin) -> virtual key.</summary>
    public static readonly int[] ModifierVk = { 0xA2, 0xA0, 0xA4, 0x5B, 0xA3, 0xA1, 0xA5, 0x5C };

    private static readonly Dictionary<int, byte> VkToHid = BuildVkToHid();
    private static readonly Dictionary<byte, int> HidToVk = VkToHid
        .GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    private static Dictionary<int, byte> BuildVkToHid()
    {
        var m = new Dictionary<int, byte>();
        for (int i = 0; i < 26; i++) m[0x41 + i] = (byte)(0x04 + i);      // A-Z
        for (int i = 0; i < 9; i++) m[0x31 + i] = (byte)(0x1E + i);       // 1-9
        m[0x30] = 0x27;                                                    // 0
        for (int i = 0; i < 12; i++) m[0x70 + i] = (byte)(0x3A + i);      // F1-F12
        for (int i = 0; i < 12; i++) m[0x7C + i] = (byte)(0x68 + i);      // F13-F24
        for (int i = 0; i < 9; i++) m[0x61 + i] = (byte)(0x59 + i);       // Num 1-9
        (int Vk, byte Hid)[] rest =
        {
            (0x0D, 0x28), (0x1B, 0x29), (0x08, 0x2A), (0x09, 0x2B), (0x20, 0x2C),
            (0xBD, 0x2D), (0xBB, 0x2E), (0xDB, 0x2F), (0xDD, 0x30), (0xDC, 0x31),
            (0xBA, 0x33), (0xDE, 0x34), (0xC0, 0x35), (0xBC, 0x36), (0xBE, 0x37), (0xBF, 0x38),
            (0x14, 0x39), (0x2C, 0x46), (0x91, 0x47), (0x13, 0x48), (0x2D, 0x49), (0x24, 0x4A),
            (0x21, 0x4B), (0x2E, 0x4C), (0x23, 0x4D), (0x22, 0x4E), (0x27, 0x4F), (0x25, 0x50),
            (0x28, 0x51), (0x26, 0x52), (0x90, 0x53), (0x6F, 0x54), (0x6A, 0x55), (0x6D, 0x56),
            (0x6B, 0x57), (0x60, 0x62), (0x6E, 0x63), (0x5D, 0x65),
        };
        foreach (var (vk, hid) in rest) m[vk] = hid;
        return m;
    }

    /// <summary>Virtual key -> modifier bit, or 0. Generic Ctrl/Shift/Alt count as the left key.</summary>
    public static byte ModifierBit(int vk)
    {
        int i = Array.IndexOf(ModifierVk, vk);
        if (i >= 0) return (byte)(1 << i);
        return vk switch { 0x11 => 0x01, 0x10 => 0x02, 0x12 => 0x04, _ => 0 };
    }

    public static byte? Hid(int vk) => VkToHid.TryGetValue(vk, out var h) ? h : null;
    public static int? Vk(byte hid) => HidToVk.TryGetValue(hid, out var v) ? v : null;

    /// <summary>Virtual keys (as a combo command lists them) -> modifiers + HID keys. Null if a key has no HID code.</summary>
    public static PortableAction? FromVirtualKeys(IEnumerable<int> vks)
    {
        byte mods = 0;
        var keys = new List<byte>();
        foreach (int vk in vks)
        {
            byte bit = ModifierBit(vk);
            if (bit != 0) { mods |= bit; continue; }
            if (Hid(vk) is not byte h) return null;
            if (!keys.Contains(h)) keys.Add(h);
        }
        return PortableAction.Key(mods, keys);
    }

    /// <summary>Modifiers + HID keys -> virtual keys in press order (modifiers first).</summary>
    public static List<int>? ToVirtualKeys(PortableAction a)
    {
        var list = new List<int>();
        for (int i = 0; i < 8; i++)
            if ((a.Modifiers & (1 << i)) != 0) list.Add(ModifierVk[i]);
        foreach (byte k in a.Keys ?? new())
        {
            if (Vk(k) is not int vk) return null;
            list.Add(vk);
        }
        return list;
    }
}
