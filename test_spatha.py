"""Tests for spatha.py against a simulated mouse that implements the protocol
as decoded from the Armoury Crate DLLs. Run: python -m pytest -q"""

import sys
import types

import pytest


class FakeSpatha:
    """Emulates the firmware side: report 0x0C, 10-byte feature reports,
    replies without a report-id byte (B0 ...), like the real device."""

    def __init__(self):
        self.mem = bytearray(0x2000)
        self.mem[0x19] = 0x60
        for p in range(6):
            b = 0x100 * (p + 1)
            self.mem[b + 0x00] = 1                      # 1000 Hz
            self.mem[b + 0x04:b + 0x08] = bytes([31, 31, 7, 7])
            for z in (0x5D, 0x37, 0x11):
                self.mem[b + z] = 10                    # breathing
                self.mem[b + z + 1] = 0x7E
                self.mem[b + z + 2:b + z + 5] = b"\xff\x00\x00"
            for i in range(14):
                self.mem[b + 0x83 + 5 * i:b + 0x88 + 5 * i] = bytes([1, 1 << (i % 5), 0, 0, 0])
            self.mem[b + 0xC9] = 24
            self.mem[b + 0xCA] = 5
            self.mem[b + 0xCB] = 15
            self.mem[b + 0xCC] = 1
        self.reply = b""
        self.sent = []
        self.busy_once = True

    # hid.device API
    def open_path(self, path):
        pass

    def close(self):
        pass

    def send_feature_report(self, buf):
        buf = bytes(buf)
        assert buf[0] == 0x0C and len(buf) == 10, buf.hex(" ")
        self.sent.append(buf)
        cmd, lo, hi = buf[1], buf[2], buf[3]
        addr = lo | hi << 8
        if cmd == 0xDF:
            self.reply = bytes([0xB0, 0xC0, lo, hi, self.mem[addr]])
        elif cmd == 0xDD:
            self.reply = bytes([0xB0, 0xC0, lo, hi]) + bytes(self.mem[addr:addr + 4])
        elif cmd == 0xDE:
            self.mem[addr] = buf[4]
            self.reply = bytes([0xB0, 0xC1, lo, hi])
        elif cmd == 0xC4 and buf[2] == 0x08:
            self.reply = bytes([0xB0, 0x55, 0xAA, 77])
        elif cmd == 0xC4:
            self.reply = b""
        return len(buf)

    def get_feature_report(self, rid, n):
        if self.busy_once:                   # firmware sometimes answers B1 first
            self.busy_once = False
            return [0xB1] + [0] * 9
        return list(self.reply.ljust(10, b"\x00"))[:n]


@pytest.fixture
def mouse(monkeypatch, tmp_path):
    fake = FakeSpatha()
    mod = types.ModuleType("hid")
    mod.enumerate = lambda vid, pid: [{
        "path": b"fake", "vendor_id": 0x0B05, "product_id": 0x181C,
        "release_number": 0x4201, "usage_page": 1, "usage": 6,
        "interface_number": 1, "product_string": "ROG Spatha"}]
    mod.device = lambda: fake
    monkeypatch.setitem(sys.modules, "hid", mod)
    monkeypatch.chdir(tmp_path)
    import spatha
    monkeypatch.setattr(spatha.time, "sleep", lambda s: None)
    return fake, spatha


def run(spatha, *argv):
    spatha.main(list(argv))


def base(p):
    return 0x100 * (p + 1)


def test_info_and_show(mouse, capsys):
    fake, spatha = mouse
    run(spatha, "info")
    out = capsys.readouterr().out
    assert "active profile : 1 of 6" in out
    run(spatha, "show")
    out = capsys.readouterr().out
    assert "1600" in out and "400" in out and "1000 Hz" in out
    assert "breathing" in out and "mouse:left" in out


def test_dpi_writes_both_axes_and_commits(mouse):
    fake, spatha = mouse
    run(spatha, "--no-backup", "dpi", "1", "3200", "-p", "2")
    assert fake.mem[base(1) + 4] == 63 and fake.mem[base(1) + 5] == 63
    assert fake.mem[base(0) + 4] == 31                    # other profile untouched
    assert fake.sent[-1][1:3] == bytes([0xC4, 0x00])     # EnableSetting last


def test_button_backspace(mouse):
    fake, spatha = mouse
    run(spatha, "--no-backup", "button", "7", "key:backspace")
    o = base(0) + 0x83 + 5 * 7
    assert bytes(fake.mem[o:o + 5]) == bytes([2, 0, 0x2A, 0, 0])
    run(spatha, "--no-backup", "button", "7", "key:ctrl+shift+esc")
    assert bytes(fake.mem[o:o + 5]) == bytes([2, 0x03, 0x29, 0, 0])
    assert spatha.decode_button(fake.mem[o:o + 5]) == "key:ctrl+shift+esc"


def test_led_all_zones(mouse):
    fake, spatha = mouse
    run(spatha, "--no-backup", "led", "all", "static", "-c", "00ff80", "-b", "2", "-w", "0")
    for z in (0x5D, 0x37, 0x11):
        o = base(0) + z
        assert fake.mem[o] == 1 and fake.mem[o + 1] == 0x07
        assert bytes(fake.mem[o + 2:o + 5]) == b"\x00\xff\x80"


def test_polling_profile_switch_sleep(mouse):
    fake, spatha = mouse
    run(spatha, "--no-backup", "polling", "500")
    run(spatha, "--no-backup", "polling", "125", "--wireless")
    assert fake.mem[base(0)] == 2 and fake.mem[base(0) + 0xCC] == 8
    run(spatha, "--no-backup", "sleep", "never")
    run(spatha, "--no-backup", "battery-alert", "30")
    assert fake.mem[base(0) + 0xCA] == 0xFF and fake.mem[base(0) + 0xCB] == 30
    run(spatha, "profile", "3")
    assert fake.mem[0x19] == 0x62
    assert any(s[1:4] == bytes([0xC4, 0x07, 0x62]) for s in fake.sent)


def test_auto_backup_and_restore(mouse, tmp_path):
    fake, spatha = mouse
    orig = bytes(fake.mem[:0x700])
    run(spatha, "dpi", "2", "800")                      # triggers automatic backup
    assert (tmp_path / "spatha_backup.bin").read_bytes() == orig
    assert fake.mem[base(0) + 6] == 15
    run(spatha, "restore", "spatha_backup.bin")
    assert bytes(fake.mem[:0x700]) == orig


def test_reply_with_echoed_report_id(mouse):
    fake, spatha = mouse
    real = fake.get_feature_report
    fake.get_feature_report = lambda rid, n: [0x0C] + real(rid, n)[:n - 1]
    run(spatha, "info")


def test_identify_maps_and_restores(mouse, monkeypatch, capsys):
    fake, spatha = mouse
    orig = bytes(fake.mem[0x183:0x183 + 70])
    seen = []
    keys = iter(["a", "f", "m", "\r"])

    def fake_getch():
        seen.append(bytes(fake.mem[0x183:0x183 + 70]))
        return next(keys)
    monkeypatch.setattr(spatha, "getch", fake_getch)
    run(spatha, "--no-backup", "identify")
    out = capsys.readouterr().out
    assert "slot  1" in out and "slot  6" in out and "slot 13" in out
    assert seen[0][0:5] == orig[0:5]                       # left click kept
    assert seen[0][5 * 13:5 * 13 + 5] == bytes([2, 0, 0x10, 0, 0])   # slot 13 -> 'm'
    assert bytes(fake.mem[0x183:0x183 + 70]) == orig        # restored


def test_special_buttons_roundtrip(mouse):
    fake, spatha = mouse
    for spec in ("dpi", "wheel:up", "wheel:down"):
        assert spatha.decode_button(spatha.encode_button(spec)) == spec


def test_identify_interrupted_then_recovered(mouse, monkeypatch, tmp_path):
    fake, spatha = mouse
    orig = bytes(fake.mem[0x183:0x183 + 70])

    def killed():
        raise SystemExit("window closed")
    monkeypatch.setattr(spatha, "getch", killed)
    real_restore = spatha.restore_identify
    monkeypatch.setattr(spatha, "restore_identify", lambda sp: None)   # finally never ran
    with pytest.raises(SystemExit):
        run(spatha, "--no-backup", "identify")
    assert bytes(fake.mem[0x183:0x183 + 70]) != orig
    assert (tmp_path / "spatha_identify_saved.bin").exists()
    monkeypatch.setattr(spatha, "restore_identify", real_restore)
    run(spatha, "--no-backup", "identify")                  # second run recovers
    assert bytes(fake.mem[0x183:0x183 + 70]) == orig
    assert not (tmp_path / "spatha_identify_saved.bin").exists()


def test_reset_profile(mouse):
    fake, spatha = mouse
    run(spatha, "--no-backup", "button", "3", "key:a", "-p", "4")
    run(spatha, "--no-backup", "reset-profile", "-p", "4")
    assert bytes(fake.mem[0x400:0x4CD]) == spatha.FACTORY_PROFILE
    assert len(spatha.FACTORY_PROFILE) == 0xCD
    assert bytes(fake.mem[0x100:0x1CD]) != spatha.FACTORY_PROFILE   # other profiles untouched


def test_default_profile_needs_flag(mouse):
    fake, spatha = mouse
    before = bytes(fake.mem[0x600:0x700])
    with pytest.raises(SystemExit):
        run(spatha, "--no-backup", "dpi", "1", "800", "-p", "6")
    assert bytes(fake.mem[0x600:0x700]) == before
    run(spatha, "--no-backup", "--allow-default", "dpi", "1", "800", "-p", "6")
    assert fake.mem[0x604] == 15
