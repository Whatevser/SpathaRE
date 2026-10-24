// Spatha L701 (original): the mouse as the UI sees it (IMouseDevice), plus export/
// import and the events the dock sends.

using System.Drawing;
using System.Text;
using HidSharp;
using SpathaRE.Core.Shared;
using static SpathaRE.Core.SpathaL701.L701Codec;

namespace SpathaRE.Core.SpathaL701;

/// <summary>
/// The original ROG Spatha (2016), wired or through its dock. Everything is
/// read from and written to the mouse's own memory, the same way Armoury Crate
/// does it: settings live in 6 onboard profiles of 256 bytes, the UI edits the
/// active one, and every save ends with the EnableSetting commit.
/// </summary>
public sealed class L701Device : IMouseDevice
{
    private readonly L701Protocol _p;
    private readonly bool _wired;
    private readonly string? _eventPath;

    private byte[] _block = new byte[BlockReadLength];
    private int _profile;
    private int _profileCount = L701Codec.ProfileCount;
    private string _profileName = "";
    private int _activeDpiStage = -1;

    private readonly List<Thread> _eventThreads = new();
    private readonly List<HidStream> _eventStreams = new();
    private volatile bool _running;

    // Wireless through the dock is patchy: single requests time out now and then
    // while the mouse itself works fine. Only call it asleep after it has been
    // silent for a while, and count anything it sends as a sign of life.
    private bool _synced;
    private DateTime _lastContact = DateTime.UtcNow;
    /// <summary>How long the mouse may stay silent before it is shown as asleep.</summary>
    public static TimeSpan AsleepAfter { get; set; } = TimeSpan.FromSeconds(60);

    public event EventHandler? ExternalChange;

    public L701Device(L701Protocol protocol, bool wired, string? eventDevicePath = null)
    {
        _p = protocol;
        _wired = wired;
        _eventPath = eventDevicePath;
    }

    public string Name => "ROG Spatha";
    public DeviceModel Model => DeviceModel.Spatha;
    public string ConnectionLabel => _wired ? "Wired" : "Wireless";
    public bool IsReady { get; private set; }

    /// <summary>Through the dock always; over the cable (where it charges) only if the mouse answers the battery query.</summary>
    public bool HasBattery => !_wired || !_noBatteryOverCable;
    public int Battery { get; private set; } = -1;
    public bool Charging { get; private set; }

    public int ProfileCount => _profileCount;
    public int Profile => _profile;

    /// <summary>Profile 6 is "Default", which Armoury never writes. Same rule here,
    /// unless "spatha_allow_default_profile" is set in config.</summary>
    public bool IsProfileReadOnly(int profile) =>
        profile == DefaultProfile && !AppConfig.GetBool("spatha_allow_default_profile");

    public string ProfileName(int profile) =>
        profile == DefaultProfile ? "Default"
        : Shared.Buttons.ProfileName(profile, profile == _profile ? _profileName : null);

    private int Base => ProfileBase(_profile);

    // ---- sync ----------------------------------------------------------

    public void Synchronize()
    {
        byte reg = _p.ReadByte(RegProfile);
        _profileCount = Math.Clamp(reg >> 4, 1, L701Codec.ProfileCount);
        _profile = Math.Clamp(reg & 0x0F, 0, _profileCount - 1);
        _block = _p.Read(Base, BlockReadLength);
        _profileName = DecodeName(_block);
        _synced = true;
        Contact();
        if (HasBattery) ReadBattery();
        StartEvents();
    }

    /// <summary>The mouse answered or sent something.</summary>
    private void Contact()
    {
        _lastContact = DateTime.UtcNow;
        if (_synced) IsReady = true;
    }

    private void Missed()
    {
        if (DateTime.UtcNow - _lastContact > AsleepAfter) IsReady = false;
    }

    private static string DecodeName(byte[] block)
    {
        int len = block[OffName];
        if (len == 0 || len == 0xFF || OffName + 1 + len > block.Length) return "";
        try { return Encoding.Unicode.GetString(block, OffName + 1, len & ~1).TrimEnd('\0'); }
        catch { return ""; }
    }

    /// <summary>Set once the cable connection didn't answer the battery query, so it isn't asked every poll.</summary>
    private bool _noBatteryOverCable;

    public void ReadBattery()
    {
        if (_noBatteryOverCable) return;
        var b = _p.ReadBattery();
        if (b is null)
        {
            // Over the cable the mouse may not answer at all; through the dock a missed
            // reply is just patchy radio, so the last reading is kept.
            if (_wired) { _noBatteryOverCable = true; Logger.WriteLine("Spatha: no battery reply over the cable"); }
            else Missed();
            return;
        }
        Contact();
        if (b.Value.Percent != Battery || b.Value.Charging != Charging)
            Logger.WriteLine($"Spatha: battery {b.Value.Percent}% (raw {b.Value.Raw}){(b.Value.Charging ? ", charging" : "")}");
        Battery = b.Value.Percent;
        Charging = b.Value.Charging;
    }

    public bool PollProfileChanged()
    {
        byte reg;
        try { reg = _p.ReadByte(RegProfile, rounds: 2); }
        catch (TimeoutException)
        {
            Missed();
            return false;
        }
        Contact();
        int now = reg & 0x0F;
        if (now == _profile) return false;
        Synchronize();
        return true;
    }

    public bool PollWake()
    {
        if (!_synced)
        {
            Synchronize();
            return IsReady;
        }
        byte reg;
        try { reg = _p.ReadByte(RegProfile, rounds: 1); }
        catch (TimeoutException) { return false; }

        // Back. The settings read before it fell asleep are still valid unless the
        // profile moved, so this costs one request instead of a full re-read.
        Contact();
        if ((reg & 0x0F) != _profile) Synchronize();
        else if (HasBattery) ReadBattery();
        return IsReady;
    }

    public void SetProfile(int profile)
    {
        if (profile < 0 || profile >= _profileCount) return;
        _p.SetActiveProfile(profile);
        _p.Commit();
        Synchronize();
    }

    /// <summary>Writes the changed bytes of the active profile, verifies, commits.</summary>
    private void WriteProfile(Action<byte[]> edit)
    {
        if (IsProfileReadOnly(_profile))
            throw new InvalidOperationException("The Default profile is read-only.");

        var wanted = (byte[])_block.Clone();
        edit(wanted);

        int first = -1, last = -1;
        for (int i = 0; i < wanted.Length; i++)
        {
            if (wanted[i] == _block[i]) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return;

        int changed = _p.WriteDiff(Base + first, _block.AsSpan(first, last - first + 1),
                                   wanted.AsSpan(first, last - first + 1));

        // Read the touched span back so a write the mouse dropped never goes unnoticed.
        var check = _p.Read(Base + first, last - first + 1);
        for (int i = 0; i < check.Length; i++)
            if (check[i] != wanted[first + i])
            {
                _block = _p.Read(Base, BlockReadLength);
                throw new IOException($"Verify failed at offset 0x{first + i:X2}");
            }

        _block = wanted;
        _p.Commit();
        Logger.WriteLine($"Spatha: profile {_profile + 1}: {changed} byte(s) written");
    }

    // ---- buttons -------------------------------------------------------

    public IReadOnlyList<ButtonSlot> ButtonSlots { get; } =
        Enumerable.Range(0, L701Codec.ButtonSlots).Select(i => new ButtonSlot(i, SlotNames[i])).ToList();

    // ---- combos: same model as the Spatha X ----
    // A combo button sends one of twelve spare keys, F13-F24 (HID 0x68-0x73), and
    // SpathaRE types the combination. The twelve combos and their keys are shared
    // with the Spatha X (config mouse_combo_F13..F24), so both mice list and edit
    // them the same way. The app fills in these two hooks at startup.

    public const byte FirstCarrier = 0x68, LastCarrier = 0x73;

    /// <summary>Carrier HID usage -> label like "Alt+Tab (F13)".</summary>
    public static Func<byte, string>? ComboLabel { get; set; }
    /// <summary>Carrier HID usage -> the keys it types, for export.</summary>
    public static Func<byte, PortableAction?>? ComboKeys { get; set; }
    /// <summary>Import: key combination -> a combo carrier (existing or newly set up), avoiding the busy ones.</summary>
    public static Func<PortableAction, HashSet<ushort>, ImportReport, ushort?>? AllocateCombo { get; set; }

    /// <summary>The carrier usage when the slot is a plain F13-F24 key press, else 0.</summary>
    public static byte CarrierOf(byte[] b) =>
        b.Length == 5 && b[0] == 0x02 && b[1] == 0 && b[2] >= FirstCarrier && b[2] <= LastCarrier && b[3] == 0 && b[4] == 0
            ? b[2] : (byte)0;

    private static string Label(byte[] bytes)
    {
        byte c = CarrierOf(bytes);
        if (c != 0) return ComboLabel?.Invoke(c) ?? KeyName(c);
        return Describe(bytes);
    }

    private static ButtonAction Action(byte[] bytes, string? label = null) =>
        new(Convert.ToHexString(bytes), label ?? Label(bytes), bytes);

    /// <summary>Same groups, names and order as the Spatha X. Rebuilt on every read so combo labels stay current.</summary>
    public IReadOnlyList<ActionGroup> ActionCatalog => BuildCatalog();

    private static IReadOnlyList<ActionGroup> BuildCatalog()
    {
        var mouse = new List<ButtonAction>
        {
            Action(L701Codec.Mouse(1)), Action(L701Codec.Mouse(2)), Action(L701Codec.Mouse(4)),
            Action(L701Codec.Mouse(8)), Action(L701Codec.Mouse(16)),
            Action(DpiButton), Action(WheelUp), Action(WheelDown), Action(Disabled),
        };
        var combos = Enumerable.Range(FirstCarrier, LastCarrier - FirstCarrier + 1)
            .Select(u => Action(Key(0, (byte)u))).ToList();
        var media = MediaKeys.Select(m => Action(L701Codec.Media(m.Usage), m.Name)).ToList();

        // The shared Keyboard list (same keys, names and order as the Spatha X), then lone modifiers.
        var keys = Shared.Buttons.KeyboardUsages.Select(u => Action(Key(0, u))).ToList();
        for (int i = 0; i < 8; i++) keys.Add(Action(Key((byte)(1 << i))));

        return new List<ActionGroup>
        {
            new("Mouse", mouse),
            new("Combos", combos),
            new("Multimedia", media),
            new("Keyboard", keys),
        };
    }

    public IReadOnlyCollection<ushort> BoundCombos => !IsReady ? Array.Empty<ushort>() :
        Enumerable.Range(0, L701Codec.ButtonSlots)
            .Select(i => CarrierOf(_block.AsSpan(OffButtons + 5 * i, 5).ToArray()))
            .Where(c => c != 0).Select(c => (ushort)c).ToHashSet();

    public ButtonAction? GetBinding(int slot)
    {
        if (!IsReady || slot < 0 || slot >= ButtonSlots.Count) return null;
        var bytes = _block.AsSpan(OffButtons + 5 * slot, 5).ToArray();
        return Action(bytes);
    }

    public void SetBinding(int slot, ButtonAction action)
    {
        if (action.Payload is not byte[] bytes || bytes.Length != 5) return;
        WriteProfile(b => bytes.CopyTo(b, OffButtons + 5 * slot));
    }

    public void ResetBindings() => WriteProfile(b =>
        Array.Copy(FactoryProfile, OffButtons, b, OffButtons, 5 * L701Codec.ButtonSlots));

    // ---- DPI -----------------------------------------------------------

    public DpiInfo Dpi => new()
    {
        Min = DpiMin,
        Max = DpiMax,
        Step = DpiStep,
        Stages = IsReady
            ? new[] { DpiFromRaw(_block[OffDpi1X]), DpiFromRaw(_block[OffDpi2X]) }
            : Array.Empty<int>(),
        ActiveStage = _activeDpiStage,
    };

    public void SetDpi(int stage, int dpi, Color? color)
    {
        byte raw = RawFromDpi(dpi);
        int x = stage == 0 ? OffDpi1X : OffDpi2X;
        WriteProfile(b => { b[x] = raw; b[x + 1] = raw; });
    }

    // ---- performance / power --------------------------------------------

    public IReadOnlyList<SettingSection> Settings
    {
        get
        {
            if (!IsReady) return Array.Empty<SettingSection>();
            var hz = PollingHz.Select(h => $"{h} Hz").ToList();
            var perf = new List<SettingItem>
            {
                new ChoiceItem("poll_wired", "Polling rate (wired)", hz, PollIndex(_block[OffPollWired])),
                new ChoiceItem("poll_wireless", "Polling rate (wireless)", hz, PollIndex(_block[OffPollWireless])),
                new ToggleItem("angle", "Angle snapping", AngleSnapOn(_block[OffAngleSnap])),
                new RangeItem("accel", "Acceleration", 0, 10, 1, _block[OffAccel] & 0x0F),
                new RangeItem("decel", "Deceleration", 0, 10, 1, _block[OffAccel] >> 4),
                new ChoiceItem("btn_resp", "Button response", Enumerable.Range(1, 7).Select(i => i.ToString()).ToList(),
                    ButtonResponseIndex(_block[OffButtonResponse]), "Lower is faster. Armoury's default is 2."),
            };
            var power = new List<SettingItem>
            {
                new ChoiceItem("sleep", "Sleep after", SleepLabels,
                    Math.Max(0, Array.IndexOf(SleepValues, _block[OffSleep])), "Wireless only."),
                new ChoiceItem("lowbatt", "Low battery alert", LowBatteryValues.Select(v => $"{v}%").ToList(),
                    Array.IndexOf(LowBatteryValues, _block[OffLowBattery])),
            };
            return new[]
            {
                new SettingSection("Performance", "Sensor & response", perf),
                new SettingSection("Power", "Power saving", power),
            };
        }
    }

    public void ApplySetting(string id, int v)
    {
        WriteProfile(b =>
        {
            switch (id)
            {
                case "poll_wired": b[OffPollWired] = PollCode(v); break;
                case "poll_wireless": b[OffPollWireless] = (byte)((b[OffPollWireless] & 0xF0) | PollCode(v)); break;
                case "angle": b[OffAngleSnap] = AngleSnapRaw(v != 0); break;
                case "accel": b[OffAccel] = (byte)((b[OffAccel] & 0xF0) | Math.Clamp(v, 0, 10)); break;
                case "decel": b[OffAccel] = (byte)((b[OffAccel] & 0x0F) | (Math.Clamp(v, 0, 10) << 4)); break;
                case "btn_resp": b[OffButtonResponse] = ButtonResponseRaw(Math.Clamp(v, 0, 6)); break;
                case "sleep": b[OffSleep] = SleepValues[Math.Clamp(v, 0, SleepValues.Length - 1)]; break;
                case "lowbatt": b[OffLowBattery] = LowBatteryValues[Math.Clamp(v, 0, LowBatteryValues.Length - 1)]; break;
            }
        });
    }

    // ---- lighting --------------------------------------------------------

    private static readonly IReadOnlyList<LightEffect> EffectList =
        Effects.Select(e => new LightEffect(e.Name, e.Colors)).ToList();

    public IReadOnlyList<LightZone> LightZones => !IsReady ? Array.Empty<LightZone>() :
        LedZones.Select(z => new LightZone
        {
            Name = z.Name,
            Effects = EffectList,
            MaxBrightness = 4,
            HasWirelessBrightness = true,
            State = ReadZone(z.Offset),
        }).ToList();

    private LightState ReadZone(int off)
    {
        var colors = new Color[12];
        for (int i = 0; i < 12; i++)
        {
            int c = off + 2 + 3 * i;
            colors[i] = Color.FromArgb(_block[c], _block[c + 1], _block[c + 2]);
        }
        byte lvl = _block[off + 1];
        return new LightState
        {
            Effect = Math.Max(0, EffectIndex(_block[off])),
            Colors = colors,
            Brightness = LevelIndex(lvl & 0x0F),
            WirelessBrightness = LevelIndex(lvl >> 4),
        };
    }

    public void SetLighting(int zone, LightState s)
    {
        int off = LedZones[zone].Offset;
        var effect = Effects[Math.Clamp(s.Effect, 0, Effects.Length - 1)];
        WriteProfile(b =>
        {
            b[off] = effect.Raw;
            b[off + 1] = (byte)((LevelSteps[Math.Clamp(s.WirelessBrightness, 0, 4)] << 4)
                                | LevelSteps[Math.Clamp(s.Brightness, 0, 4)]);
            for (int i = 0; i < Math.Min(effect.Colors, s.Colors.Length); i++)
            {
                int c = off + 2 + 3 * i;
                b[c] = s.Colors[i].R; b[c + 1] = s.Colors[i].G; b[c + 2] = s.Colors[i].B;
            }
        });
    }

    // ---- export / import -----------------------------------------------

    private const int ButtonBytes = 5 * L701Codec.ButtonSlots;

    /// <summary>The DPI and button bytes of any profile, read straight from memory
    /// (no profile switch needed). Other offsets are left zero.</summary>
    private byte[] ReadPortableBytes(int profile)
    {
        if (profile == _profile && IsReady) return (byte[])_block.Clone();
        var b = new byte[BlockReadLength];
        int baseAddr = ProfileBase(profile);
        _p.Read(baseAddr + OffDpi1X, 4).CopyTo(b, OffDpi1X);
        _p.Read(baseAddr + OffButtons, ButtonBytes).CopyTo(b, OffButtons);
        return b;
    }

    public PortableProfile ExportProfile(int profile)
    {
        var b = ReadPortableBytes(profile);
        var p = new PortableProfile
        {
            Name = ProfileName(profile),
            Index = profile,
            Dpi = { DpiFromRaw(b[OffDpi1X]), DpiFromRaw(b[OffDpi2X]) },
        };
        for (int i = 0; i < L701Codec.ButtonSlots; i++)
            p.Buttons[Shared.Buttons.SpathaSlots[i]] = ToPortable(b.AsSpan(OffButtons + 5 * i, 5).ToArray());
        return p;
    }

    public void ImportProfile(int profile, PortableProfile data, ImportReport report)
    {
        string where = $"Profile {profile + 1}";
        if (profile < 0 || profile >= _profileCount) { report.Note($"{where}: this mouse has no such profile, skipped."); return; }
        if (IsProfileReadOnly(profile)) { report.Note($"{where} (Default) is read-only, skipped."); return; }

        var current = ReadPortableBytes(profile);
        var wanted = (byte[])current.Clone();

        for (int i = 0; i < Math.Min(2, data.Dpi.Count); i++)
        {
            byte raw = RawFromDpi(data.Dpi[i]);
            if (data.Dpi[i] > DpiMax || data.Dpi[i] < DpiMin)
                report.Note($"{where}: DPI stage {i + 1} {data.Dpi[i]} is outside {DpiMin}-{DpiMax}, set to {DpiFromRaw(raw)}.");
            int x = i == 0 ? OffDpi1X : OffDpi2X;
            wanted[x] = raw;
            wanted[x + 1] = raw;
        }
        if (data.Dpi.Count > 2)
            report.Note($"{where}: the Spatha has 2 DPI stages, stages 3-{data.Dpi.Count} were left out.");

        // Key combinations become combos (F13-F24) as on the Spatha X. Carriers bound to
        // buttons this import leaves alone stay as they are.
        var busy = new HashSet<ushort>(report.ReservedCarriers);
        for (int i = 0; i < L701Codec.ButtonSlots; i++)
            if (!data.Buttons.ContainsKey(Shared.Buttons.SpathaSlots[i]))
                if (CarrierOf(current.AsSpan(OffButtons + 5 * i, 5).ToArray()) is byte c && c != 0) busy.Add(c);

        for (int i = 0; i < L701Codec.ButtonSlots; i++)
        {
            string id = Shared.Buttons.SpathaSlots[i];
            if (!data.Buttons.TryGetValue(id, out var action)) continue;
            // Already does that (the mouse keeps stray bytes after some codes): leave it.
            if (ToPortable(current.AsSpan(OffButtons + 5 * i, 5).ToArray()).SameAs(action)) continue;
            if (action.Type == "key" && action.KeyCount > 1 && AllocateCombo is not null)
            {
                if (AllocateCombo(action, busy, report) is ushort carrier)
                    Key(0, (byte)carrier).CopyTo(wanted, OffButtons + 5 * i);
                else
                    report.Note($"{where}: {Shared.Buttons.Name(id)}: no free combo slot (F13-F24) left, left unchanged.");
                continue;
            }
            var bytes = FromPortable(action, out string? why);
            if (bytes is null)
            {
                report.Note($"{where}: {Shared.Buttons.Name(id)}: {why}, left unchanged.");
                continue;
            }
            bytes.CopyTo(wanted, OffButtons + 5 * i);
        }

        int baseAddr = ProfileBase(profile);
        int changed = 0;
        foreach (var (off, len) in new[] { (OffDpi1X, 4), (OffButtons, ButtonBytes) })
        {
            changed += _p.WriteDiff(baseAddr + off, current.AsSpan(off, len), wanted.AsSpan(off, len));
            var check = _p.Read(baseAddr + off, len);
            if (!check.AsSpan().SequenceEqual(wanted.AsSpan(off, len)))
                throw new IOException($"Verify failed in profile {profile + 1}");
        }
        if (changed > 0) _p.Commit();
        if (profile == _profile && IsReady)
        {
            Array.Copy(wanted, OffDpi1X, _block, OffDpi1X, 4);
            Array.Copy(wanted, OffButtons, _block, OffButtons, ButtonBytes);
        }
        report.Written++;
        Logger.WriteLine($"Spatha: imported into profile {profile + 1}: {changed} byte(s) written");
    }

    private static readonly (byte Mask, string Name)[] PortableMouse =
        { (1, "left"), (2, "right"), (4, "middle"), (8, "back"), (16, "forward") };

    private static readonly (byte Usage, string Name)[] PortableMedia =
    {
        (0xCD, "play_pause"), (0xB7, "stop"), (0xB5, "next"), (0xB6, "previous"),
        (0xE2, "mute"), (0xE9, "volume_up"), (0xEA, "volume_down"),
    };

    public static PortableAction ToPortable(byte[] b)
    {
        PortableAction a;
        byte carrier = CarrierOf(b);
        if (carrier != 0 && ComboKeys?.Invoke(carrier) is PortableAction keys)
        {
            // Export the keys the combo types, so the file means the same on any machine.
            keys.Label = Label(b);
            return keys;
        }
        switch (b[0])
        {
            case 0xFF: a = PortableAction.Simple("disabled"); break;
            case 0x0B: a = PortableAction.Simple("dpi"); break;
            case 0x00 when b[1] <= 1: a = PortableAction.Wheel(b[1] == 0 ? "up" : "down"); break;
            case 0x01 when PortableMouse.Any(m => (b[1] & m.Mask) != 0):
                a = PortableAction.Mouse(PortableMouse.First(m => (b[1] & m.Mask) != 0).Name);
                break;
            case 0x02: a = PortableAction.Key(b[1], b.Skip(2)); break;
            case 0x03 when PortableMedia.Any(m => m.Usage == b[1]):
                a = PortableAction.OfMedia(PortableMedia.First(m => m.Usage == b[1]).Name);
                break;
            default: a = new PortableAction { Type = "raw", Model = "Spatha", Raw = Convert.ToHexString(b) }; break;
        }
        a.Label = Label(b);
        return a;
    }

    /// <summary>Null when the Spatha can't do it; <paramref name="why"/> says why.</summary>
    public static byte[]? FromPortable(PortableAction a, out string? why)
    {
        why = null;
        switch (a.Type)
        {
            case "disabled": return (byte[])Disabled.Clone();
            case "dpi": return (byte[])DpiButton.Clone();
            case "wheel": return (byte[])(a.Button == "down" ? WheelDown : WheelUp).Clone();
            case "mouse":
                foreach (var (mask, name) in PortableMouse)
                    if (name == a.Button) return L701Codec.Mouse(mask);
                break;
            case "media":
                foreach (var (usage, name) in PortableMedia)
                    if (name == a.Media) return L701Codec.Media(usage);
                break;
            case "key":
            {
                var keys = a.Keys ?? new List<byte>();
                if (keys.Count == 0 && a.Modifiers == 0) { why = "empty key combination"; return null; }
                if (keys.Count > 3) { why = $"{a.Label ?? "the combination"} has more than 3 keys"; return null; }
                return Key(a.Modifiers, keys.ToArray());
            }
            case "raw" when a.Model == "Spatha" && a.Raw?.Length == 10:
                return Convert.FromHexString(a.Raw);
            case "side":
                why = $"\"{Shared.Buttons.Name(a.Button ?? "")}\" as a function only exists on the Spatha X";
                return null;
            case "double_click": why = "double click only exists on the Spatha X"; return null;
            case "target_focus": why = "target focus only exists on the Spatha X"; return null;
        }
        why ??= $"\"{a.Label ?? a.Type}\" isn't supported on the Spatha";
        return null;
    }

    // ---- events pushed by the mouse/dock (03 A2 profile, A3 DPI, A8 battery, AA dock) --

    /// <summary>
    /// Listens on every readable collection of the mouse / dock (the events came on the
    /// dock's vendor collection in testing, but listening on all of them costs nothing).
    /// Each listener survives read errors: it logs, reopens and carries on, so one radio
    /// hiccup can't silently end event delivery for the rest of the session.
    /// </summary>
    private void StartEvents()
    {
        if (_running || _eventPath is null || !OperatingSystem.IsWindows()) return;
        _running = true;
        List<string> paths;
        try
        {
            var me = DeviceList.Local.GetHidDevices(L701Discovery.VendorId)
                .FirstOrDefault(d => d.DevicePath.Equals(_eventPath, StringComparison.OrdinalIgnoreCase));
            int pid = me?.ProductID ?? (_wired ? L701Discovery.PidWired : L701Discovery.PidDock);
            paths = DeviceList.Local.GetHidDevices(L701Discovery.VendorId, pid)
                .Where(d => !d.DevicePath.EndsWith("\\kbd", StringComparison.OrdinalIgnoreCase))
                .Where(d => { try { return d.GetMaxInputReportLength() > 0; } catch { return false; } })
                .Select(d => d.DevicePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e)
        {
            Logger.WriteLine("Spatha: event listener: enumeration failed: " + e.Message);
            paths = new() { _eventPath };
        }
        foreach (var path in paths)
        {
            var t = new Thread(() => EventLoop(path)) { IsBackground = true, Name = "Spatha events" };
            lock (_eventThreads) _eventThreads.Add(t);
            t.Start();
        }
    }

    private HidStream? OpenEvents(string path)
    {
        try
        {
            var dev = DeviceList.Local.GetHidDevices(L701Discovery.VendorId)
                .FirstOrDefault(d => d.DevicePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (dev is null) return null;
            var cfg = new OpenConfiguration();
            cfg.SetOption(OpenOption.Exclusive, false);
            cfg.SetOption(OpenOption.Interruptible, true);
            var stream = dev.Open(cfg);
            stream.ReadTimeout = 1000;
            lock (_eventStreams) _eventStreams.Add(stream);
            return stream;
        }
        catch { return null; }
    }

    private void EventLoop(string path)
    {
        string name = path.Contains("col", StringComparison.OrdinalIgnoreCase)
            ? path[(path.IndexOf("col", StringComparison.OrdinalIgnoreCase))..].Split('#')[0] : "device";
        int failures = 0;
        while (_running)
        {
            var stream = OpenEvents(path);
            if (stream is null)
            {
                // Mouse / mouse-mode collections Windows keeps for itself land here; give up quietly.
                if (++failures == 1) Logger.WriteLine($"Spatha: event listener: can't open {name}");
                if (failures >= 3) return;
                Thread.Sleep(2000);
                continue;
            }
            Logger.WriteLine($"Spatha: event listener on {name}");
            failures = 0;
            var buf = new byte[Math.Max(8, stream.Device.GetMaxInputReportLength())];
            try
            {
                while (_running)
                {
                    int n;
                    try { n = stream.Read(buf); }
                    catch (TimeoutException) { continue; }
                    if (n < 3 || buf[0] != 0x03) continue;
                    HandleEvent(buf);
                }
            }
            catch (Exception e)
            {
                if (_running) Logger.WriteLine($"Spatha: event listener on {name} stopped ({e.Message}), reopening");
            }
            finally
            {
                lock (_eventStreams) _eventStreams.Remove(stream);
                try { stream.Dispose(); } catch { }
            }
            if (_running) Thread.Sleep(500);
        }
    }

    private void HandleEvent(byte[] buf)
    {
        if (buf[1] != 0xA2)              // A2 comes in bursts while the radio reconnects
            Logger.WriteLine($"Spatha: event {buf[0]:X2} {buf[1]:X2} {buf[2]:X2}");
        Contact();
        switch (buf[1])
        {
            case 0xA3:                       // DPI stage
                _activeDpiStage = Math.Clamp(buf[2] & 0x0F, 0, 1);
                break;
            case 0xA8:                       // battery level, sent after it lands on the dock
                if (buf[2] <= 100) Battery = BatteryPercent(buf[2], Charging);
                break;
            case 0xAA:                       // 01 = put on the dock, 00 = taken off
                Charging = buf[2] == 1;
                Logger.WriteLine($"Spatha: {(Charging ? "put on the dock, charging" : "taken off the dock")}");
                break;
        }
        ExternalChange?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _running = false;
        lock (_eventStreams)
            foreach (var st in _eventStreams) { try { st.Dispose(); } catch { } }
        _p.Dispose();
    }
}
