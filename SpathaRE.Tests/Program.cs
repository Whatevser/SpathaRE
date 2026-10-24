using System.Drawing;
using SpathaRE.Core.Devices;
using SpathaRE.Core.Spatha;

// Simulated original Spatha, behaving like the firmware decoded from Armoury's
// DLLs: report 0x0C, replies start with B0 (no report-id echo), B1 = busy.
// Memory starts from a real dump layout (factory profile in every slot).

var failures = new List<string>();
void Check(bool ok, string what) { if (!ok) failures.Add(what); Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }

Logger.FileLogging = false;

SpathaProtocol NewMouse(out FakeSpatha fake, bool echoReportId = false)
{
    fake = new FakeSpatha { EchoReportId = echoReportId };
    return new SpathaProtocol(fake) { SettleMs = 0, PollMs = 0 };
}

// ---- protocol ----
{
    var p = NewMouse(out var fake);
    Check(p.ReadByte(0x19) == 0x60, "read profile register");
    Check(p.Read(0x100, 6).SequenceEqual(SpathaCodec.FactoryProfile.Take(6)), "read4 + read1 mix");
    p.WriteByte(0x104, 0x42);
    Check(fake.Mem[0x104] == 0x42, "write byte");
    Check(fake.BusyServed > 0, "busy replies were retried");
    var bat = p.ReadBattery();
    Check(bat is { Percent: 85, Charging: false }, "battery 85% not charging");
    fake.Charging = true; fake.BatteryRaw = 75;
    Check(p.ReadBattery() is { Percent: 65, Charging: true }, "battery charging correction 75 -> 65");
    Check(p.ReadVersion(0) == 40, "firmware version query");
    p.SetActiveProfile(2);
    Check(fake.Mem[0x19] == 0x62 && fake.Sent.Any(s => s[1] == 0xC4 && s[2] == 0x07 && s[3] == 0x62), "switch profile writes reg 0x19 and sends C4 07");
}
{
    var p = NewMouse(out var fake, echoReportId: true);
    Check(p.ReadByte(0x19) == 0x60, "replies with an echoed report id are accepted");
}
{
    var p = NewMouse(out var fake);
    fake.StaleReplies = 3;
    Check(p.ReadByte(0x1C9) == SpathaCodec.FactoryProfile[0xC9], "stale replies for other addresses are skipped");
}

// ---- codec ----
Check(SpathaCodec.DpiFromRaw(31) == 1600 && SpathaCodec.DpiFromRaw(103) == 5200, "DPI scale (confirmed on hardware)");
Check(SpathaCodec.RawFromDpi(8200) == 163 && SpathaCodec.RawFromDpi(50) == 0, "DPI range");
Check(SpathaCodec.Describe(SpathaCodec.Key(0x03, 0x29)) == "Ctrl+Shift+Esc", "describe shortcut");
Check(SpathaCodec.Describe(SpathaCodec.Key(0, 0x2A)) == "Backspace", "describe Backspace");
Check(SpathaCodec.Describe(SpathaCodec.Media(0xE9)) == "Volume up", "describe media");
Check(SpathaCodec.Describe(SpathaCodec.DpiButton) == "DPI switch", "describe DPI");
Check(SpathaCodec.Describe(SpathaCodec.WheelDown) == "Wheel down", "describe wheel down");
Check(SpathaCodec.Describe(SpathaCodec.Mouse(4)) == "Wheel click", "describe wheel click");
Check(SpathaCodec.FactoryProfile.Length == 0xCD, "factory profile length");

// ---- device ----
{
    var p = NewMouse(out var fake);
    var d = new SpathaDevice(p, wired: false);
    d.Synchronize();
    Check(d.IsReady && d.Profile == 0 && d.ProfileCount == 6, "sync reads profile register");
    Check(d.ProfileName(0) == "Profile 1", "Armoury's stored \"Profile1\" shows as \"Profile 1\", like the Spatha X");
    Check(d.Dpi.Stages.SequenceEqual(new[] { 1650, 450 }), "DPI stages from factory profile");
    Check(d.Battery == 85, "battery read on wireless sync");

    d.SetDpi(1, 3200, null);
    Check(fake.Mem[0x106] == 63 && fake.Mem[0x107] == 63, "set DPI writes X and Y");
    Check(fake.Sent.Last()[1] == 0xC4 && fake.Sent.Last()[2] == 0x00, "write ends with EnableSetting commit");
    Check(d.Dpi.Stages[1] == 3200, "model updated after write");

    var backspace = d.ActionCatalog.SelectMany(g => g.Actions).First(a => a.Label == "Backspace");
    d.SetBinding(7, backspace);
    Check(fake.Mem.Skip(0x100 + 0x83 + 35).Take(5).SequenceEqual(new byte[] { 2, 0, 0x2A, 0, 0 }), "bind Backspace to a side button");
    Check(d.GetBinding(7)?.Label == "Backspace", "binding read back");

    var sc = d.MakeShortcut(new Shortcut(0x01, new byte[] { 0x06 }))!;
    d.SetBinding(8, sc);
    Check(d.GetBinding(8)?.Label == "Ctrl+C", "native shortcut binding");

    d.ResetBindings();
    Check(fake.Mem.Skip(0x183).Take(70).SequenceEqual(SpathaCodec.FactoryProfile.Skip(0x83).Take(70)), "reset restores factory buttons");

    int sentBefore = fake.Sent.Count(s => s[1] == 0xDE);
    d.ApplySetting("poll_wired", 2);
    d.ApplySetting("sleep", 5);
    d.ApplySetting("lowbatt", 4);
    d.ApplySetting("angle", 1);
    d.ApplySetting("accel", 7);
    d.ApplySetting("decel", 3);
    Check(fake.Mem[0x100] == 2 && fake.Mem[0x1CA] == 0xFF && fake.Mem[0x1CB] == 30, "polling / sleep / battery alert");
    Check(fake.Mem[0x101] == 0x81 && fake.Mem[0x10C] == 0x37, "angle snapping + accel/decel nibbles");
    d.ApplySetting("sleep", 5);
    // 500 Hz is already the factory value, so 5 of the 6 settings touch the mouse;
    // repeating "sleep" must not write again.
    Check(fake.Sent.Count(s => s[1] == 0xDE) == sentBefore + 5, "unchanged values are not rewritten");

    var zone = d.LightZones[1];
    var st = zone.State.Clone();
    st.Effect = 1; st.Brightness = 4; st.WirelessBrightness = 2;
    st.Colors = new[] { Color.FromArgb(0, 255, 128), Color.FromArgb(10, 20, 30) };
    d.SetLighting(1, st);
    Check(fake.Mem[0x137] == 10 && fake.Mem[0x138] == 0x7E, "wheel lighting effect + brightness byte");
    Check(fake.Mem.Skip(0x139).Take(6).SequenceEqual(new byte[] { 0, 255, 128, 10, 20, 30 }), "wheel lighting colours");
    Check(d.LightZones[1].State.Effect == 1, "lighting read back");

    d.SetProfile(5);
    Check(d.Profile == 5 && d.IsProfileReadOnly(5), "Default profile is read-only");
    bool threw = false;
    try { d.SetDpi(0, 800, null); } catch (InvalidOperationException) { threw = true; }
    Check(threw && fake.Mem[0x604] == SpathaCodec.FactoryProfile[4], "writes to Default are refused");

    fake.Mem[0x19] = 0x62;
    Check(d.PollProfileChanged() && d.Profile == 2, "detects a profile switch made on the mouse");
}
{
    var p = NewMouse(out var fake);
    fake.DropWritesTo = 0x104;
    var d = new SpathaDevice(p, wired: true);
    d.Synchronize();
    bool threw = false;
    try { d.SetDpi(0, 3200, null); } catch (IOException) { threw = true; }
    Check(threw && d.Dpi.Stages[0] == 1650, "a write the mouse drops is detected and the model re-read");
    Check(!d.HasBattery, "wired: no battery");
}

{
    var p = NewMouse(out var fake);
    var d = new SpathaDevice(p, wired: false);
    d.Synchronize();
    fake.Asleep = true;
    Check(!d.PollProfileChanged() && d.IsReady, "one missed reply (patchy wireless) doesn't mark the mouse asleep");
    SpathaDevice.AsleepAfter = TimeSpan.Zero;
    Check(!d.PollProfileChanged() && !d.IsReady, "silent for longer than AsleepAfter: shown as asleep");
    Check(!d.PollWake() && !d.IsReady, "wake check while still asleep");
    fake.Asleep = false;
    int reads = fake.Sent.Count;
    Check(d.PollWake() && d.IsReady, "wake check brings it back");
    Check(fake.Sent.Count - reads <= 3, "waking with the same profile costs a probe + battery read, not a full re-read");
    SpathaDevice.AsleepAfter = TimeSpan.FromSeconds(60);
}

// ---- combos: same F13-F24 model as the Spatha X ----
{
    var p = NewMouse(out var fake);
    var d = new SpathaDevice(p, wired: true);
    d.Synchronize();
    Check(d.ActionCatalog.Select(g => g.Name).SequenceEqual(new[] { "Mouse", "Combos", "Multimedia", "Keyboard" }),
          "categories and order match the Spatha X");
    Check(d.ActionCatalog[2].Actions.First().Label == "Volume up", "multimedia in the Spatha X order");
    var combos = d.ActionCatalog[1].Actions;
    Check(combos.Count == 12 && combos[0].Label == "F13", "12 combos (labels come from the app)");
    Check(!d.ActionCatalog[3].Actions.Any(a => a.Label == "F13"), "F13-F24 are only listed as combos");
    SpathaDevice.ComboLabel = u => $"Alt+Tab (F{13 + u - 0x68})";
    d.SetBinding(10, d.ActionCatalog[1].Actions[0]);
    Check(fake.Mem.Skip(0x183 + 50).Take(5).SequenceEqual(new byte[] { 2, 0, 0x68, 0, 0 }), "a combo binds the F13 carrier");
    Check(d.GetBinding(10)?.Label == "Alt+Tab (F13)" && d.BoundCombos.SequenceEqual(new ushort[] { 0x68 }), "combo label and bound carriers");

    SpathaDevice.AllocateCombo = (a, busy, r) => { ushort c = 0x69; while (busy.Contains(c)) c++; busy.Add(c); return c; };
    var data = new PortableProfile { Buttons = { ["side3"] = PortableAction.Key(0x03, new byte[] { 0x17 }) } };   // Ctrl+Shift+T
    d.ImportProfile(0, data, new ImportReport());
    Check(fake.Mem.Skip(0x183 + 30).Take(5).SequenceEqual(new byte[] { 2, 0, 0x69, 0, 0 }), "imported key combination becomes a combo, skipping the one in use");
    SpathaDevice.ComboLabel = null;
    SpathaDevice.AllocateCombo = null;
}

// ---- names shared by both models ----
Check(SpathaCodec.Describe(SpathaCodec.Key(0x02)) == "Left Shift", "lone modifier named like the key list (Left Shift, not Shift)");
Check(SpathaCodec.Describe(SpathaCodec.Key(0x08, 0x2B)) == "Win+Tab", "combination uses short modifier names");
Check(Buttons.WithoutCarrier("Win+Tab (F18)") == "Win+Tab" && Buttons.WithoutCarrier("F5") == "F5", "button map hides the combo's F-key");
Check(Buttons.ProfileName(2, "Sniper") == "Sniper" && Buttons.ProfileName(2, "") == "Profile 3", "real profile names are kept");
Check(Buttons.SlotName(DeviceModel.Spatha, 10) == "Side button 1" && Buttons.SlotName(DeviceModel.SpathaX, 8) == "Side button 1",
      "side button 1 has the same name on both models");
Check(Enumerable.Range(0, 14).All(i => SpathaCodec.SlotNames[i] == Buttons.SlotName(DeviceModel.Spatha, i)),
      "codec slot names match the shared table");
Check(Buttons.ProfileSwitchSlots(DeviceModel.Spatha, 1).SequenceEqual(new[] { 5, 6 }), "profile 2 = DPI + side 3 (Spatha slots 5, 6)");
Check(Buttons.ProfileSwitchSlots(DeviceModel.SpathaX, 2).SequenceEqual(new[] { 5, 13 }), "profile 3 = DPI + side 6 (X slots 5, 13)");
Check(Buttons.ProfileSwitchSlots(DeviceModel.Spatha, 5).SequenceEqual(new[] { 5, 8 }), "profile 6 = DPI + side 2");

// ---- export / import ----
{
    var p = NewMouse(out var fake);
    var d = new SpathaDevice(p, wired: true);
    d.Synchronize();
    var exported = d.ExportProfile(2);
    Check(exported.Dpi.SequenceEqual(new[] { 1650, 450 }) && exported.Buttons.Count == 14, "export another profile without switching");
    Check(exported.Buttons["side1"].Type == "key" && exported.Buttons["side1"].Keys![0] == 0x24, "side 1 exports as key 7");
    Check(exported.Buttons["wheel_click"].Type == "mouse" && exported.Buttons["wheel_click"].Button == "middle", "wheel click exports as middle");

    var file = new ProfileFile { Model = "SpathaX", Profiles = { new PortableProfile
    {
        Name = "From X", Index = 0, Dpi = { 800, 1600, 3200, 6400 },
        Buttons =
        {
            ["side1"] = PortableAction.Key(0x01, new byte[] { 0x06 }),            // Ctrl+C
            ["side2"] = new PortableAction { Type = "side", Button = "side4" },   // X-only
            ["back"] = PortableAction.OfMedia("volume_up"),
            ["dpi"] = PortableAction.Simple("dpi"),
        },
    } } };
    var parsed = ProfileFile.Parse(file.ToJson());
    var report = new ImportReport();
    d.ImportProfile(3, parsed.Profiles[0], report);
    int b = 0x100 * 4;
    Check(fake.Mem[b + 4] == 15 && fake.Mem[b + 6] == 31, "import sets the first two DPI stages");
    Check(fake.Mem.Skip(b + 0x83 + 5 * 10).Take(5).SequenceEqual(new byte[] { 2, 1, 6, 0, 0 }), "Ctrl+C from the X lands as a native shortcut on side 1");
    Check(fake.Mem.Skip(b + 0x83 + 5 * 3).Take(5).SequenceEqual(new byte[] { 3, 0xE9, 0, 0, 0 }), "media key mapped");
    Check(fake.Mem.Skip(b + 0x83 + 5 * 8).Take(5).SequenceEqual(SpathaCodec.FactoryProfile.Skip(0x83 + 5 * 8).Take(5)), "X-only function left unchanged");
    Check(report.Notes.Any(n => n.Contains("Side button 2")) && report.Notes.Any(n => n.Contains("2 DPI stages")), "report lists what was skipped");
    Check(fake.Sent.Last()[1] == 0xC4 && fake.Sent.Last()[2] == 0x00, "import commits");

    var ro = new ImportReport();
    d.ImportProfile(5, parsed.Profiles[0], ro);
    Check(ro.Written == 0 && fake.Mem[0x604] == SpathaCodec.FactoryProfile[4], "import never writes the Default profile");

    var round = new ImportReport();
    d.ImportProfile(0, d.ExportProfile(3), round);
    var a = d.ExportProfile(0); var c = d.ExportProfile(3);
    Check(round.Notes.Count == 0 && a.Buttons.All(kv => kv.Value.SameAs(c.Buttons[kv.Key])) && a.Dpi.SequenceEqual(c.Dpi),
          "export + import round trip copies a profile");
}

Console.WriteLine(failures.Count == 0 ? "\nALL PASSED" : $"\n{failures.Count} FAILED");
return failures.Count == 0 ? 0 : 1;

sealed class FakeSpatha : ISpathaTransport
{
    public byte[] Mem = new byte[0x2000];
    public byte ReportId => 0x0C;
    public bool EchoReportId;
    public bool Charging;
    public int BatteryRaw = 85;
    public int BusyServed;
    public int StaleReplies;
    public int DropWritesTo = -1;
    public bool Asleep;
    public List<byte[]> Sent = new();
    private byte[] _reply = Array.Empty<byte>();
    private int _busy = 1;

    public FakeSpatha()
    {
        Mem[0x19] = 0x60;
        var name = System.Text.Encoding.Unicode.GetBytes("Profile1");
        for (int p = 0; p < 6; p++)
        {
            int b = 0x100 * (p + 1);
            SpathaCodec.FactoryProfile.CopyTo(Mem, b);
            Mem[b + 0xD1] = (byte)name.Length;
            name.CopyTo(Mem, b + 0xD2);
            name[14] = (byte)('2' + p);
        }
    }

    public bool SetFeature(byte[] r)
    {
        if (r[0] != 0x0C || r.Length < 9) throw new InvalidOperationException("bad report " + Convert.ToHexString(r));
        Sent.Add(r);
        byte cmd = r[1], lo = r[2], hi = r[3];
        int addr = lo | hi << 8;
        _reply = cmd switch
        {
            0xDF => new byte[] { 0xB0, 0xC0, lo, hi, Mem[addr] },
            0xDD => new byte[] { 0xB0, 0xC0, lo, hi, Mem[addr], Mem[addr + 1], Mem[addr + 2], Mem[addr + 3] },
            0xDE => Write(addr, r[4], lo, hi),
            0xC4 when r[2] == 0x11 => new byte[] { 0xB0, 0xC4, 0, 0, (byte)BatteryRaw, 0, (byte)(Charging ? 1 : 0) },
            0xC4 when r[2] == 0x08 => new byte[] { 0xB0, 0x55, 0xAA, 40 },
            0xC4 when r[2] == 0x07 => Array.Empty<byte>(),
            _ => Array.Empty<byte>(),
        };
        return true;
    }

    private byte[] Write(int addr, byte v, byte lo, byte hi)
    {
        if (addr != DropWritesTo) Mem[addr] = v;
        return new byte[] { 0xB0, 0xC1, lo, hi };
    }

    public byte[]? GetFeature()
    {
        if (Asleep) return new byte[33];
        if (_busy-- > 0) { BusyServed++; return Pad(new byte[] { 0xB1 }); }
        if (StaleReplies-- > 0) return Pad(new byte[] { 0xB0, 0xC0, 0x99, 0x09, 0x00 });
        _busy = 1;
        return Pad(_reply);
    }

    private byte[] Pad(byte[] r)
    {
        var buf = new byte[33];
        if (EchoReportId) { buf[0] = 0x0C; r.CopyTo(buf, 1); }
        else r.CopyTo(buf, 0);
        return buf;
    }

    public void Dispose() { }
}
