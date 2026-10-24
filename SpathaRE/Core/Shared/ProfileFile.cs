// Shared by both mice: the export/import file format (buttons and DPI).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpathaRE.Core.Shared;

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
