using System.Drawing;
using MouseHud.Core.Mouse;

namespace MouseHud.Core.Devices;

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
        Mouse.ButtonSlots.OrderBy(kv => kv.Key).Select(kv => new ButtonSlot(kv.Key, kv.Value.Name)).ToList();

    private static ButtonAction Action(ushort code, string? label = null) =>
        new($"{code:X4}", label ?? AsusMouse.LabelForActionCode(code), code);

    public IReadOnlyList<ActionGroup> ActionCatalog =>
        Mouse.BindingGroups.Select(g => new ActionGroup(g.GroupLabel,
            g.Items.Select(i => Action(i.Code, i.Name)).ToList())).ToList();

    public ButtonAction? GetBinding(int slot)
    {
        if (!Mouse.ButtonBindingsReady || slot < 0 || slot >= Mouse.ButtonBindings.Length) return null;
        ushort code = Mouse.ButtonBindings[slot];
        var known = ActionCatalog.SelectMany(g => g.Actions).FirstOrDefault(a => (ushort)a.Payload == code);
        return known ?? Action(code);
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

    /// <summary>The Spatha X sends combos as F13-F24 carriers that ComboDispatcher replays.</summary>
    public bool SupportsShortcuts => false;
    public ButtonAction? MakeShortcut(Shortcut shortcut) => null;

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
                    MaxBrightness = m.MaxBrightness(),
                    State = new LightState
                    {
                        Effect = Math.Max(0, Array.FindIndex(modes, x => x.Mode == s.LightingMode)),
                        Colors = new[] { s.RGBColor },
                        Brightness = s.Brightness,
                        Speed = s.AnimationSpeed switch { AnimationSpeed.Slow => 0, AnimationSpeed.Fast => 2, _ => 1 },
                        Random = s.RandomColor,
                    },
                };
            }).ToList();
        }
    }

    public void SetLighting(int zone, LightState s)
    {
        var z = Mouse.SupportedLightingZones()[zone];
        var modes = ModesFor(z);
        var current = Mouse.LightingSettingForZone(z) ?? new LightingSetting();
        Mouse.SetLightingSetting(new LightingSetting
        {
            LightingMode = modes[Math.Clamp(s.Effect, 0, modes.Length - 1)].Mode,
            RGBColor = s.Colors.Length > 0 ? s.Colors[0] : current.RGBColor,
            Brightness = s.Brightness,
            RandomColor = s.Random,
            AnimationSpeed = s.Speed switch { 0 => AnimationSpeed.Slow, 2 => AnimationSpeed.Fast, _ => AnimationSpeed.Medium },
            AnimationDirection = current.AnimationDirection,
        }, z);
    }

    public void Dispose() => Mouse.Dispose();
}
