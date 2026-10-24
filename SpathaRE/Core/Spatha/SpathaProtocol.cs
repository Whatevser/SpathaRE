namespace SpathaRE.Core.Spatha;

/// <summary>One HID collection that carries the Spatha feature reports.</summary>
public interface ISpathaTransport : IDisposable
{
    /// <summary>Report ID used by this collection (0x0C normally, 0x00 on the mouse collection).</summary>
    byte ReportId { get; }
    bool SetFeature(byte[] report);
    /// <summary>Returns the raw feature report, or null on failure.</summary>
    byte[]? GetFeature();
}

/// <summary>
/// Request/reply engine. A command is one SetFeature: [rid, cmd, args..]; the
/// reply is read back with GetFeature and starts with B0 (ok) or B1 (busy).
/// The firmware doesn't echo the report ID, but some stacks add it, so both
/// layouts are accepted.
/// </summary>
public sealed class SpathaProtocol : IDisposable
{
    private const byte Ack = 0xB0, Busy = 0xB1;
    private const byte CmdCtrl = 0xC4, CmdRead4 = 0xDD, CmdWrite = 0xDE, CmdRead1 = 0xDF;
    private const byte SubRead = 0xC0, SubWrite = 0xC1;

    private readonly ISpathaTransport _io;

    // Through the dock every request is relayed to the mouse over radio, so a reply
    // can take a few hundred ms (stale replies come back first). These match what
    // worked in the Python tool; a quick reply still returns immediately.
    /// <summary>Delay between sending and the first reply poll.</summary>
    public int SettleMs { get; set; } = 20;
    /// <summary>Delay between reply polls.</summary>
    public int PollMs { get; set; } = 15;
    /// <summary>Reply polls per attempt.</summary>
    public int Polls { get; set; } = 30;

    public SpathaProtocol(ISpathaTransport io) => _io = io;

    public void Dispose() => _io.Dispose();

    private void Send(params byte[] payload)
    {
        // Armoury always sends at least report id + 8 bytes; the transport pads further
        // to the collection's real feature length.
        var buf = new byte[Math.Max(9, payload.Length + 1)];
        buf[0] = _io.ReportId;
        payload.CopyTo(buf, 1);
        if (!_io.SetFeature(buf)) throw new IOException("SetFeature failed");
    }

    private byte[]? Receive()
    {
        var data = _io.GetFeature();
        if (data is null || data.Length < 2) return null;
        if (data[0] == Ack || data[0] == Busy) return data;
        if (data[0] == _io.ReportId && (data[1] == Ack || data[1] == Busy)) return data[1..];
        return data;
    }

    private static void Sleep(int ms) { if (ms > 0) Thread.Sleep(ms); }

    /// <summary>Send, then poll replies until one matches. Stale replies (for
    /// another request) are skipped, which keeps this robust if another program
    /// is talking to the mouse at the same time.</summary>
    private byte[] Transact(byte[] payload, Func<byte[], bool> match, int polls = 0, int rounds = 4)
    {
        if (polls <= 0) polls = Polls;
        for (int round = 0; round < rounds; round++)
        {
            Send(payload);
            Sleep(SettleMs);
            for (int i = 0; i < polls; i++)
            {
                var r = Receive();
                if (r is not null && r.Length >= 2 && r[0] == Ack && match(r)) return r;
                Sleep(PollMs);
            }
            Sleep(60);
        }
        throw new TimeoutException($"No reply to {Convert.ToHexString(payload)}");
    }

    // ---- memory ----

    /// <param name="rounds">Send attempts before giving up; lower it for cheap polls.</param>
    public byte ReadByte(int addr, int rounds = 4)
    {
        byte lo = (byte)addr, hi = (byte)(addr >> 8);
        var r = Transact(new byte[] { CmdRead1, lo, hi, 0, 0 },
            r => r.Length >= 5 && r[1] == SubRead && r[2] == lo && r[3] == hi, rounds: rounds);
        return r[4];
    }

    /// <summary>Quick check used during discovery: reads the profile register with a short timeout.</summary>
    public byte? Probe()
    {
        try
        {
            var r = Transact(new byte[] { CmdRead1, SpathaCodec.RegProfile, 0, 0, 0 },
                r => r.Length >= 5 && r[1] == SubRead && r[2] == SpathaCodec.RegProfile && r[3] == 0,
                rounds: 3);
            return r[4];
        }
        catch (Exception e)
        {
            Logger.WriteLine("Spatha probe: " + e.Message);
            return null;
        }
    }

    public byte[] Read4(int addr)
    {
        byte lo = (byte)addr, hi = (byte)(addr >> 8);
        var r = Transact(new byte[] { CmdRead4, lo, hi },
            r => r.Length >= 8 && r[1] == SubRead && r[2] == lo && r[3] == hi);
        return r[4..8];
    }

    public byte[] Read(int addr, int count)
    {
        var outp = new byte[count];
        int n = 0;
        while (n < count)
        {
            if (count - n >= 4)
            {
                Read4(addr + n).CopyTo(outp, n);
                n += 4;
            }
            else
            {
                outp[n] = ReadByte(addr + n);
                n++;
            }
        }
        return outp;
    }

    public void WriteByte(int addr, byte value)
    {
        if (addr < 0 || addr >= 0x2000) throw new ArgumentOutOfRangeException(nameof(addr));
        byte lo = (byte)addr, hi = (byte)(addr >> 8);
        Transact(new byte[] { CmdWrite, lo, hi, value },
            r => r.Length >= 4 && r[1] == SubWrite && r[2] == lo && r[3] == hi);
    }

    /// <summary>Writes only the bytes that differ from <paramref name="current"/>. Returns how many changed.</summary>
    public int WriteDiff(int addr, ReadOnlySpan<byte> current, ReadOnlySpan<byte> wanted)
    {
        int changed = 0;
        for (int i = 0; i < wanted.Length; i++)
        {
            if (current[i] == wanted[i]) continue;
            WriteByte(addr + i, wanted[i]);
            changed++;
        }
        return changed;
    }

    // ---- control commands (C4 ...) ----

    private void Ctrl(byte a, byte b = 0, byte c = 0, byte d = 0)
    {
        Send(CmdCtrl, a, b, c, d, 0, 0, 0);
        Sleep(20);
    }

    /// <summary>"EnableSetting": Armoury sends this after every save.</summary>
    public void Commit()
    {
        Ctrl(0x00);
        Sleep(250);
    }

    /// <summary>Switch the active onboard profile (0-based).</summary>
    public void SetActiveProfile(int profile)
    {
        byte reg = ReadByte(SpathaCodec.RegProfile);
        byte value = (byte)((reg & 0xF0) | (profile & 0x0F));
        WriteByte(SpathaCodec.RegProfile, value);
        Ctrl(0x07, value);
    }

    /// <summary>C4 11 00 -> B0 C4 .. .. level .. charging. Null when the mouse doesn't answer.</summary>
    public (int Percent, int Raw, bool Charging)? ReadBattery()
    {
        try
        {
            var r = Transact(new byte[] { CmdCtrl, 0x11, 0x00, 0, 0, 0, 0, 0 },
                r => r.Length >= 7 && r[1] == CmdCtrl, rounds: 2);
            bool charging = r[6] == 1;
            return (SpathaCodec.BatteryPercent(r[4], charging), r[4], charging);
        }
        catch (TimeoutException) { return null; }
    }

    /// <summary>C4 08 sel -> B0 55 AA n: firmware minor of the device (sel 0) or of the mouse via the dock (sel 2).</summary>
    public int? ReadVersion(byte selector)
    {
        try
        {
            var r = Transact(new byte[] { CmdCtrl, 0x08, selector, 0, 0, 0, 0, 0 },
                r => r.Length >= 4 && r[1] == 0x55 && r[2] == 0xAA, rounds: 2);
            return r[3];
        }
        catch (TimeoutException) { return null; }
    }
}
