// Shared by both mice: converts between HID key codes (what the mice store) and
// Windows virtual keys (what SpathaRE types for combos).

namespace SpathaRE.Core.Shared;

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
