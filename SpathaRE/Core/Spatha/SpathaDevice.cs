using System.Drawing;
using System.Text;
using HidSharp;
using SpathaRE.Core.Devices;
using static SpathaRE.Core.Spatha.SpathaCodec;

namespace SpathaRE.Core.Spatha;

/// <summary>
/// The original ROG Spatha (2016), wired or through its dock. Everything is
/// read from and written to the mouse's own memory, the same way Armoury Crate
/// does it: settings live in 6 onboard profiles of 256 bytes, the UI edits the
/// active one, and every save ends with the EnableSetting commit.
/// </summary>
public sealed class SpathaDevice : IMouseDevice
{
    private readonly SpathaProtocol _p;
    private readonly bool _wired;
    private readonly string? _eventPath;

    private byte[] _block = new byte[BlockReadLength];
    private int _profile;
    private int _profileCount = SpathaCodec.ProfileCount;
    private string _profileName = "";
    private int _activeDpiStage = -1;

    private HidStream? _events;
    private Thread? _eventThread;
    private volatile bool _running;

    // Wireless through the dock is patchy: single requests time out now and then
    // while the mouse itself works fine. Only call it asleep after it has been
    // silent for a while, and count anything it sends as a sign of life.
    private bool _synced;
    private DateTime _lastContact = DateTime.UtcNow;
    /// <summary>How long the mouse may stay silent before it is shown as asleep.</summary>
    public static TimeSpan AsleepAfter { get; set; } = TimeSpan.FromSeconds(60);

    public event EventHandler? ExternalChange;

    public SpathaDevice(SpathaProtocol protocol, bool wired, string? eventDevicePath = null)
    {
        _p = protocol;
        _wired = wired;
        _eventPath = eventDevicePath;
    }

    public string Name => "ROG Spatha";
    public DeviceModel Model => DeviceModel.Spatha;
    public string ConnectionLabel => _wired ? "Wired" : "Wireless";
    public bool IsReady { get; private set; }

    public bool HasBattery => !_wired;
    public int Battery { get; private set; } = -1;
    public bool Charging { get; private set; }

    public int ProfileCount => _profileCount;
    public int Profile => _profile;

    /// <summary>Profile 6 is "Default", which Armoury never writes. Same rule here,
    /// unless "spatha_allow_default_profile" is set in config.</summary>
    public bool IsProfileReadOnly(int profile) =>
        profile == DefaultProfile && !AppConfig.GetBool("spatha_allow_default_profile");

    public string ProfileName(int profile) =>
        profile == _profile && _profileName.Length > 0 ? _profileName
        : profile == DefaultProfile ? "Default" : $"Profile {profile + 1}";

    private int Base => ProfileBase(_profile);

    // ---- sync ----------------------------------------------------------

    public void Synchronize()
    {
        byte reg = _p.ReadByte(RegProfile);
        _profileCount = Math.Clamp(reg >> 4, 1, SpathaCodec.ProfileCount);
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

    public void ReadBattery()
    {
        var b = _p.ReadBattery();
        if (b is null) { Missed(); return; }      // keep the last reading over a dropped reply
        Contact();
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
        Enumerable.Range(0, SpathaCodec.ButtonSlots).Select(i => new ButtonSlot(i, SlotNames[i])).ToList();

    private static ButtonAction Action(byte[] bytes, string? label = null) =>
        new(Convert.ToHexString(bytes), label ?? Describe(bytes), bytes);

    public IReadOnlyList<ActionGroup> ActionCatalog { get; } = BuildCatalog();

    private static IReadOnlyList<ActionGroup> BuildCatalog()
    {
        var mouse = new List<ButtonAction>
        {
            Action(SpathaCodec.Mouse(1)), Action(SpathaCodec.Mouse(2)), Action(SpathaCodec.Mouse(4)),
            Action(SpathaCodec.Mouse(8)), Action(SpathaCodec.Mouse(16)),
            Action(DpiButton), Action(WheelUp), Action(WheelDown), Action(Disabled),
        };
        var media = MediaKeys.Select(m => Action(SpathaCodec.Media(m.Usage), m.Name)).ToList();

        // Every HID key the firmware can send, in keyboard order. Lone modifiers go in
        // the modifier byte; everything else is a key usage.
        var keys = new List<ButtonAction>();
        var usages = Enumerable.Range(0x04, 0x38 - 0x04 + 1)       // A-Z, 1-0, Enter..'/'
            .Concat(Enumerable.Range(0x39, 0x53 - 0x39 + 1))        // Caps, F1-F12, nav block, NumLock
            .Concat(Enumerable.Range(0x54, 0x63 - 0x54 + 1))        // numpad
            .Concat(new[] { 0x65 })                                 // Menu
            .Concat(Enumerable.Range(0x68, 12))                     // F13-F24
            .Where(u => u != 0x32);                                 // non-US # (no key on most layouts)
        foreach (int u in usages) keys.Add(Action(Key(0, (byte)u)));
        string[] mods = { "Left Ctrl", "Left Shift", "Left Alt", "Left Win", "Right Ctrl", "Right Shift", "Right Alt", "Right Win" };
        for (int i = 0; i < 8; i++) keys.Add(Action(Key((byte)(1 << i)), mods[i]));

        return new List<ActionGroup>
        {
            new("Mouse", mouse),
            new("Media", media),
            new("Keyboard", keys),
        };
    }

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
        Array.Copy(FactoryProfile, OffButtons, b, OffButtons, 5 * SpathaCodec.ButtonSlots));

    public bool SupportsShortcuts => true;

    public ButtonAction? MakeShortcut(Shortcut s)
    {
        if (s.Keys.Count == 0 && s.Modifiers == 0) return null;
        return Action(Key(s.Modifiers, s.Keys.Take(3).ToArray()));
    }

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

    private const int ButtonBytes = 5 * SpathaCodec.ButtonSlots;

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
        for (int i = 0; i < SpathaCodec.ButtonSlots; i++)
            p.Buttons[Devices.Buttons.SpathaSlots[i]] = ToPortable(b.AsSpan(OffButtons + 5 * i, 5).ToArray());
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

        for (int i = 0; i < SpathaCodec.ButtonSlots; i++)
        {
            string id = Devices.Buttons.SpathaSlots[i];
            if (!data.Buttons.TryGetValue(id, out var action)) continue;
            // Already does that (the mouse keeps stray bytes after some codes): leave it.
            if (ToPortable(current.AsSpan(OffButtons + 5 * i, 5).ToArray()).SameAs(action)) continue;
            var bytes = FromPortable(action, out string? why);
            if (bytes is null)
            {
                report.Note($"{where}: {Devices.Buttons.Name(id)}: {why}, left unchanged.");
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
        a.Label = Describe(b);
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
                    if (name == a.Button) return SpathaCodec.Mouse(mask);
                break;
            case "media":
                foreach (var (usage, name) in PortableMedia)
                    if (name == a.Media) return SpathaCodec.Media(usage);
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
                why = $"\"{Devices.Buttons.Name(a.Button ?? "")}\" as a function only exists on the Spatha X";
                return null;
            case "double_click": why = "double click only exists on the Spatha X"; return null;
            case "target_focus": why = "target focus only exists on the Spatha X"; return null;
        }
        why ??= $"\"{a.Label ?? a.Type}\" isn't supported on the Spatha";
        return null;
    }

    // ---- events pushed by the mouse/dock (03 A2 profile, A3 DPI, A8 battery, AA dock) --

    private void StartEvents()
    {
        if (_running || _eventPath is null || !OperatingSystem.IsWindows()) return;
        try
        {
            var dev = DeviceList.Local.GetHidDevices(SpathaDiscovery.VendorId)
                .FirstOrDefault(d => d.DevicePath.Equals(_eventPath, StringComparison.OrdinalIgnoreCase));
            if (dev is null || dev.GetMaxInputReportLength() <= 0) return;
            var cfg = new OpenConfiguration();
            cfg.SetOption(OpenOption.Exclusive, false);
            cfg.SetOption(OpenOption.Interruptible, true);
            _events = dev.Open(cfg);
            _events.ReadTimeout = 500;
        }
        catch (Exception e)
        {
            Logger.WriteLine("Spatha: event listener unavailable: " + e.Message);
            return;
        }

        _running = true;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "Spatha events" };
        _eventThread.Start();
    }

    private void EventLoop()
    {
        var buf = new byte[Math.Max(8, _events!.Device.GetMaxInputReportLength())];
        while (_running)
        {
            int n;
            try { n = _events.Read(buf); }
            catch (TimeoutException) { continue; }
            catch { break; }
            if (n < 3 || buf[0] != 0x03) continue;
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
                    break;
            }
            ExternalChange?.Invoke(this, EventArgs.Empty);
        }
        _running = false;
    }

    public void Dispose()
    {
        _running = false;
        try { _events?.Dispose(); } catch { }
        _p.Dispose();
    }
}
