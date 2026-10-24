# MouseHud

A lightweight, ARM64-native controller for ASUS ROG mice. Derived from
[G-Helper](https://github.com/seerge/g-helper) by seerge, with everything
except the peripheral (HID) stack removed.

**Supports:**

| Mouse | USB IDs | Protocol |
|---|---|---|
| ROG Spatha X | wireless `0B05:1979`, wired `0B05:1977` | G-Helper's AsusMouse (vendored, unchanged) |
| ROG Spatha (original, 2016) | wired `0B05:181C`, dock `0B05:1824` | reverse engineered from Armoury Crate, see [SpathaRE](https://github.com/Whatevser/SpathaRE) |

Both get the same settings window: an Armoury-style button map with
callouts, DPI stages, polling, angle snapping, acceleration, lighting per zone,
sleep timer, low-battery alert, battery level and onboard profile switching.

---

## Why this exists

G-Helper does laptop control *and* peripheral control in one binary. The laptop
side is what causes both problems worth avoiding:

| Problem | Where it lives in G-Helper | Status here |
|---|---|---|
| Fights Armoury Crate / OEM apps | `AsusACPI.cs`, `Helpers/AsusService.cs` | **Deleted** |
| Pinned to x64 | `NvAPIWrapper.Net` (nvapi64.dll), `Pawn/RyzenSMU.bin`, `Pawn/IntelMSR.bin` | **Deleted** |

What's left is pure HID feature reports over `HidSharp`. No WMI, no ACPI, no
driver, no service, no elevation. It cannot break an OEM app because it never
touches one.

One caveat that *isn't* solved by deleting code: if Armoury Crate is running at
the same time, both apps will talk to the same HID interface and overwrite each
other's writes. That's coexistence, not hooking — but you still want AC's mouse
service stopped.

## Layout

```
Core/
  Devices/
    IMouseDevice.cs       UI-facing model shared by every mouse
    AsusMouseDevice.cs    adapter: G-Helper AsusMouse (Spatha X) -> IMouseDevice
  Spatha/                 original ROG Spatha
    SpathaCodec.cs        memory layout, value encodings, button codec, factory profile
    SpathaProtocol.cs     request/reply engine (DD/DE/DF reads+writes, C4 control)
    WinHidTransport.cs    feature reports via hid.dll, collection discovery
    SpathaDevice.cs       IMouseDevice for the Spatha
  MouseManager.cs         detection, hotplug, polling, serialised device I/O
  Usb/, Mouse/            vendored from G-Helper
UI/
  SettingsWindow          nav rail + Buttons / Performance / Lighting / Power
  SlotMap.cs, SpathaArt.cs  Armoury-style button map (art in Assets/)
  ProfilePopup            tray popup: battery, profiles
  ColorPicker, HidKeys.cs
Input/, Hotkeys/          combo replay (Spatha X), global hotkeys
MouseHud.Tests/           Spatha protocol tests against a simulated mouse (runs anywhere)
```

## Original Spatha notes

* Settings live in 6 onboard profiles. The window edits the **active** one;
  switching profiles in the header switches the mouse's profile.
* Profile 6 is the mouse's factory "Default" profile. Armoury Crate never
  writes it, so MouseHud keeps it read-only. Set
  `"spatha_allow_default_profile": 1` in config to unlock it.
* Buttons can be bound to **real key shortcuts** (Ctrl+Shift+Esc, Backspace,
  ...). The mouse sends them by itself, no software needed: use the
  *Shortcut* tab under Buttons and press the keys.
* Every write is read back and verified, then committed with the same
  "EnableSetting" command Armoury uses.
* Close Armoury Crate first. Both programs talking to the mouse at once
  works but is slow and confusing.

## Multi-key bindings (Ctrl+C, Alt+Tab, ...)

The mouse cannot emit a modifier combo. Binding a button to "Copy" makes the
firmware send a **carrier key** — F13-F24, one per combo action code
0x0068-0x0073 — and the host is expected to replay the real sequence. Without
that host half, the button sends F13 and nothing happens.

`ComboDispatcher` registers a global hotkey for each carrier that is actually
bound (not all twelve — that would take F13-F24 from other apps for nothing) and
rebuilds the set whenever bindings, profile or device change. `KeyboardSynth`
then replays via `SendInput`.

Two details that matter:

- **Release order is reversed.** Ctrl must still be held when C goes up, or the
  target app sees a bare C.
- **Real scan codes are sent**, and the extended-key flag is set only for keys
  that are genuinely extended. Games reading raw input or DirectInput commonly
  ignore synthetic events that carry no scan code.

Each action's keystrokes are editable in Settings → Buttons, or directly in
config as `mouse_combo_F13` ... `mouse_combo_F24`. Both `Ctrl+C` and the raw
`0xA2 0x43` hex form are accepted. `combo_hold_ms` (default 10) sets how long
keys are held down.

If another application already owns a carrier key, registration fails and the
log says so explicitly — that button will silently do nothing until the conflict
is resolved.

## Onboard profile tracking

The Spatha X switches profiles from its own button combo and does not notify the
host. `MouseManager` therefore polls a single 0x12 status read on a timer
(`profile_poll_ms`, default 1000, minimum 250, `0` disables) and only does a full
re-sync when the value actually moved — profiles carry their own DPI, lighting
and bindings, so everything on screen has to be re-read.

Polls are skipped rather than queued when the HID bus is busy, so a poll can
never stack up behind a slow write. External changes raise `ProfileChanged` with
`External = true` and pop a tray balloon (`notify_profile_change`).

### Changes to vendored files

Kept deliberately minimal so upstream fixes can be re-applied by hand:

1. `GHelper` -> `MouseHud` namespace rename (mechanical, whole-token).
2. Namespace flattening: `AnimeMatrix.Communication[.Platform]` -> `Core.Usb`,
   `Peripherals[.Mouse[.Models]]` -> `Core.Mouse[.Models]`.
3. `AsusMouse.cs`: removed `MapAuraToLightingMode()` and `SyncFromKeyboardAura()`.
   These were the only two methods referencing keyboard Aura state, and they
   dragged in `USB/Aura.cs`, which drags in the whole laptop stack.

Nothing else. `AsusMouse.cs` still calls `Logger` and `AppConfig` at four sites;
the shims in `Core/` are API-compatible on purpose so the file needs no edits.

## Build

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-arm64
```

Requires the .NET 8 SDK. To build an x64 binary instead:
`dotnet publish -c Release -r win-x64 -p:Platform=x64`. Output is framework-dependent single-file; add
`-p:SelfContained=true` if you'd rather not install the runtime.

To cross-build from an x64 box, `-r win-arm64` still works — nothing native is
compiled, only the apphost is architecture-specific.

## Config

`%APPDATA%\MouseHud\config.json`. Created on first write. Hotkey gestures use
WPF `Key` names, so `F13`, `NumPad1`, `OemPlus`, `OemTilde` all parse. Set a
value to `""` to disable that hotkey.

```json
{
  "hotkey_profile_1":    "Ctrl+Alt+1",
  "hotkey_profile_2":    "Ctrl+Alt+2",
  "hotkey_profile_3":    "Ctrl+Alt+3",
  "hotkey_profile_4":    "Ctrl+Alt+4",
  "hotkey_profile_5":    "Ctrl+Alt+5",
  "hotkey_profile_next": "Ctrl+Alt+OemPlus",
  "hotkey_profile_prev": "Ctrl+Alt+OemMinus",
  "hotkey_popup":        "Ctrl+Alt+M",
  "usb_packet_logger":   0
}
```

Set `usb_packet_logger` to `1` to dump every HID exchange to the log — that's the
switch you'll want when mapping the original Spatha's protocol.

Tray menu -> *Reload hotkeys* re-reads the file without restarting.

## Spatha X specifics

The device stores **5 profiles onboard**, each with its own DPI stages, lighting
and button bindings. `SetProfile()` switches the hardware profile, so a hotkey
press swaps everything at once — no software layer intercepting input.

14 rebindable slots: 8 standard (clicks, scroll, DPI, side back/forward) read
from binding group 0, plus the 6 side buttons from group 1.

## Credits

* [G-Helper](https://github.com/seerge/g-helper) by **seerge and contributors**:
  the HID stack, the AsusMouse protocol and the Spatha X support MouseHud is
  built on, and a lot of the ideas.
* Original Spatha protocol: reverse engineered from Armoury Crate's
  `IfSpatha.dll`, `SPATHA HID Library`, `SPATHA SYNC Dll` and
  `AacMouseExternHal`, verified on a real mouse. Write-up:
  [SpathaRE](https://github.com/Whatevser/SpathaRE).
* Button-map artwork: line renders of the ROG Spatha.

## Tests

```
cd MouseHud.Tests
dotnet run
```

Runs the original-Spatha protocol and device code against a simulated mouse
(busy replies, stale replies, dropped writes, profile switching, battery).
Pure .NET, so it also runs on Linux and macOS.

## Licence

GPL-3.0, inherited from G-Helper. Original work © seerge and contributors.
