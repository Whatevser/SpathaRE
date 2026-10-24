# spatha-tool

Configure the original ASUS ROG Spatha (non-X, `0B05:181C`) without Armoury Crate. The protocol is documented in [PROTOCOL.md](PROTOCOL.md).

**Status:** not yet tested on real hardware. Everything was decoded from Armoury Crate's DLLs and checked against a simulated mouse (`test_spatha.py`).

## Setup

```
pip install hidapi
python spatha.py list --probe      # should report "responds to Spatha protocol"
python spatha.py dump original.bin # back up everything first (~30 s)
```

Close Armoury Crate first so the two programs don't send commands at the same time. On Linux you need access to `/dev/hidraw*` (use root or a udev rule).

## Examples

```
python spatha.py info
python spatha.py show                 # decode the active profile
python spatha.py show --all --hex     # all 6 profiles plus raw bytes
python spatha.py dpi 1 1600
python spatha.py dpi 2 800 -p 2       # profile 2
python spatha.py polling 1000
python spatha.py polling 500 --wireless
python spatha.py led all static -c 00ff80 -b 4 -w 2
python spatha.py led wheel breathing -c ff0000 -c 0000ff
python spatha.py led logo battery
python spatha.py button 5 key:backspace
python spatha.py button 6 key:ctrl+shift+esc
python spatha.py button 7 media:volup
python spatha.py button 8 mouse:middle
python spatha.py sleep 10
python spatha.py battery-alert 15
python spatha.py profile 2
python spatha.py identify             # press buttons to see their slot numbers
python spatha.py restore original.bin
python spatha.py reset-profile -p 1   # Armoury's factory defaults for one profile
```

Raw access for experimenting:

```
python spatha.py -v read 0x100 0xD1   # -v prints every packet
python spatha.py write 0x1CA 0x05
python spatha.py raw C4 08 00         # send any command and print the reply
```

## Safety

* Before the first write, the tool saves the whole device memory to `spatha_backup.bin`, unless that file already exists or you pass `--no-backup`. `restore` writes a dump back.
* Every write is read back and verified, then committed with `C4 00` (the same "EnableSetting" Armoury uses). Pass `--no-commit` to skip the commit.
* The tool never touches firmware or bootloader commands. It only writes the settings memory that Armoury writes too.

## Things to verify on a real mouse

1. ~~DPI formula~~ (confirmed: `(raw+1)*50`).
2. Which physical button each slot 0–13 belongs to. Run `python spatha.py identify`, press each button, then press Enter. Your bindings are restored afterwards.
4. What `info` reports for battery and status, both wired and wireless.
