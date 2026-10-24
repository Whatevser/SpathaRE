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
        IsReady = true;
        if (HasBattery) ReadBattery();
        StartEvents();
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
        if (b is null) { Battery = -1; return; }
        Battery = b.Value.Percent;
        Charging = b.Value.Charging;
    }

    public bool PollProfileChanged()
    {
        byte reg;
        try { reg = _p.ReadByte(RegProfile); }
        catch (TimeoutException)
        {
            // Mouse asleep or out of range; the battery poll re-syncs it when it answers again.
            IsReady = false;
            return false;
        }
        int now = reg & 0x0F;
        if (now == _profile) return false;
        Synchronize();
        return true;
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
        SlotNames.Select((n, i) => new ButtonSlot(i, n)).ToList();

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

    // ---- events pushed by the mouse (03 A2 profile, 03 A3 DPI) ----------

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
            if (buf[1] == 0xA3)
            {
                _activeDpiStage = Math.Clamp(buf[2] & 0x0F, 0, 1);
                ExternalChange?.Invoke(this, EventArgs.Empty);
            }
            else if (buf[1] == 0xA2)
            {
                ExternalChange?.Invoke(this, EventArgs.Empty);
            }
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
