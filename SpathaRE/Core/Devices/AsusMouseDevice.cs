using System.Drawing;
using SpathaRE.Core.Mouse;

namespace SpathaRE.Core.Devices;

/// <summary>
/// Adapter that exposes G-Helper's AsusMouse (Spatha X) through IMouseDevice.
/// The protocol code underneath is used as-is.
/// </summary>
public sealed class AsusMouseDevice : IMouseDevice
{
    public AsusMouse Mouse { get; }

    public event EventHandler? ExternalChange;

    public AsusMouseDevice(AsusMouse mouse)
    {
        Mouse = mouse;
        mouse.BatteryUpdated += (_, _) => ExternalChange?.Invoke(this, EventArgs.Empty);
    }

    public string Name => Mouse.GetDisplayName().Replace(" (Wireless)", "").Replace(" (Wired)", "");
    public DeviceModel Model => DeviceModel.SpathaX;
    public string ConnectionLabel => Mouse.Wireless ? "Wireless" : "Wired";
    public bool IsReady => Mouse.IsDeviceReady;

    public bool HasBattery => Mouse.HasBattery();
    public int Battery => Mouse.HasBattery() ? Mouse.Battery : -1;
    public bool Charging => Mouse.Charging;

    public int ProfileCount => Mouse.HasProfiles() ? Mouse.ProfileCount() : 1;
    public int Profile => Mouse.Profile;
    public bool IsProfileReadOnly(int profile) => false;
    public string ProfileName(int profile) => $"Profile {profile + 1}";

    public void Synchronize() => Mouse.SynchronizeDevice();
    public void ReadBattery() => Mouse.ReadBattery();

    public void SetProfile(int profile)
    {
        Mouse.SetProfile(profile);
        Mouse.SynchronizeDevice();
    }

    /// <summary>The Spatha X has no cheap probe; a full sync is how G-Helper wakes it.</summary>
    public bool PollWake()
    {
        Mouse.SynchronizeDevice();
        return Mouse.IsDeviceReady;
    }

    public bool PollProfileChanged()
    {
        if (!Mouse.HasProfiles()) return false;
        int before = Mouse.Profile;
        Mouse.ReadProfile();
        if (Mouse.Profile == before) return false;
        Mouse.SynchronizeDevice();
        return true;
    }

    // ---- buttons ----

    public IReadOnlyList<ButtonSlot> ButtonSlots =>
        Mouse.ButtonSlots.OrderBy(kv => kv.Key).Select(kv => new ButtonSlot(kv.Key, Buttons.SlotName(Model, kv.Key))).ToList();

    public const ushort FirstCarrier = 0x0068, LastCarrier = 0x0073;
    public static bool IsCarrier(ushort code) => code >= FirstCarrier && code <= LastCarrier;
    public static string CarrierKey(ushort code) => $"F{13 + (code - FirstCarrier)}";

    /// <summary>Names shared with the original Spatha (G-Helper's own wording differs).</summary>
    private static readonly Dictionary<ushort, string> CommonNames = new()
    {
        { 0x01F0, "Left click" }, { 0x01F1, "Right click" }, { 0x01F2, "Wheel click" },
        { 0x01E3, "Double click" }, { 0x01E4, "Back" }, { 0x01E5, "Forward" },
        { 0x01E6, "DPI switch" }, { 0x01E7, "Target focus" },
        { 0x01E8, "Wheel up" }, { 0x01E9, "Wheel down" },
        { 0x01EA, "Side button 1" }, { 0x01EB, "Side button 2" }, { 0x01EC, "Side button 3" },
        { 0x01ED, "Side button 4" }, { 0x01EE, "Side button 5" }, { 0x01EF, "Side button 6" },
        { 0x0000, "Disabled" },
        { 0x00F0, "Play / Pause" }, { 0x00F1, "Stop" }, { 0x00F2, "Next track" },
        { 0x00F3, "Previous track" }, { 0x00F4, "Mute" }, { 0x00F5, "Volume down" }, { 0x00F6, "Volume up" },
    };

    /// <summary>A combo shows the keys it types and the carrier key the mouse sends, e.g. "Alt+Tab (F13)".</summary>
    public static string Label(ushort code)
    {
        if (IsCarrier(code) && AsusMouse.CombosByCode.TryGetValue(code, out var combo))
            return $"{Input.ComboDispatcher.Describe(combo.ResolveCommand())} ({CarrierKey(code)})";
        if (CommonNames.TryGetValue(code, out var name)) return name;
        if (code is >= 0x0004 and <= 0x00E7) return Buttons.KeyName(code);
        return AsusMouse.LabelForActionCode(code);
    }

    private static ButtonAction Action(ushort code) => new($"{code:X4}", Label(code), code);

    /// <summary>G-Helper's catalogue with shared names, except Keyboard, which is the list both
    /// mice share (same keys, names and order; F13-F24 are the combos). Key codes on the
    /// Spatha X are HID usages, lone modifiers 0xE0-0xE7.</summary>
    public IReadOnlyList<ActionGroup> ActionCatalog =>
        Mouse.BindingGroups.Select(g => new ActionGroup(g.GroupLabel,
            g.GroupLabel == "Keyboard"
                ? Buttons.KeyboardUsages.Select(u => (ushort)u)
                    .Concat(Enumerable.Range(0xE0, 8).Select(u => (ushort)u))
                    .Select(Action).ToList()
                : g.Items.Select(i => Action(i.Code)).ToList())).ToList();

    public ButtonAction? GetBinding(int slot)
    {
        if (!Mouse.ButtonBindingsReady || slot < 0 || slot >= Mouse.ButtonBindings.Length) return null;
        ushort code = Mouse.ButtonBindings[slot];
        return Action(code);
    }

    public void SetBinding(int slot, ButtonAction action)
    {
        if (action.Payload is not ushort code) return;
        Mouse.SetButtonBinding(slot, code);
        Mouse.FlushSettings();
    }

    public void ResetBindings()
    {
        Mouse.ResetButtonBindings();
        Mouse.FlushSettings();
        Mouse.ReadAndLogButtonBindings();
    }


    // ---- DPI ----

    public DpiInfo Dpi
    {
        get
        {
            int count = Mouse.CanChangeDPICount() ? Mouse.CurrentDPIProfileCount : Mouse.DPIProfileCount();
            count = Math.Min(count, Mouse.DpiSettings.Length);
            var stages = new int[count];
            var colors = Mouse.HasDPIColors() ? new Color[count] : null;
            for (int i = 0; i < count; i++)
            {
                var d = Mouse.DpiSettings[i] ?? new AsusMouseDPI();
                stages[i] = (int)d.DPI;
                if (colors is not null) colors[i] = d.Color;
            }
            return new DpiInfo
            {
                Min = Mouse.MinDPI(),
                Max = Mouse.MaxDPI(),
                Step = Mouse.DPIIncrements(),
                Stages = stages,
                Colors = colors,
                ActiveStage = Mouse.DpiProfile - 1,
            };
        }
    }

    public void SetDpi(int stage, int dpi, Color? color)
    {
        var current = Mouse.DpiSettings.ElementAtOrDefault(stage) ?? new AsusMouseDPI();
        Mouse.SetDPIForProfile(new AsusMouseDPI { DPI = (uint)dpi, Color = color ?? current.Color }, stage + 1);
    }

    // ---- generic settings ----

    private static readonly (PowerOffSetting Value, string Label)[] PowerOff =
    {
        (PowerOffSetting.Minutes1, "1 min"), (PowerOffSetting.Minutes2, "2 min"),
        (PowerOffSetting.Minutes3, "3 min"), (PowerOffSetting.Minutes5, "5 min"),
        (PowerOffSetting.Minutes10, "10 min"), (PowerOffSetting.Never, "Never"),
    };

    private List<DebounceTime> DebounceValues() => Enum.GetValues<DebounceTime>()
        .Where(d => d >= Mouse.MinDebounce() && d <= Mouse.MaxDebounce()).ToList();

    public IReadOnlyList<SettingSection> Settings
    {
        get
        {
            var m = Mouse;
            if (!m.IsDeviceReady) return Array.Empty<SettingSection>();
            var perf = new List<SettingItem>();
            var rates = m.SupportedPollingrates();
            if (rates.Length > 0)
                perf.Add(new ChoiceItem("poll", "Polling rate", rates.Select(m.PollingRateDisplayString).ToList(),
                    Math.Max(0, Array.IndexOf(rates, m.PollingRate))));
            if (m.HasAngleSnapping()) perf.Add(new ToggleItem("angle", "Angle snapping", m.AngleSnapping));
            if (m.HasMotionSync()) perf.Add(new ToggleItem("motion", "Motion sync", m.MotionSync));
            if (m.HasLiftOffSetting())
                perf.Add(new ChoiceItem("liftoff", "Lift-off distance", new[] { "Low", "High" },
                    m.LiftOffDistance == LiftOffDistance.High ? 1 : 0));
            if (m.HasDebounceSetting())
            {
                var values = DebounceValues();
                perf.Add(new ChoiceItem("debounce", "Debounce", values.Select(d => $"{m.DebounceTimeInMS(d)} ms").ToList(),
                    Math.Max(0, values.IndexOf(m.Debounce))));
            }
            if (m.HasAcceleration()) perf.Add(new RangeItem("accel", "Acceleration", 0, m.MaxAcceleration(), 1, m.Acceleration));
            if (m.HasDeceleration()) perf.Add(new RangeItem("decel", "Deceleration", 0, m.MaxDeceleration(), 1, m.Deceleration));

            var power = new List<SettingItem>();
            if (m.HasAutoPowerOff())
                power.Add(new ChoiceItem("poweroff", "Sleep after", PowerOff.Select(p => p.Label).ToList(),
                    Math.Max(0, Array.FindIndex(PowerOff, p => p.Value == m.PowerOffSetting))));
            if (m.HasLowBatteryWarning())
                power.Add(new RangeItem("lowbatt", "Low battery alert", 0, m.LowBatteryWarningMax(),
                    m.LowBatteryWarningStep(), m.LowBatteryWarning, "%"));

            var sections = new List<SettingSection>();
            if (perf.Count > 0) sections.Add(new SettingSection("Performance", "Sensor & response", perf));
            if (power.Count > 0) sections.Add(new SettingSection("Power", "Power saving", power));
            return sections;
        }
    }

    public void ApplySetting(string id, int v)
    {
        var m = Mouse;
        switch (id)
        {
            case "poll": m.SetPollingRate(m.SupportedPollingrates()[v]); break;
            case "angle": m.SetAngleSnapping(v != 0); break;
            case "motion": m.SetMotionSync(v != 0); break;
            case "liftoff": m.SetLiftOffDistance(v == 1 ? LiftOffDistance.High : LiftOffDistance.Low); break;
            case "debounce": m.SetDebounce(DebounceValues()[v]); break;
            case "accel": m.SetAcceleration(v); break;
            case "decel": m.SetDeceleration(v); break;
            case "poweroff": m.SetEnergySettings(m.LowBatteryWarning, PowerOff[v].Value); break;
            case "lowbatt": m.SetEnergySettings(v, m.PowerOffSetting); break;
        }
    }

    // ---- lighting ----

    private static readonly (LightingMode Mode, string Name)[] ModeNames =
    {
        (LightingMode.Static, "Static"), (LightingMode.Breathing, "Breathing"),
        (LightingMode.ColorCycle, "Color cycle"), (LightingMode.Rainbow, "Rainbow"),
        (LightingMode.React, "Reactive"), (LightingMode.Comet, "Comet"),
        (LightingMode.BatteryState, "Battery"), (LightingMode.Off, "Off"),
    };

    private static string ZoneName(LightingZone z) => z switch
    {
        LightingZone.Logo => "Logo",
        LightingZone.Scrollwheel => "Scroll wheel",
        LightingZone.Underglow => "Underglow",
        LightingZone.Dock => "Dock",
        _ => "All",
    };

    private (LightingMode Mode, string Name)[] ModesFor(LightingZone z) =>
        ModeNames.Where(x => Mouse.IsLightingModeSupportedForZone(x.Mode, z)).ToArray();

    public IReadOnlyList<LightZone> LightZones
    {
        get
        {
            var m = Mouse;
            if (!m.HasRGB() || !m.IsDeviceReady) return Array.Empty<LightZone>();
            return m.SupportedLightingZones().Select(z =>
            {
                var modes = ModesFor(z);
                var s = m.LightingSettingForZone(z) ?? new LightingSetting();
                return new LightZone
                {
                    Name = ZoneName(z),
                    Effects = modes.Select(x => new LightEffect(x.Name,
                        m.SupportsColorSetting(x.Mode) ? 1 : 0,
                        m.SupportsAnimationSpeed(x.Mode),
                        m.SupportsRandomColor(x.Mode))).ToList(),
                    MaxBrightness = 4,
                    State = new LightState
                    {
                        Effect = Math.Max(0, Array.FindIndex(modes, x => x.Mode == s.LightingMode)),
                        Colors = new[] { s.RGBColor },
                        Brightness = LevelFromPercent(s.Brightness, m.MaxBrightness()),
                        Speed = s.AnimationSpeed switch { AnimationSpeed.Slow => 0, AnimationSpeed.Fast => 2, _ => 1 },
                        Random = s.RandomColor,
                    },
                };
            }).ToList();
        }
    }

    /// <summary>
    /// The Spatha X takes brightness as 0-100 but only shows 4 lit steps (25/50/75/100),
    /// the same as the original Spatha. Both mice therefore use levels 0-4 (0 = off);
    /// a value set elsewhere (e.g. 30) shows as the nearest level.
    /// </summary>
    private static int LevelFromPercent(int value, int max) =>
        max <= 0 ? 0 : Math.Clamp((int)Math.Round(value * 4.0 / max), 0, 4);

    public void SetLighting(int zone, LightState s)
    {
        var z = Mouse.SupportedLightingZones()[zone];
        var modes = ModesFor(z);
        var current = Mouse.LightingSettingForZone(z) ?? new LightingSetting();
        Mouse.SetLightingSetting(new LightingSetting
        {
            LightingMode = modes[Math.Clamp(s.Effect, 0, modes.Length - 1)].Mode,
            RGBColor = s.Colors.Length > 0 ? s.Colors[0] : current.RGBColor,
            Brightness = Math.Clamp(s.Brightness, 0, 4) * Mouse.MaxBrightness() / 4,
            RandomColor = s.Random,
            AnimationSpeed = s.Speed switch { 0 => AnimationSpeed.Slow, 2 => AnimationSpeed.Fast, _ => AnimationSpeed.Medium },
            AnimationDirection = current.AnimationDirection,
        }, z);
    }

    // ---- export / import ----

    private static readonly (ushort Code, string Button)[] PortableMouse =
    {
        (0x01F0, "left"), (0x01F1, "right"), (0x01F2, "middle"), (0x01E4, "back"), (0x01E5, "forward"),
    };

    private static readonly (ushort Code, string Name)[] PortableMedia =
    {
        (0x00F0, "play_pause"), (0x00F1, "stop"), (0x00F2, "next"), (0x00F3, "previous"),
        (0x00F4, "mute"), (0x00F6, "volume_up"), (0x00F5, "volume_down"),
    };

    private static readonly HashSet<ushort> KeyboardCodes = Buttons.KeyboardUsages.Select(u => (ushort)u).ToHashSet();

    private void SwitchTo(int profile)
    {
        if (!Mouse.HasProfiles() || Mouse.Profile == profile) return;
        Mouse.SetProfile(profile);
        Mouse.SynchronizeDevice();
    }

    public PortableProfile ExportProfile(int profile)
    {
        SwitchTo(profile);
        var p = new PortableProfile { Name = ProfileName(profile), Index = profile };
        p.Dpi.AddRange(Dpi.Stages);
        if (Mouse.ButtonBindingsReady)
            for (int slot = 0; slot < Math.Min(Buttons.SpathaXSlots.Length, Mouse.ButtonBindings.Length); slot++)
                p.Buttons[Buttons.SpathaXSlots[slot]] = ToPortable(Mouse.ButtonBindings[slot]);
        return p;
    }

    public static PortableAction ToPortable(ushort code)
    {
        PortableAction a;
        if (code == 0x0000) a = PortableAction.Simple("disabled");
        else if (code == 0x01E6) a = PortableAction.Simple("dpi");
        else if (code == 0x01E8 || code == 0x01E9) a = PortableAction.Wheel(code == 0x01E8 ? "up" : "down");
        else if (code == 0x01E3) a = PortableAction.Simple("double_click");
        else if (code == 0x01E7) a = PortableAction.Simple("target_focus");
        else if (code >= 0x01EA && code <= 0x01EF) a = new PortableAction { Type = "side", Button = $"side{code - 0x01EA + 1}" };
        else if (PortableMouse.Any(m => m.Code == code)) a = PortableAction.Mouse(PortableMouse.First(m => m.Code == code).Button);
        else if (PortableMedia.Any(m => m.Code == code)) a = PortableAction.OfMedia(PortableMedia.First(m => m.Code == code).Name);
        else if (IsCarrier(code) && AsusMouse.CombosByCode.TryGetValue(code, out var combo)
                 && KeyCodes.FromVirtualKeys(Input.ComboDispatcher.ParseCommand(combo.ResolveCommand()).Select(k => (int)k)) is PortableAction keys)
            a = keys;
        else if (code >= 0x00E0 && code <= 0x00E7) a = PortableAction.Key((byte)(1 << (code - 0x00E0)), Array.Empty<byte>());
        else if (KeyboardCodes.Contains(code)) a = PortableAction.Key(0, new[] { (byte)code });
        else a = new PortableAction { Type = "raw", Model = "SpathaX", Raw = $"{code:X4}" };
        a.Label = Label(code);
        return a;
    }

    public IReadOnlyCollection<ushort> BoundCombos =>
        !Mouse.ButtonBindingsReady || Mouse.ButtonBindings is null ? Array.Empty<ushort>()
            : Mouse.ButtonBindings.Where(IsCarrier).ToHashSet();

    /// <summary>The key combination a carrier currently types, as modifiers + HID keys.</summary>
    public static PortableAction? CarrierKeys(ushort code) =>
        AsusMouse.CombosByCode.TryGetValue(code, out var combo)
            ? KeyCodes.FromVirtualKeys(Input.ComboDispatcher.ParseCommand(combo.ResolveCommand()).Select(k => (int)k))
            : null;

    public void ImportProfile(int profile, PortableProfile data, ImportReport report)
    {
        string where = $"Profile {profile + 1}";
        if (profile < 0 || profile >= ProfileCount) { report.Note($"{where}: the Spatha X has {ProfileCount} profiles, skipped."); return; }
        SwitchTo(profile);
        if (!Mouse.ButtonBindingsReady) throw new IOException("Button bindings couldn't be read from the mouse.");

        // Work out every slot's new code first, so combo carriers can be shared and allocated.
        var final = (ushort[])Mouse.ButtonBindings.Clone();
        var combos = new Dictionary<int, PortableAction>();      // slot -> key combination needing a carrier
        for (int slot = 0; slot < Math.Min(Buttons.SpathaXSlots.Length, final.Length); slot++)
        {
            string id = Buttons.SpathaXSlots[slot];
            if (!data.Buttons.TryGetValue(id, out var a)) continue;
            if (ToPortable(final[slot]).SameAs(a)) continue;      // already does that
            var code = FromPortable(a, out bool needsCarrier, out string? why);
            if (needsCarrier) { combos[slot] = a; continue; }
            if (code is null) { report.Note($"{where}: {Buttons.Name(id)}: {why}, left unchanged."); continue; }
            final[slot] = code.Value;
        }

        // Carriers stay put if they are bound to a slot this import doesn't touch.
        var busy = new HashSet<ushort>(report.ReservedCarriers);
        for (int slot = 0; slot < final.Length; slot++)
            if (!combos.ContainsKey(slot) && IsCarrier(final[slot])) busy.Add(final[slot]);

        foreach (var (slot, a) in combos)
        {
            ushort? carrier = AllocateCarrier(a, busy, report);
            if (carrier is null) { report.Note($"{where}: {Buttons.Name(Buttons.SpathaXSlots[slot])}: no free combo slot (F13-F24) left, left unchanged."); continue; }
            final[slot] = carrier.Value;
        }

        int changed = 0;
        for (int slot = 0; slot < final.Length; slot++)
        {
            if (final[slot] == Mouse.ButtonBindings[slot]) continue;
            Mouse.SetButtonBinding(slot, final[slot]);
            changed++;
        }

        var dpi = Dpi;
        int stages = Math.Min(data.Dpi.Count, dpi.Stages.Length);
        for (int i = 0; i < stages; i++)
        {
            int v = (int)Math.Round(Math.Clamp(data.Dpi[i], dpi.Min, dpi.Max) / (double)dpi.Step) * dpi.Step;
            if (v != data.Dpi[i]) report.Note($"{where}: DPI stage {i + 1} {data.Dpi[i]} adjusted to {v}.");
            if (v != dpi.Stages[i]) { SetDpi(i, v, null); changed++; }
        }
        if (data.Dpi.Count > dpi.Stages.Length)
            report.Note($"{where}: this mouse has {dpi.Stages.Length} DPI stages, the rest were left out.");

        if (changed > 0) Mouse.FlushSettings();
        report.Written++;
        Logger.WriteLine($"{Name}: imported into profile {profile + 1}: {changed} change(s)");
    }

    /// <summary>
    /// The combo (F13-F24 carrier) for a key combination: one that already types
    /// those keys, else the first one not in <paramref name="busy"/>, which is then
    /// set to type them. Shared by both mice. Null when all twelve are taken.
    /// </summary>
    public static ushort? AllocateCarrier(PortableAction a, HashSet<ushort> busy, ImportReport report)
    {
        ushort? carrier = null;
        for (ushort c = FirstCarrier; c <= LastCarrier && carrier is null; c++)
            if (CarrierKeys(c)?.KeySignature == a.KeySignature) carrier = c;
        var vks = KeyCodes.ToVirtualKeys(a);
        for (ushort c = FirstCarrier; c <= LastCarrier && carrier is null && vks is not null; c++)
        {
            if (busy.Contains(c)) continue;
            var combo = AsusMouse.CombosByCode[c];
            string old = Input.ComboDispatcher.Describe(combo.ResolveCommand());
            AppConfig.Set(combo.ConfigKey, string.Join(" ", vks.Select(v => $"0x{v:X2}")));
            report.Note($"Combo {CarrierKey(c)} now types {Input.ComboDispatcher.Describe(combo.ResolveCommand())} " +
                        $"(was {old}). Every button that uses {CarrierKey(c)} types it too.");
            carrier = c;
        }
        if (carrier is null) return null;
        busy.Add(carrier.Value);
        report.ReservedCarriers.Add(carrier.Value);
        return carrier;
    }

    /// <summary>The Spatha X code for an action, or null. Key combinations the mouse can't send
    /// itself set <paramref name="needsCarrier"/>: they go through a combo slot (F13-F24).</summary>
    public static ushort? FromPortable(PortableAction a, out bool needsCarrier, out string? why)
    {
        needsCarrier = false;
        why = null;
        switch (a.Type)
        {
            case "disabled": return 0x0000;
            case "dpi": return 0x01E6;
            case "wheel": return a.Button == "down" ? (ushort)0x01E9 : (ushort)0x01E8;
            case "double_click": return 0x01E3;
            case "target_focus": return 0x01E7;
            case "side" when a.Button is { Length: 5 } b && b.StartsWith("side") && b[4] is >= '1' and <= '6':
                return (ushort)(0x01EA + (b[4] - '1'));
            case "mouse":
                foreach (var (code, name) in PortableMouse)
                    if (name == a.Button) return code;
                break;
            case "media":
                foreach (var (code, name) in PortableMedia)
                    if (name == a.Media) return code;
                break;
            case "key":
            {
                var keys = a.Keys ?? new List<byte>();
                if (a.Modifiers == 0 && keys.Count == 1)
                {
                    if (KeyboardCodes.Contains(keys[0])) return keys[0];
                    if (keys[0] >= 0x68 && keys[0] <= 0x73) { why = "F13-F24 are reserved for combos on the Spatha X"; return null; }
                }
                if (keys.Count == 0 && System.Numerics.BitOperations.PopCount(a.Modifiers) == 1)
                    return (ushort)(0x00E0 + System.Numerics.BitOperations.TrailingZeroCount(a.Modifiers));
                if (keys.Count == 0 && a.Modifiers == 0) { why = "empty key combination"; return null; }
                if (KeyCodes.ToVirtualKeys(a) is null) { why = $"{a.Label ?? "a key"} has no Windows key code"; return null; }
                needsCarrier = true;
                return null;
            }
            case "raw" when a.Model == "SpathaX" && a.Raw?.Length == 4:
                return Convert.ToUInt16(a.Raw, 16);
        }
        why = $"\"{a.Label ?? a.Type}\" isn't supported on the Spatha X";
        return null;
    }

    public void Dispose() => Mouse.Dispose();
}
