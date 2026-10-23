# ASUS ROG Spatha (original, non-X) – USB configuration protocol

Reverse engineered from the Armoury Crate Spatha plugin DLLs, with no captures from real hardware:

| DLL | What it showed |
|---|---|
| `IfSpatha.dll` (x86) | Device detection, register map, Armoury setting ↔ register converters, read/write/ack logic |
| `SPATHA_HID_Library_C++(_x64).dll` | Bulk profile load/save, the "EnableSetting" commit, LED preview |
| `SPATHA_SYNC_Dll(_x64).dll` | Aura Sync direct mode, default-LED writer (independently confirms the address map) |
| `ArmourySDK.dll` | Generic Aura SDK loader, nothing Spatha-specific |

Confidence: **[C]** = read directly from code, **[I]** = inferred from how Armoury uses it. Verify [I] items on a real mouse.

---

## 1. Transport

* USB `0B05:181C` (wired, from g-helper issue #5892). `IfSpatha.dll` doesn't check the PID. It matches HID devices whose **bcdDevice** is `0x4200`, `0x4201`, `0xD000` or `0xD002`. `0x42xx` is probably the mouse and `0xD0xx` the RF dongle **[I]**.
* All traffic uses **HID feature reports** (`HidD_SetFeature` / `HidD_GetFeature`). No interrupt endpoints are involved. **[C]**
* Normal mode: the HID **keyboard** collection (`GUID_DEVINTERFACE_KEYBOARD`), **report ID `0x0C`**, **10-byte** report (ID + 9). **[C]**
* Alternate mode, chosen by a flag in Armoury: the mouse collection whose `FeatureReportByteLength == 9`, report ID `0x00`, 9 bytes. **[C]**
* Requests are `RID CMD p1 p2 p3 …` padded with zeros. Replies (GetFeature) begin with **`B0`** in byte 0, so the firmware doesn't echo the report ID. `B1` = busy/NAK and `FF` = no data yet; on either, retry the GetFeature. **[C]**
* Armoury's timing: SetFeature → ~30 ms → GetFeature, retried up to 15× at 30 ms intervals; the whole command is retried up to 300×. **[C]**
* The newer `SPATHA_HID_Library` passes buffer lengths of 0x805 (set) and 0x21 (get). Windows accepts oversized buffers, so these lengths don't indicate the real report size.

## 2. Commands

`lo hi` = 16-bit little-endian address.

| Request (after RID) | Reply | Meaning | |
|---|---|---|---|
| `DF lo hi 00 00` | `B0 C0 lo hi v` | read 1 byte | [C] |
| `DD lo hi` | `B0 C0 lo hi d0 d1 d2 d3` | read 4 bytes | [C] |
| `DB lo hi 00 00 00 00 00` | `B0 lo hi ?? d0..d19` (reply read with **report ID 0x10**) | read 20 bytes (only used by the newer HID library) | [C] |
| `DE lo hi v` | `B0 C1 lo hi` | write 1 byte (addr < 0x2000) | [C] |
| `C4 00 00 00 00` | – | **EnableSetting / commit**. Sent after every settings write, followed by a 250 ms wait | [C] |
| `C4 07 <reg19> 00 00` | – | load/activate profile (sent after writing reg 0x19) | [C] |
| `C4 08 00` | `B0 55 AA n` or `B0 3C C3 n` | status, probably battery level; the `3C C3` form seems to mean "charging" | [C] format, [I] meaning |
| `C4 08 02` | same | status of the RF link/dongle | [I] |
| `C4 06 02 01/00` | – | enter/leave Aura Sync direct mode (IfSpatha sends `FF` to enable "driver mode") | [C] |
| `C4 03 05 01 <frame>` / `C4 03 05 00 00` → `B0 C5 frame` | | set/get Aura Sync frame | [C] |
| `C4 0F 00 <off> <v>` | – | live (non-saved) write of a profile register, used for LED preview | [C] |
| `C4 03 04 01 00` | – | issued after an LED preview | [C] |
| `C4 04 00/01` | – | start/stop surface calibration | [I] |
| `C4 11 00` | `B0 C4 …` | unknown query | [C] |
| `C4 14 00 02/01 01/00` → `B0 F0 …` | | "KData" mode: sensor data dump (8×16-bit) | [C] |
| report `10 00 F0 00 + 9 bytes` | – | LED preview, 3×RGB (newer HID library) | [C] |

While in driver mode the mouse sends **input report 3**: `03 A2 p` = profile changed, `03 A3 v` = DPI changed. **[C]**

## 3. Memory map

```
0x0019            active profile: (profile_count << 4) | index   e.g. 0x60 = profile 1 of 6   [C]
0x0100 + 0x100*p  profile p (p = 0..5), 0xD1 bytes used                                 [C]
```

Armoury's bulk save writes only 0x100–0x5FF (profiles 1–5). Every per-register path accepts p ≤ 5.

### Profile block (offsets from `0x100*(p+1)`)

| Off | Field | Encoding | |
|---|---|---|---|
| 0x00 | polling rate (wired) | report interval: 8=125 Hz, 4=250, 2=500, 1=1000 (Armoury also knows 0x10 and 0x00) | [C] values, [I] Hz |
| 0x01 | angle snapping | `0x80\|level` = on; Armoury writes `0x06` for off | [C] |
| 0x02 | lift-off / calibration height | `135 + 3*height` | [C] |
| 0x03 | ? (LoadSettings reads its low nibble) | | |
| 0x04/0x05 | DPI stage 1 X / Y | raw; **DPI = (raw+1)×50** (ADNS-9800: 50 CPI steps, 0xA4 = 8200). Armoury default 31 = 1600 | [C], confirmed on hardware |
| 0x06/0x07 | DPI stage 2 X / Y | same as above (default 7 = 400) | |
| 0x0C | acceleration / deceleration | low nibble accel, high nibble decel, 0–10 each | [C] |
| 0x11–0x36 | LED zone 3 | see below | [C] |
| 0x37–0x5C | LED zone 2 | | [C] |
| 0x5D–0x82 | LED zone 1 | | [C] |
| 0x83–0xC8 | 14 button slots × 5 bytes | see below | [C] |
| 0xC9 | button response | `16 + 8*index` (index 0–6) | [C] |
| 0xCA | sleep timer | minutes 1/2/3/5/10, `FF` = never | [C] values, [I] meaning |
| 0xCB | low-battery alert | percent 10/15/20/25/30, `FF` = off | [C] values, [I] meaning |
| 0xCC | polling rate (wireless) | low nibble, same codes as 0x00 | [C] |
| 0xD0 | surface calibration mode | `FF` = off, else material+2 | [C] |

#### LED zone (38 bytes)

```
+0  effect      1=static(1 colour) 10=breathing(2) 5=colour cycle(12) 12=reactive(1)
                6, 11, 14 = Armoury effects 3/5/6 (no colours, names unknown)
+1  brightness  low nibble = wired, high nibble = wireless; steps 0,4,7,11,14 (= Armoury 0..4)
+2  RGB × 12
```

Armoury's default (from the SYNC DLL) for every zone is `0A 7E FF 00 00 …`: breathing, wired brightness 4, wireless brightness 2, red. Which physical zone is 1/2/3 (logo, wheel, side) still needs checking on hardware.

#### Button slot (5 bytes)

```
01 mask 00 00 00          mouse button, mask: 1=L 2=R 4=M 8=Back 16=Fwd
02 mods k1 k2 k3          keyboard: HID modifier bitmask (1 LCtrl, 2 LShift, 4 LAlt, 8 LWin,
                          0x10-0x80 right-hand) + up to 3 HID usage IDs (page 7)
03 usage 00 00 00         consumer key: B5 next, B6 prev, B7 stop, CD play/pause,
                          E2 mute, E9 vol+, EA vol-
00 00 / 00 01             wheel up / wheel down (confirmed)
04 01                     Armoury function 250
0B 00 00 00 00            DPI cycle (confirmed)
FF ..                     disabled
```

Armoury groups the slots as two lists: `[0x83 0x88 0x8D 0xBF 0xC4 0x9C 0x97 0x92]` (8 buttons) and `[0xA1 0xA6 0xAB 0xB0 0xB5 0xBA]` (6 buttons). Factory bindings read from a real mouse:

| Slot | Off | Default | Likely button |
|---|---|---|---|
| 0–4 | 0x83–0x97 | mouse L, R, M, Back, Fwd | left, right, wheel click, side back/forward |
| 5 | 0x9C | `0B 00 00 00 00` | DPI button (confirmed) |
| 6–11 | 0xA1–0xBA | keys 9, =, 8, -, 7, 0 | 6-button side grid |
| 12 | 0xBF | `00 00 00 00 00` | wheel up (confirmed) |
| 13 | 0xC4 | `00 01 00 00 00` | wheel down (confirmed) |

`spatha.py identify` temporarily maps slots 1–13 to letters and reports which slot each physical button belongs to.

Keys are stored as raw HID usages, so any key can be bound even where Armoury's UI won't allow it. That likely explains the Backspace issue in the g-helper report: Backspace is `02 00 2A 00 00`.

## 4. Typical sequences

* **Read profile**: `DF 19` → profile index; then `DD` for 0x00–0xCC of the block, plus `DF` at 0xD0.
* **Write setting**: `DE addr v` for each changed byte (wait for `B0 C1 lo hi`), then `C4 00 00 00 00` and a 250 ms wait.
* **Switch profile**: `DE 19 00 (0x60|p)` then `C4 07 (0x60|p)`.

## 5. Not covered yet

* Macros (`SetMacro`). The code converts Armoury macro events and stores them through a separate routine (`sub_10006dc0`), not decoded.
* RF dongle PID/usage, and whether the dongle wants report ID 0 / 9 bytes.
* Names of LED effects 3/5/6 and of the special button functions.
