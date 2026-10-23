#!/usr/bin/env python3
"""
spatha.py - configure the original ASUS ROG Spatha (non-X, USB 0B05:181C)
without Armoury Crate.

The protocol was reverse engineered from Armoury Crate's Spatha plugins
(IfSpatha.dll, SPATHA_HID_Library_C++.dll, SPATHA_SYNC_Dll.dll). See
PROTOCOL.md for the full write-up and for which parts are confirmed vs.
inferred.

Requires:  pip install hidapi
"""

import argparse
import os
import sys
import time

ASUS_VID = 0x0B05
SPATHA_PIDS = {0x181C}                     # wired; the RF dongle PID is unknown
SPATHA_BCD = {0x4200, 0x4201, 0xD000, 0xD002}  # bcdDevice values IfSpatha.dll matches on

# --- protocol constants ------------------------------------------------------

CMD_CTRL = 0xC4        # control / mode commands
CMD_READ4 = 0xDD       # read 4 bytes at addr
CMD_WRITE = 0xDE       # write 1 byte at addr
CMD_READ1 = 0xDF       # read 1 byte at addr

ACK = 0xB0
BUSY = 0xB1
SUB_READ = 0xC0        # reply to DD / DF
SUB_WRITE = 0xC1       # reply to DE

REG_PROFILE = 0x19     # (profile_count << 4) | active_profile
PROFILE_COUNT = 6
PROFILE_SIZE = 0x100
MEM_END = 0x100 + PROFILE_COUNT * PROFILE_SIZE   # 0x700

# Offsets inside one profile block (block for profile p starts at 0x100*(p+1))
OFF_POLL_WIRED = 0x00
OFF_ANGLE_SNAP = 0x01
OFF_LIFT_OFF = 0x02
OFF_DPI = {1: (0x04, 0x05), 2: (0x06, 0x07)}
OFF_ACCEL = 0x0C        # low nibble accel, high nibble decel
LED_ZONES = {1: 0x5D, 2: 0x37, 3: 0x11}   # effect byte; +1 brightness; +2.. colors
LED_ZONE_SIZE = 38      # effect, brightness, 12 x RGB
OFF_BUTTONS = 0x83      # 14 slots x 5 bytes
BUTTON_SLOTS = 14
OFF_BTN_RESPONSE = 0xC9
OFF_SLEEP = 0xCA
OFF_LOW_BATT = 0xCB
OFF_POLL_WIRELESS = 0xCC
OFF_CALIBRATION = 0xD0
PROFILE_USED = 0xD1     # bytes 0x00..0xD0 are the known/used part of a profile

POLL_CODES = {125: 8, 250: 4, 500: 2, 1000: 1}
POLL_NAMES = {v: k for k, v in POLL_CODES.items()}

# Armoury effect index -> raw effect byte, plus how many colors it uses
LED_EFFECTS = {
    "static":      (1, 1),
    "breathing":   (10, 2),
    "colorcycle":  (5, 12),
    "effect3":     (6, 0),   # name unknown (Armoury effect #3, no colors)
    "reactive":    (12, 1),
    "effect5":     (11, 0),  # name unknown (Armoury effect #5, no colors)
    "effect6":     (14, 0),  # name unknown (Armoury effect #6, no colors)
}
LED_EFFECT_NAMES = {raw: name for name, (raw, _) in LED_EFFECTS.items()}
LEVEL_STEPS = [0, 4, 7, 11, 14]          # brightness index 0..4 -> nibble

SLEEP_MINUTES = [1, 2, 3, 5, 10]
LOW_BATT_PCT = [10, 15, 20, 25, 30]

MOUSE_BUTTONS = {"left": 1, "right": 2, "middle": 4, "back": 8, "forward": 16}
MEDIA_KEYS = {"next": 0xB5, "prev": 0xB6, "stop": 0xB7, "playpause": 0xCD,
              "mute": 0xE2, "volup": 0xE9, "voldown": 0xEA}
MODIFIERS = {"ctrl": 0x01, "lctrl": 0x01, "shift": 0x02, "lshift": 0x02,
             "alt": 0x04, "lalt": 0x04, "win": 0x08, "gui": 0x08, "lwin": 0x08,
             "rctrl": 0x10, "rshift": 0x20, "ralt": 0x40, "altgr": 0x40,
             "rwin": 0x80, "rgui": 0x80}

# HID keyboard usage IDs (USB HID Usage Tables, page 0x07)
HID_KEYS = {chr(ord("a") + i): 0x04 + i for i in range(26)}
HID_KEYS.update({str(i): 0x1E + i - 1 for i in range(1, 10)})
HID_KEYS.update({
    "0": 0x27, "enter": 0x28, "esc": 0x29, "escape": 0x29, "backspace": 0x2A,
    "tab": 0x2B, "space": 0x2C, "minus": 0x2D, "equal": 0x2E, "lbracket": 0x2F,
    "rbracket": 0x30, "backslash": 0x31, "semicolon": 0x33, "quote": 0x34,
    "grave": 0x35, "comma": 0x36, "period": 0x37, "slash": 0x38,
    "capslock": 0x39, "printscreen": 0x46, "scrolllock": 0x47, "pause": 0x48,
    "insert": 0x49, "home": 0x4A, "pageup": 0x4B, "delete": 0x4C, "end": 0x4D,
    "pagedown": 0x4E, "right": 0x4F, "left": 0x50, "down": 0x51, "up": 0x52,
    "numlock": 0x53, "menu": 0x65,
})
HID_KEYS.update({f"f{i}": 0x3A + i - 1 for i in range(1, 13)})
HID_KEYS.update({f"f{i}": 0x68 + i - 13 for i in range(13, 25)})
HID_KEY_NAMES = {}
for _name, _code in HID_KEYS.items():
    HID_KEY_NAMES.setdefault(_code, _name)


class SpathaError(Exception):
    pass


# --- transport ---------------------------------------------------------------

class Spatha:
    """One open HID collection that answers the Spatha vendor protocol."""

    def __init__(self, dev, report_id=0x0C, report_len=10, read_len=33,
                 verbose=False):
        self.dev = dev
        self.rid = report_id
        self.report_len = report_len
        self.read_len = read_len
        self.verbose = verbose

    def _send(self, payload):
        buf = bytes([self.rid]) + bytes(payload)
        buf = buf.ljust(self.report_len, b"\x00")
        if self.verbose:
            print("  >>", buf.hex(" "), file=sys.stderr)
        n = self.dev.send_feature_report(buf)
        if n is not None and n < 0:
            raise SpathaError("send_feature_report failed")

    def _recv(self):
        data = bytes(self.dev.get_feature_report(self.rid, self.read_len))
        if self.verbose:
            print("  <<", data.hex(" "), file=sys.stderr)
        # The firmware answers with B0/B1 in byte 0 (it does not echo the
        # report ID). Accept an echoed report ID too, in case a driver adds one.
        if data and data[0] in (ACK, BUSY):
            return data
        if len(data) > 1 and data[0] == self.rid and data[1] in (ACK, BUSY):
            return data[1:]
        return data

    def transact(self, payload, match, tries=15, settle=0.03, rounds=10):
        """Send payload, then poll replies until match(reply) is truthy."""
        for _ in range(rounds):
            self._send(payload)
            time.sleep(settle)
            for _ in range(tries):
                r = self._recv()
                if r and r[0] == ACK and match(r):
                    return r
                time.sleep(0.03)
            time.sleep(0.12)
        raise SpathaError(f"no valid reply to {bytes(payload).hex(' ')}")

    # memory access
    def read_byte(self, addr):
        lo, hi = addr & 0xFF, addr >> 8
        r = self.transact([CMD_READ1, lo, hi, 0, 0],
                          lambda r: r[1] == SUB_READ and r[2] == lo and r[3] == hi)
        return r[4]

    def read4(self, addr):
        lo, hi = addr & 0xFF, addr >> 8
        r = self.transact([CMD_READ4, lo, hi],
                          lambda r: r[1] == SUB_READ and r[2] == lo and r[3] == hi)
        return bytes(r[4:8])

    def read(self, addr, count, progress=False):
        out = bytearray()
        progress = progress and sys.stderr.isatty()
        use4 = True
        a = addr
        while len(out) < count:
            if progress:
                print(f"\r  reading 0x{a:03X} ({len(out)}/{count})", end="",
                      file=sys.stderr, flush=True)
            if use4 and count - len(out) >= 4:
                try:
                    out += self.read4(a)
                    a += 4
                    continue
                except SpathaError:
                    use4 = False      # firmware without DD: fall back to DF
            out.append(self.read_byte(a))
            a += 1
        if progress:
            print("\r" + " " * 40 + "\r", end="", file=sys.stderr)
        return bytes(out[:count])

    def write_byte(self, addr, value):
        if not 0 <= addr < 0x2000:
            raise SpathaError(f"address 0x{addr:X} out of range")
        lo, hi = addr & 0xFF, addr >> 8
        self.transact([CMD_WRITE, lo, hi, value & 0xFF],
                      lambda r: r[1] == SUB_WRITE and r[2] == lo and r[3] == hi)

    def write(self, addr, data, verify=True):
        for i, v in enumerate(data):
            self.write_byte(addr + i, v)
        if verify:
            back = self.read(addr, len(data))
            if back != bytes(data):
                raise SpathaError(
                    f"verify failed at 0x{addr:X}: wrote {bytes(data).hex(' ')}, "
                    f"read {back.hex(' ')}")

    def ctrl(self, a=0, b=0, c=0, d=0):
        self._send([CMD_CTRL, a, b, c, d, 0, 0, 0])
        time.sleep(0.02)

    # high level
    def commit(self):
        """EnableSetting: Armoury sends this after writing settings."""
        self.ctrl(0x00, 0x00, 0x00, 0x00)
        time.sleep(0.25)

    def profile_reg(self):
        return self.read_byte(REG_PROFILE)

    def active_profile(self):
        return self.profile_reg() & 0x0F

    def set_active_profile(self, p):
        reg = self.profile_reg()
        count = (reg >> 4) or PROFILE_COUNT
        if not 0 <= p < count:
            raise SpathaError(f"profile must be 1..{count}")
        new = (reg & 0xF0) | p
        self.write_byte(REG_PROFILE, new)
        self.ctrl(0x07, new, 0, 0)     # load profile

    def status(self):
        """C4 08 00 -> B0 55 AA <n>  or  B0 3C C3 <n> (charging?)."""
        self._send([CMD_CTRL, 0x08, 0x00, 0, 0, 0, 0, 0])
        time.sleep(0.03)
        for _ in range(15):
            r = self._recv()
            if len(r) >= 4 and r[0] == ACK:
                if r[1] == 0x55 and r[2] == 0xAA:
                    return ("ok", r[3])
                if r[1] == 0x3C and r[2] == 0xC3:
                    return ("charging", r[3])
            time.sleep(0.03)
        return ("unknown", None)


def profile_base(p):
    return 0x100 * (p + 1)


# --- discovery ---------------------------------------------------------------

def _hid():
    try:
        import hid
    except ImportError:
        sys.exit("The 'hidapi' Python package is missing: pip install hidapi")
    return hid


def candidates(show_all=False):
    hid = _hid()
    out = []
    for d in hid.enumerate(ASUS_VID, 0):
        likely = d["product_id"] in SPATHA_PIDS or d.get("release_number") in SPATHA_BCD
        if likely or show_all:
            out.append(d)
    # keyboard collections first: that's where Armoury talks to the mouse
    out.sort(key=lambda d: (not (d.get("usage_page") == 1 and d.get("usage") == 6),
                            d.get("interface_number", 0)))
    return out


def probe(info, verbose=False):
    hid = _hid()
    for rid, rlen in ((0x0C, 10), (0x00, 9)):
        dev = hid.device()
        try:
            dev.open_path(info["path"])
        except (OSError, IOError):
            return None
        sp = Spatha(dev, rid, rlen, verbose=verbose)
        try:
            sp.transact([CMD_READ1, REG_PROFILE, 0, 0, 0],
                        lambda r: r[1] == SUB_READ and r[2] == REG_PROFILE,
                        tries=3, rounds=2)
            return sp
        except Exception:
            dev.close()
    return None


def open_device(args):
    hid = _hid()
    if args.path:
        dev = hid.device()
        dev.open_path(args.path.encode() if isinstance(args.path, str) else args.path)
        return Spatha(dev, args.report_id, args.report_len, verbose=args.verbose)
    for info in candidates():
        sp = probe(info, args.verbose)
        if sp:
            if args.verbose:
                print(f"using {info['path']!r} rid=0x{sp.rid:02X}", file=sys.stderr)
            return sp
    sys.exit("No responding Spatha found. Run 'spatha.py list --all' and try --path.")


# --- decoding helpers ----------------------------------------------------------

def dpi_from_raw(raw):
    return (raw + 1) * 50


def raw_from_dpi(dpi):
    raw = round(dpi / 50) - 1
    if not 0 <= raw <= 163:
        raise SpathaError("DPI must be 50..8200")
    return raw


def decode_button(b):
    t = b[0]
    if t == 0xFF:
        return "disabled"
    if t == 1:
        names = [n for n, m in MOUSE_BUTTONS.items() if b[1] & m]
        return "mouse:" + "+".join(names) if names else f"mouse:0x{b[1]:02X}"
    if t == 2:
        mods = [n for n in ("ctrl", "shift", "alt", "win", "rctrl", "rshift", "ralt", "rwin")
                if b[1] & MODIFIERS[n]]
        keys = [HID_KEY_NAMES.get(k, f"0x{k:02X}") for k in b[2:5] if k]
        return "key:" + "+".join(mods + keys) if (mods or keys) else "key:(none)"
    if t == 3:
        name = {v: k for k, v in MEDIA_KEYS.items()}.get(b[1], f"0x{b[1]:02X}")
        return f"media:{name}"
    return "special:" + bytes(b).hex(",")


def encode_button(spec):
    spec = spec.strip().lower()
    if spec in ("disable", "disabled", "off", "none"):
        return [0xFF, 0, 0, 0, 0]
    kind, _, val = spec.partition(":")
    if kind == "raw":
        vals = [int(x, 16) for x in val.replace(" ", ",").split(",") if x]
        if len(vals) != 5:
            raise SpathaError("raw button needs 5 hex bytes, e.g. raw:01,01,00,00,00")
        return vals
    if kind == "mouse":
        mask = 0
        for n in val.split("+"):
            if n not in MOUSE_BUTTONS:
                raise SpathaError(f"mouse button must be one of {', '.join(MOUSE_BUTTONS)}")
            mask |= MOUSE_BUTTONS[n]
        return [1, mask, 0, 0, 0]
    if kind == "key":
        mods, keys = 0, []
        for n in val.split("+"):
            if n in MODIFIERS:
                mods |= MODIFIERS[n]
            elif n in HID_KEYS:
                keys.append(HID_KEYS[n])
            elif n.startswith("0x"):
                keys.append(int(n, 16))
            else:
                raise SpathaError(f"unknown key '{n}'")
        if len(keys) > 3:
            raise SpathaError("at most 3 non-modifier keys")
        return [2, mods] + (keys + [0, 0, 0])[:3]
    if kind == "media":
        if val not in MEDIA_KEYS:
            raise SpathaError(f"media key must be one of {', '.join(MEDIA_KEYS)}")
        return [3, MEDIA_KEYS[val], 0, 0, 0]
    raise SpathaError("button action must be mouse:..., key:..., media:..., disable or raw:...")


def nibble_index(v):
    return LEVEL_STEPS.index(v) if v in LEVEL_STEPS else f"raw {v}"


def describe_profile(p, blk):
    lines = [f"Profile {p + 1}  (base 0x{profile_base(p):03X})"]
    poll = POLL_NAMES.get(blk[OFF_POLL_WIRED], f"raw {blk[OFF_POLL_WIRED]}")
    pollw = POLL_NAMES.get(blk[OFF_POLL_WIRELESS] & 0x0F, f"raw 0x{blk[OFF_POLL_WIRELESS]:02X}")
    lines.append(f"  polling        wired {poll} Hz, wireless {pollw} Hz")
    for st, (ox, oy) in OFF_DPI.items():
        x, y = blk[ox], blk[oy]
        xy = f"{dpi_from_raw(x)}" if x == y else f"X {dpi_from_raw(x)} / Y {dpi_from_raw(y)}"
        lines.append(f"  DPI stage {st}    {xy}   (raw {x}/{y})")
    a = blk[OFF_ANGLE_SNAP]
    lines.append(f"  angle snapping {'on, level %d' % (a & 0x0F) if a & 0x80 else 'off'}  (raw 0x{a:02X})")
    acc = blk[OFF_ACCEL]
    lines.append(f"  accel/decel    {acc & 0x0F} / {acc >> 4}")
    lines.append(f"  button resp.   raw {blk[OFF_BTN_RESPONSE]}  (Armoury index {(blk[OFF_BTN_RESPONSE] - 16) // 8})")
    lines.append(f"  lift-off       raw {blk[OFF_LIFT_OFF]}  calibration raw 0x{blk[OFF_CALIBRATION]:02X}")
    s = blk[OFF_SLEEP]
    lines.append(f"  sleep timer    {str(s) + ' min' if s in SLEEP_MINUTES else 'raw 0x%02X' % s}")
    lb = blk[OFF_LOW_BATT]
    lines.append(f"  low-batt alert {str(lb) + ' %' if lb in LOW_BATT_PCT else 'off/raw 0x%02X' % lb}")
    for z, off in sorted(LED_ZONES.items()):
        eff, lvl = blk[off], blk[off + 1]
        name = LED_EFFECT_NAMES.get(eff, f"raw 0x{eff:02X}")
        ncol = LED_EFFECTS.get(name, (0, 1))[1]
        cols = [blk[off + 2 + 3 * i: off + 5 + 3 * i].hex().upper() for i in range(max(ncol, 1))]
        lines.append(f"  LED zone {z}     {name:<10} bright wired {nibble_index(lvl & 15)}, "
                     f"wireless {nibble_index(lvl >> 4)}  colors {' '.join(cols)}")
    for i in range(BUTTON_SLOTS):
        off = OFF_BUTTONS + 5 * i
        b = blk[off: off + 5]
        lines.append(f"  button slot {i:2d} (0x{off:02X})  {decode_button(b):<22} [{b.hex(' ')}]")
    return "\n".join(lines)


def parse_int(s):
    return int(s, 0)


def parse_color(s):
    s = s.lstrip("#")
    if len(s) != 6:
        raise SpathaError(f"color '{s}' must be RRGGBB")
    return list(bytes.fromhex(s))


# --- commands ------------------------------------------------------------------

def resolve_profile(sp, args):
    if getattr(args, "profile", None):
        if not 1 <= args.profile <= PROFILE_COUNT:
            raise SpathaError(f"profile must be 1..{PROFILE_COUNT}")
        return args.profile - 1
    return sp.active_profile()


def ensure_backup(sp, args):
    if args.no_backup:
        return
    path = args.backup
    if os.path.exists(path):
        return
    print(f"First write: saving a full backup to {path} (takes ~30 s) ...", file=sys.stderr)
    data = sp.read(0, MEM_END, progress=True)
    with open(path, "wb") as f:
        f.write(data)
    print(f"Backup saved. Restore with: spatha.py restore {path}", file=sys.stderr)


def write_profile_bytes(sp, args, p, changes):
    """changes: {offset: value}. Writes only bytes that differ, then commits."""
    ensure_backup(sp, args)
    base = profile_base(p)
    offs = sorted(changes)
    current = sp.read(base + offs[0], offs[-1] - offs[0] + 1)
    n = 0
    for off in offs:
        v = changes[off] & 0xFF
        if current[off - offs[0]] != v:
            sp.write_byte(base + off, v)
            n += 1
    if n:
        back = sp.read(base + offs[0], offs[-1] - offs[0] + 1)
        bad = [o for o in offs if back[o - offs[0]] != changes[o] & 0xFF]
        if bad:
            raise SpathaError("verify failed at offsets " + ", ".join(f"0x{o:02X}" for o in bad))
        if not args.no_commit:
            sp.commit()
    print(f"profile {p + 1}: {n} byte(s) changed" + ("" if n else " (already set)"))


def cmd_list(args):
    for d in candidates(args.all):
        print(f"{d['vendor_id']:04X}:{d['product_id']:04X} bcd={d.get('release_number', 0):04X} "
              f"if={d.get('interface_number')} usage={d.get('usage_page', 0):04X}:{d.get('usage', 0):04X} "
              f"{d.get('product_string') or ''}\n    path={d['path']!r}")
        if args.probe:
            sp = probe(d, args.verbose)
            print("    -> responds to Spatha protocol" + (f" (report id 0x{sp.rid:02X})" if sp else "") if sp
                  else "    -> no reply")
            if sp:
                sp.dev.close()


def cmd_info(args):
    sp = open_device(args)
    reg = sp.profile_reg()
    print(f"active profile : {(reg & 15) + 1} of {reg >> 4}   (reg 0x19 = 0x{reg:02X})")
    state, val = sp.status()
    print(f"status (C4 08) : {state} {val if val is not None else ''}  "
          "(battery %, 0 = wired/unknown; unverified)")


def cmd_show(args):
    sp = open_device(args)
    profiles = range(PROFILE_COUNT) if args.all else [resolve_profile(sp, args)]
    for p in profiles:
        blk = sp.read(profile_base(p), PROFILE_USED, progress=True)
        print(describe_profile(p, blk))
        if args.hex:
            for i in range(0, len(blk), 16):
                print(f"    {i:02X}: {blk[i:i + 16].hex(' ')}")
        print()


def cmd_dump(args):
    sp = open_device(args)
    data = sp.read(0, MEM_END, progress=True)
    with open(args.file, "wb") as f:
        f.write(data)
    print(f"wrote {len(data)} bytes (0x000-0x{MEM_END - 1:03X}) to {args.file}")


def cmd_restore(args):
    sp = open_device(args)
    with open(args.file, "rb") as f:
        want = f.read()
    if len(want) != MEM_END:
        sys.exit(f"{args.file} is not a {MEM_END}-byte dump from this tool")
    cur = sp.read(0x100, MEM_END - 0x100, progress=True)
    diffs = [a for a in range(0x100, MEM_END) if cur[a - 0x100] != want[a]]
    print(f"{len(diffs)} byte(s) differ in profile memory (0x100-0x{MEM_END - 1:03X})")
    if args.dry_run:
        for a in diffs:
            print(f"  0x{a:03X}: {cur[a - 0x100]:02X} -> {want[a]:02X}")
        return
    for a in diffs:
        sp.write_byte(a, want[a])
    if want[REG_PROFILE] != sp.profile_reg():
        sp.write_byte(REG_PROFILE, want[REG_PROFILE])
    sp.commit()
    print("restored")


def cmd_profile(args):
    sp = open_device(args)
    sp.set_active_profile(args.number - 1)
    sp.commit()
    print(f"active profile -> {args.number}")


def cmd_dpi(args):
    sp = open_device(args)
    p = resolve_profile(sp, args)
    x = args.raw if args.raw is not None else raw_from_dpi(args.dpi)
    y = x if args.y is None else raw_from_dpi(args.y)
    ox, oy = OFF_DPI[args.stage]
    write_profile_bytes(sp, args, p, {ox: x, oy: y})


def cmd_polling(args):
    sp = open_device(args)
    p = resolve_profile(sp, args)
    code = POLL_CODES[args.hz]
    if args.wireless:
        cur = sp.read_byte(profile_base(p) + OFF_POLL_WIRELESS)
        write_profile_bytes(sp, args, p, {OFF_POLL_WIRELESS: (cur & 0xF0) | code})
    else:
        write_profile_bytes(sp, args, p, {OFF_POLL_WIRED: code})


def cmd_led(args):
    sp = open_device(args)
    p = resolve_profile(sp, args)
    raw, ncol = LED_EFFECTS[args.effect]
    colors = [parse_color(c) for c in (args.color or [])]
    if ncol and not colors:
        colors = [[0xFF, 0, 0]]
    while ncol and len(colors) < ncol:
        colors.append(colors[-1])
    zones = sorted(LED_ZONES) if args.zone == "all" else [int(args.zone)]
    changes = {}
    for z in zones:
        off = LED_ZONES[z]
        changes[off] = raw
        if args.brightness is not None or args.wireless_brightness is not None:
            cur = sp.read_byte(profile_base(p) + off + 1)
            lo = LEVEL_STEPS[args.brightness] if args.brightness is not None else cur & 15
            hi = LEVEL_STEPS[args.wireless_brightness] if args.wireless_brightness is not None else cur >> 4
            changes[off + 1] = (hi << 4) | lo
        for i, c in enumerate(colors[:12]):
            for j in range(3):
                changes[off + 2 + 3 * i + j] = c[j]
    write_profile_bytes(sp, args, p, changes)


def cmd_button(args):
    sp = open_device(args)
    p = resolve_profile(sp, args)
    if not 0 <= args.slot < BUTTON_SLOTS:
        raise SpathaError(f"slot must be 0..{BUTTON_SLOTS - 1} (see 'show')")
    off = OFF_BUTTONS + 5 * args.slot
    data = encode_button(args.action)
    write_profile_bytes(sp, args, p, {off + i: v for i, v in enumerate(data)})


def cmd_sleep(args):
    sp = open_device(args)
    p = resolve_profile(sp, args)
    v = 0xFF if args.minutes == "never" else int(args.minutes)
    if v != 0xFF and v not in SLEEP_MINUTES:
        raise SpathaError(f"minutes must be one of {SLEEP_MINUTES} or never")
    write_profile_bytes(sp, args, p, {OFF_SLEEP: v})


def cmd_battery_alert(args):
    sp = open_device(args)
    p = resolve_profile(sp, args)
    v = 0xFF if args.percent == "off" else int(args.percent)
    if v != 0xFF and v not in LOW_BATT_PCT:
        raise SpathaError(f"percent must be one of {LOW_BATT_PCT} or off")
    write_profile_bytes(sp, args, p, {OFF_LOW_BATT: v})


def cmd_read(args):
    sp = open_device(args)
    data = sp.read(args.addr, args.count)
    for i in range(0, len(data), 16):
        print(f"{args.addr + i:04X}: {data[i:i + 16].hex(' ')}")


def cmd_write(args):
    sp = open_device(args)
    ensure_backup(sp, args)
    data = [parse_int(v) for v in args.values]
    sp.write(args.addr, data)
    if not args.no_commit:
        sp.commit()
    print(f"wrote {len(data)} byte(s) at 0x{args.addr:04X}")


def cmd_raw(args):
    sp = open_device(args)
    payload = list(bytes.fromhex("".join(args.hex)))
    sp._send(payload)
    time.sleep(0.05)
    print(bytes(sp._recv()).hex(" "))


def cmd_commit(args):
    open_device(args).commit()
    print("sent C4 00 (EnableSetting)")


def build_parser():
    ap = argparse.ArgumentParser(description="ASUS ROG Spatha (original) configuration tool")
    ap.add_argument("--path", help="HID path to use instead of auto-detection")
    ap.add_argument("--report-id", type=parse_int, default=0x0C)
    ap.add_argument("--report-len", type=int, default=10)
    ap.add_argument("-v", "--verbose", action="store_true", help="print every packet")
    ap.add_argument("--backup", default="spatha_backup.bin",
                    help="automatic backup taken before the first write (default: %(default)s)")
    ap.add_argument("--no-backup", action="store_true")
    ap.add_argument("--no-commit", action="store_true", help="don't send C4 00 after writing")
    sub = ap.add_subparsers(dest="cmd", required=True)

    def add(name, fn, help_, profile=False):
        s = sub.add_parser(name, help=help_)
        s.set_defaults(fn=fn)
        if profile:
            s.add_argument("-p", "--profile", type=int, help="profile 1-6 (default: active)")
        return s

    s = add("list", cmd_list, "list ASUS HID collections")
    s.add_argument("--all", action="store_true", help="every ASUS device, not just Spatha")
    s.add_argument("--probe", action="store_true", help="test which collection answers")
    add("info", cmd_info, "active profile and status")
    s = add("show", cmd_show, "decode a profile", profile=True)
    s.add_argument("--all", action="store_true", help="all 6 profiles")
    s.add_argument("--hex", action="store_true", help="also print the raw bytes")
    s = add("dump", cmd_dump, "save device memory 0x000-0x6FF to a file")
    s.add_argument("file")
    s = add("restore", cmd_restore, "write a dump back (profile memory only)")
    s.add_argument("file")
    s.add_argument("--dry-run", action="store_true")
    s = add("profile", cmd_profile, "switch active profile")
    s.add_argument("number", type=int)
    s = add("dpi", cmd_dpi, "set a DPI stage", profile=True)
    s.add_argument("stage", type=int, choices=[1, 2])
    s.add_argument("dpi", type=int, nargs="?", help="DPI (50-8200, step 50)")
    s.add_argument("--y", type=int, help="separate Y-axis DPI")
    s.add_argument("--raw", type=parse_int, help="raw register value instead of DPI")
    s = add("polling", cmd_polling, "set polling rate", profile=True)
    s.add_argument("hz", type=int, choices=sorted(POLL_CODES))
    s.add_argument("--wireless", action="store_true", help="set the wireless rate instead")
    s = add("led", cmd_led, "set lighting", profile=True)
    s.add_argument("zone", choices=["all", "1", "2", "3"])
    s.add_argument("effect", choices=list(LED_EFFECTS))
    s.add_argument("--color", "-c", action="append", help="RRGGBB, repeat for more colors")
    s.add_argument("--brightness", "-b", type=int, choices=range(5), help="wired brightness 0-4")
    s.add_argument("--wireless-brightness", "-w", type=int, choices=range(5))
    s = add("button", cmd_button, "remap a button slot", profile=True)
    s.add_argument("slot", type=int, help="slot 0-13 (see 'show')")
    s.add_argument("action", help="mouse:left | key:ctrl+c | key:backspace | media:volup | disable | raw:..")
    s = add("sleep", cmd_sleep, "wireless sleep timer", profile=True)
    s.add_argument("minutes", help="1, 2, 3, 5, 10 or never")
    s = add("battery-alert", cmd_battery_alert, "low battery warning level", profile=True)
    s.add_argument("percent", help="10, 15, 20, 25, 30 or off")
    s = add("read", cmd_read, "read raw memory")
    s.add_argument("addr", type=parse_int)
    s.add_argument("count", type=parse_int, nargs="?", default=16)
    s = add("write", cmd_write, "write raw memory")
    s.add_argument("addr", type=parse_int)
    s.add_argument("values", nargs="+")
    s = add("raw", cmd_raw, "send a raw command (hex, without report id) and print the reply")
    s.add_argument("hex", nargs="+")
    add("commit", cmd_commit, "send EnableSetting (C4 00)")
    return ap


def main(argv=None):
    args = build_parser().parse_args(argv)
    if args.cmd == "dpi" and args.dpi is None and args.raw is None:
        sys.exit("give a DPI value or --raw")
    try:
        args.fn(args)
    except SpathaError as e:
        sys.exit(f"error: {e}")


if __name__ == "__main__":
    main()
