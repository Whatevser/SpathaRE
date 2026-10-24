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

    public static string ProfileSwitchText(int profile) =>
        ProfileSwitchButton(profile) is string side ? $"DPI button + {Name(side)}" : "";
}
