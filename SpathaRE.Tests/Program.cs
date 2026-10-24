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
Check(SpathaCodec.Describe(SpathaCodec.WheelDown) == "Scroll down", "describe wheel down");
Check(SpathaCodec.FactoryProfile.Length == 0xCD, "factory profile length");

// ---- device ----
{
    var p = NewMouse(out var fake);
    var d = new SpathaDevice(p, wired: false);
    d.Synchronize();
    Check(d.IsReady && d.Profile == 0 && d.ProfileCount == 6, "sync reads profile register");
    Check(d.ProfileName(0) == "Profile1", "profile name decoded from 0xD1");
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
    Check(!d.PollProfileChanged() && !d.IsReady, "mouse falling asleep marks the device not ready");
    fake.Asleep = false;
    d.Synchronize();
    Check(d.IsReady, "device comes back after waking");
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
