using System.Drawing;

namespace SpathaRE.Core.Devices;

/// <summary>Which physical mouse a device is; selects the button-map artwork.</summary>
public enum DeviceModel
{
    Spatha,
    SpathaX,
}

/// <summary>
/// The UI-facing view of a mouse. Both the original Spatha (own protocol) and
/// the Spatha X (G-Helper's AsusMouse) implement it, so every page of the
/// settings window is shared.
///
/// Methods are synchronous and may block on USB; MouseManager runs them off
/// the UI thread and serialises them.
/// </summary>
public interface IMouseDevice : IDisposable
{
    string Name { get; }
    DeviceModel Model { get; }
    /// <summary>Raised from a background thread when the mouse reports a change by itself.</summary>
    event EventHandler? ExternalChange;
    /// <summary>"Wired", "Wireless", ...</summary>
    string ConnectionLabel { get; }
    bool IsReady { get; }

    bool HasBattery { get; }
    /// <summary>Percent, or -1 when unknown.</summary>
    int Battery { get; }
    bool Charging { get; }

    int ProfileCount { get; }
    /// <summary>0-based active onboard profile.</summary>
    int Profile { get; }
    bool IsProfileReadOnly(int profile);
    string ProfileName(int profile);

    /// <summary>Full re-read of the active profile.</summary>
    void Synchronize();
    void ReadBattery();
    void SetProfile(int profile);
    /// <summary>Cheap check for a profile switch made on the mouse itself.</summary>
    bool PollProfileChanged();
    /// <summary>While not ready: cheap check whether the mouse answers again. True when it is ready now.</summary>
    bool PollWake();

    // ---- buttons ----
    IReadOnlyList<ButtonSlot> ButtonSlots { get; }
    IReadOnlyList<ActionGroup> ActionCatalog { get; }
    ButtonAction? GetBinding(int slot);
    void SetBinding(int slot, ButtonAction action);
    void ResetBindings();
    /// <summary>Combo carriers (0x68-0x73 = F13-F24) bound in the active profile; ComboDispatcher replays them.</summary>
    IReadOnlyCollection<ushort> BoundCombos { get; }
    /// <summary>True when the mouse can send a modifier combo by itself.</summary>
    bool SupportsShortcuts { get; }
    ButtonAction? MakeShortcut(Shortcut shortcut);

    // ---- DPI ----
    DpiInfo Dpi { get; }
    void SetDpi(int stage, int dpi, Color? color);

    // ---- generic performance / power settings ----
    IReadOnlyList<SettingSection> Settings { get; }
    void ApplySetting(string id, int value);

    // ---- lighting ----
    IReadOnlyList<LightZone> LightZones { get; }
    void SetLighting(int zone, LightState state);

    // ---- export / import (buttons and DPI only) ----
    /// <summary>Reads one onboard profile. May switch the mouse to that profile; the caller switches back.</summary>
    PortableProfile ExportProfile(int profile);
    /// <summary>Writes buttons and DPI into one onboard profile. May switch the mouse to that profile.</summary>
    void ImportProfile(int profile, PortableProfile data, ImportReport report);
}

public sealed record ButtonSlot(int Slot, string Name);

/// <summary>An assignable button function. Key identifies it; Payload is device-specific.</summary>
public sealed record ButtonAction(string Key, string Label, object Payload)
{
    public bool Equals(ButtonAction? other) => other is not null && other.Key == Key;
    public override int GetHashCode() => Key.GetHashCode();
}

public sealed record ActionGroup(string Name, IReadOnlyList<ButtonAction> Actions);

/// <summary>HID modifier bitmask (1 LCtrl, 2 LShift, 4 LAlt, 8 LWin, 0x10.. right) plus up to 3 HID key usages.</summary>
public sealed record Shortcut(byte Modifiers, IReadOnlyList<byte> Keys);

public sealed class DpiInfo
{
    public int Min { get; init; } = 100;
    public int Max { get; init; } = 8200;
    public int Step { get; init; } = 50;
    public int[] Stages { get; init; } = Array.Empty<int>();
    /// <summary>Per-stage indicator colour, or null when the mouse has none.</summary>
    public Color[]? Colors { get; init; }
    /// <summary>0-based active stage, or -1 when unknown.</summary>
    public int ActiveStage { get; init; } = -1;
}

public abstract record SettingItem(string Id, string Label, string? Hint);

/// <summary>Pick one of a few options; value = option index.</summary>
public sealed record ChoiceItem(string Id, string Label, IReadOnlyList<string> Options, int Selected, string? Hint = null)
    : SettingItem(Id, Label, Hint);

/// <summary>On/off; value = 0 or 1.</summary>
public sealed record ToggleItem(string Id, string Label, bool Value, string? Hint = null)
    : SettingItem(Id, Label, Hint);

/// <summary>Numeric range; value = the number.</summary>
public sealed record RangeItem(string Id, string Label, int Min, int Max, int Step, int Value, string Unit = "", string? Hint = null)
    : SettingItem(Id, Label, Hint);

/// <summary>Page is "Performance" or "Power".</summary>
public sealed record SettingSection(string Page, string Title, IReadOnlyList<SettingItem> Items);

public sealed record LightEffect(string Name, int ColorCount, bool HasSpeed = false, bool HasRandom = false);

public sealed class LightZone
{
    public required string Name { get; init; }
    public required IReadOnlyList<LightEffect> Effects { get; init; }
    public required LightState State { get; init; }
    /// <summary>Brightness levels above off; 4 on both Spathas (25/50/75/100 %).</summary>
    public int MaxBrightness { get; init; } = 4;
    public bool HasWirelessBrightness { get; init; }
}

public sealed class LightState
{
    public int Effect { get; set; }
    public Color[] Colors { get; set; } = { Color.Red };
    public int Brightness { get; set; }
    public int WirelessBrightness { get; set; }
    /// <summary>0 slow, 1 medium, 2 fast.</summary>
    public int Speed { get; set; } = 1;
    public bool Random { get; set; }

    public LightState Clone() => new()
    {
        Effect = Effect,
        Colors = (Color[])Colors.Clone(),
        Brightness = Brightness,
        WirelessBrightness = WirelessBrightness,
        Speed = Speed,
        Random = Random,
    };
}
