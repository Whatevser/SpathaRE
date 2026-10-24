namespace SpathaRE.Core.Spatha;

/// <summary>
/// Memory layout and value encodings of the original ROG Spatha (0x181C / dock
/// 0x1824). Reverse engineered from Armoury Crate's IfSpatha.dll and confirmed
/// on hardware; see SpathaRE/PROTOCOL.md for the full write-up.
/// </summary>
public static class SpathaCodec
{
    public const byte RegProfile = 0x19;          // (count << 4) | active
    public const int ProfileCount = 6;
    public const int DefaultProfile = 5;          // "Default": Armoury never writes it

    public static int ProfileBase(int profile) => 0x100 * (profile + 1);

    // ---- offsets inside a profile block ----
    public const int OffPollWired = 0x00;
    public const int OffAngleSnap = 0x01;
    public const int OffDpi1X = 0x04, OffDpi1Y = 0x05, OffDpi2X = 0x06, OffDpi2Y = 0x07;
    public const int OffAccel = 0x0C;             // low nibble accel, high nibble decel
    public const int OffButtons = 0x83;           // 14 slots x 5 bytes
    public const int ButtonSlots = 14;
    public const int OffButtonResponse = 0xC9;
    public const int OffSleep = 0xCA;
    public const int OffLowBattery = 0xCB;
    public const int OffPollWireless = 0xCC;
    public const int OffName = 0xD1;              // length byte, then UTF-16LE
    public const int BlockReadLength = 0xF0;      // enough for settings + name

    /// <summary>Zone -> offset of its 38-byte block: effect, brightness, 12 x RGB.</summary>
    public static readonly (string Name, int Offset)[] LedZones =
    {
        ("Logo", 0x5D),
        ("Scroll wheel", 0x37),
        ("Base", 0x11),
    };

    // ---- DPI: ADNS-9800 counts in 50 CPI steps ----
    public const int DpiMin = 50, DpiMax = 8200, DpiStep = 50;
    public static int DpiFromRaw(byte raw) => (raw + 1) * 50;
    public static byte RawFromDpi(int dpi) => (byte)Math.Clamp(dpi / 50 - 1, 0, 163);

    // ---- polling: stored as report interval in ms ----
    public static readonly int[] PollingHz = { 125, 250, 500, 1000 };
    private static readonly byte[] PollingCode = { 8, 4, 2, 1 };
    public static int PollIndex(byte raw) => Array.IndexOf(PollingCode, (byte)(raw & 0x0F));
    public static byte PollCode(int index) => PollingCode[index];

    // ---- angle snapping: Armoury writes 0x80|1 for on and 0x06 for off ----
    public static bool AngleSnapOn(byte raw) => (raw & 0x80) != 0;
    public static byte AngleSnapRaw(bool on) => on ? (byte)0x81 : (byte)0x06;

    // ---- button response: 16 + 8*index ----
    public static int ButtonResponseIndex(byte raw) => Math.Clamp((raw - 16 + 4) / 8, 0, 6);
    public static byte ButtonResponseRaw(int index) => (byte)(16 + 8 * index);

    // ---- power ----
    public static readonly byte[] SleepValues = { 1, 2, 3, 5, 10, 0xFF };
    public static readonly string[] SleepLabels = { "1 min", "2 min", "3 min", "5 min", "10 min", "Never" };
    public static readonly byte[] LowBatteryValues = { 10, 15, 20, 25, 30 };

    // ---- lighting ----
    /// <summary>Armoury order. Raw effect byte and number of colours used.</summary>
    public static readonly (string Name, byte Raw, int Colors)[] Effects =
    {
        ("Static", 1, 1),
        ("Breathing", 10, 2),
        ("Color cycle", 5, 12),
        ("Random", 6, 0),
        ("Reactive", 12, 1),
        ("Battery", 11, 0),
    };
    public static readonly byte[] LevelSteps = { 0, 4, 7, 11, 14 };

    public static int EffectIndex(byte raw) => Array.FindIndex(Effects, e => e.Raw == raw);
    public static int LevelIndex(int nibble)
    {
        int best = 0;
        for (int i = 0; i < LevelSteps.Length; i++)
            if (Math.Abs(LevelSteps[i] - nibble) < Math.Abs(LevelSteps[best] - nibble)) best = i;
        return best;
    }

    // ---- battery (C4 11 00) ----
    /// <summary>Armoury lowers readings taken while charging (IfSpatha.dll).</summary>
    public static int BatteryPercent(int raw, bool charging)
    {
        if (!charging || raw >= 90) return raw;
        if (raw >= 80) return raw - 5;
        if (raw >= 70) return raw - 10;
        if (raw >= 65) return raw - 15;
        return raw switch { 60 => 50, 55 => 45, 50 => 45, 45 => 40, _ => raw };
    }

    // ---- buttons: 5 bytes per slot ----
    /// <summary>Slot names, shared with the Spatha X (see Core/Devices/Buttons.cs). The side grid is
    /// numbered 1-2-3 / 4-5-6 as on the art; factory keys 7 8 9 / 0 - =.</summary>
    public static readonly string[] SlotNames =
    {
        "Left click", "Right click", "Wheel click", "Back", "Forward", "DPI button",
        "Side button 3", "Side button 6", "Side button 2", "Side button 5", "Side button 1", "Side button 4",
        "Wheel up", "Wheel down",
    };

    public static byte[] Mouse(byte mask) => new byte[] { 0x01, mask, 0, 0, 0 };
    public static byte[] Media(byte usage) => new byte[] { 0x03, usage, 0, 0, 0 };
    public static byte[] Key(byte modifiers, params byte[] keys)
    {
        var b = new byte[] { 0x02, modifiers, 0, 0, 0 };
        for (int i = 0; i < Math.Min(3, keys.Length); i++) b[2 + i] = keys[i];
        return b;
    }
    public static readonly byte[] DpiButton = { 0x0B, 0, 0, 0, 0 };
    public static readonly byte[] WheelUp = { 0x00, 0x00, 0, 0, 0 };
    public static readonly byte[] WheelDown = { 0x00, 0x01, 0, 0, 0 };
    public static readonly byte[] Disabled = { 0xFF, 0, 0, 0, 0 };

    public static readonly (string Name, byte Usage)[] MediaKeys =
    {
        // Same order as the Spatha X's Multimedia list.
        ("Volume up", 0xE9), ("Volume down", 0xEA), ("Next track", 0xB5), ("Previous track", 0xB6),
        ("Mute", 0xE2), ("Play / Pause", 0xCD), ("Stop", 0xB7),
    };

    private static readonly (byte Mask, string Name)[] MouseNames =
    {
        (1, "Left click"), (2, "Right click"), (4, "Wheel click"), (8, "Back"), (16, "Forward"),
    };

    /// <summary>HID keyboard usage -> name (shared with the Spatha X).</summary>
    public static string KeyName(byte usage) => Devices.Buttons.KeyName(usage);

    public static string ModifiersText(byte mods)
    {
        var parts = new List<string>();
        for (int i = 0; i < 8; i++)
            if ((mods & (1 << i)) != 0 && !parts.Contains(Devices.Buttons.ModifierShortNames[i]))
                parts.Add(Devices.Buttons.ModifierShortNames[i]);
        return string.Join("+", parts);
    }

    /// <summary>Human-readable label for a 5-byte slot.</summary>
    public static string Describe(ReadOnlySpan<byte> span) => Describe(span.ToArray());

    public static string Describe(byte[] b)
    {
        switch (b[0])
        {
            case 0xFF:
                return "Disabled";
            case 0x0B:
                return "DPI switch";
            case 0x00 when b[1] <= 1:
                return b[1] == 0 ? "Wheel up" : "Wheel down";
            case 0x01:
            {
                var names = MouseNames.Where(m => (b[1] & m.Mask) != 0).Select(m => m.Name).ToList();
                return names.Count > 0 ? string.Join(" + ", names) : $"Mouse 0x{b[1]:X2}";
            }
            case 0x03:
            {
                foreach (var (name, usage) in MediaKeys)
                    if (usage == b[1]) return name;
                return $"Media 0x{b[1]:X2}";
            }
            case 0x02 when b[2] == 0 && b[3] == 0 && b[4] == 0 && System.Numerics.BitOperations.PopCount(b[1]) == 1:
                // A lone modifier, named like the key list: "Left Shift".
                return Devices.Buttons.ModifierKeyNames[System.Numerics.BitOperations.TrailingZeroCount(b[1])];
            case 0x02:
            {
                var parts = new List<string>();
                string mods = ModifiersText(b[1]);
                if (mods.Length > 0) parts.Add(mods);
                for (int i = 2; i < 5; i++)
                    if (b[i] != 0) parts.Add(KeyName(b[i]));
                return parts.Count > 0 ? string.Join("+", parts) : "No key";
            }
            default:
                return "Raw " + Convert.ToHexString(b);
        }
    }

    /// <summary>
    /// Armoury's built-in factory profile, offsets 0x00-0xCC (what its
    /// "reset profile" writes). Used to restore default button bindings.
    /// </summary>
    public static readonly byte[] FactoryProfile = Convert.FromHexString(
        "02008b20202008083232808000000000" +
        "ff0a9f00ff0000ff0000ff0000ff0000" +
        "ff0000ff0000ff0000ff0000ff0000ff" +
        "0000ff0000ff000a9f00ff0000ff0000" +
        "ff0000ff0000ff0000ff0000ff0000ff" +
        "0000ff0000ff0000ff0000ff000a9f00" +
        "ff0000ff0000ff0000ff0000ff0000ff" +
        "0000ff0000ff0000ff0000ff0000ff00" +
        "00ff0001010000000102000000010400" +
        "0000010800000001100000000b000000" +
        "00020026000002002e00000200250000" +
        "02002d00000200240000020027000000" +
        "000000000001270000180a1402");
}
