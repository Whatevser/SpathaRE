# spatha.py

A command-line tool for the **original ASUS ROG Spatha** (model L701, 2016, not the
Spatha X). It's the tool the protocol was decoded and tested with. For everyday
use, the [SpathaRE app](../../README.md) is easier; this one is for scripting,
backups and poking at the protocol. The protocol itself is written up in
[PROTOCOL.md](../../PROTOCOL.md).

Works over the cable (`0B05:181C`) and through the dock (`0B05:1824`), on
Windows and Linux.

## Status

Tested on a real Spatha, wired and through the dock: profiles, DPI, buttons,
polling, sleep timer, battery alert, battery level, firmware versions, backup
and restore. `test_spatha.py` runs the code against a simulated mouse
(`python -m pytest`).

Lighting is only partly confirmed. The zone names (logo, wheel, base) and
effects were matched by changing them in Armoury Crate and reading the memory
back. The test mouse's LEDs never lit up, so what each setting looks like on a
working mouse is unverified.

## Setup

```
pip install hidapi
python spatha.py list --probe          # which HID collection answers
python spatha.py dump original.bin     # back up the settings memory first
```

Close Armoury Crate first so the two programs don't talk to the mouse at the
same time. On Linux you need access to `/dev/hidraw*` (root or a udev rule).
Through the dock the mouse must be switched on; every request goes over radio,
so reads are slower than over the cable.

## Button slots

`button` and `identify` use slot numbers. Side buttons are numbered as in the
SpathaRE app: top row 1-2-3, bottom row 4-5-6, front to back.

| Slot | Button | Factory |
|---|---|---|
| 0 | Left click | left click |
| 1 | Right click | right click |
| 2 | Wheel click | middle click |
| 3 | Back | back |
| 4 | Forward | forward |
| 5 | DPI button | DPI switch |
| 6 | Side button 3 | key 9 |
| 7 | Side button 6 | key = |
| 8 | Side button 2 | key 8 |
| 9 | Side button 5 | key - |
| 10 | Side button 1 | key 7 |
| 11 | Side button 4 | key 0 |
| 12 | Wheel up | wheel up |
| 13 | Wheel down | wheel down |

Profile 6 is the mouse's "Default" profile. Armoury Crate never writes it, so
the tool refuses to either, unless you pass `--allow-default`.

## Examples

Reading:

```
python spatha.py info                  # active profile, battery, firmware versions
python spatha.py show                  # decode the active profile
python spatha.py show --all --hex      # all 6 profiles plus raw bytes
```

Settings (`-p N` picks a profile, default is the active one):

```
python spatha.py profile 2             # switch the active profile
python spatha.py dpi 1 1600            # DPI stage 1 (50-8200, steps of 50)
python spatha.py dpi 2 800 -p 2
python spatha.py polling 1000          # wired polling rate
python spatha.py polling 500 --wireless
python spatha.py sleep 10              # 1, 2, 3, 5, 10 or never
python spatha.py battery-alert 15      # 10, 15, 20, 25, 30 or off
python spatha.py led all static -c 00ff80 -b 4 -w 2
python spatha.py led wheel breathing -c ff0000 -c 0000ff
python spatha.py led logo battery
python spatha.py reset-profile -p 1    # Armoury's factory defaults for one profile
```

`led` zones are `logo`, `wheel`, `base` or `all`; effects are `static`,
`breathing`, `colorcycle`, `random`, `reactive` and `battery`. `-b` is the wired
brightness and `-w` the wireless one, each 0-4.

Buttons:

```
python spatha.py button 10 key:ctrl+c          # side button 1 -> Ctrl+C
python spatha.py button 11 key:ctrl+shift+esc  # side button 4
python spatha.py button 6 media:volup          # side button 3
python spatha.py button 2 mouse:middle
python spatha.py button 9 dpi
python spatha.py button 8 wheel:up
python spatha.py button 7 disable
python spatha.py identify                      # press buttons to see their slot numbers
```

Actions: `mouse:left|right|middle|back|forward`, `key:` with up to 3 keys plus
`ctrl`, `shift`, `alt`, `win` (and `rctrl`, `rshift`, `altgr`, `rwin`), `media:`
`playpause|stop|next|prev|mute|volup|voldown`, `dpi`, `wheel:up|down`,
`disable`, or `raw:` with 5 hex bytes. The mouse sends `key:` combinations by
itself, so they keep working without any software running. (The SpathaRE app
uses F13-F24 combos instead, the same way on both Spathas.)

Backup and restore:

```
python spatha.py dump backup.bin       # memory 0x000-0x6FF
python spatha.py restore backup.bin    # writes the profile memory back
python spatha.py restore backup.bin --dry-run
```

## Diagnostics

```
python spatha.py -v read 0x100 0xD1    # -v prints every packet
python spatha.py write 0x1CA 0x05
python spatha.py raw C4 08 00          # send any command, print the reply
python spatha.py commit                # send EnableSetting (C4 00)
python spatha.py listen                # print what the mouse and dock send
python spatha.py --no-probe raw C4 08 00   # talk to the dock with the mouse off
python spatha.py led-test -c ff0000    # drive the LEDs directly (Aura Sync style)
```

`listen` shows the events the dock pushes: `03 A2` profile, `03 A3` DPI stage,
`03 A8` battery level, `03 AA 01/00` mouse put on / taken off the dock. It keeps
running when the dock is unplugged and picks it up again.

With the mouse switched off, the dock answers every command with `B1` (busy) and
then `B2`. It relays everything to the mouse and answers nothing itself.

`--path` and `--report-id` pick a HID collection and report ID by hand. Normally
the tool finds them: the vendor collection `Col03` with report `0x0C`.

## Safety

* Before the first write, the tool saves the device memory to
  `spatha_backup.bin`, unless that file already exists or you pass
  `--no-backup`. `restore` writes a dump back.
* Every write is read back and verified, then committed with `C4 00`, the same
  "EnableSetting" Armoury sends. `--no-commit` skips the commit.
* The tool only writes the settings memory Armoury writes too. It has no
  firmware or bootloader commands.
* `raw` and `write` send whatever you give them. Only use them with commands and
  addresses documented in PROTOCOL.md.
