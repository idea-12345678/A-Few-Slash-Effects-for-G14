# A Few Slash Effects for G14

An unofficial fork of [G-Helper](https://github.com/seerge/g-helper) that adds four extra **Slash Lighting**
patterns for ROG Zephyrus G14/G16 laptops, including a live **battery gauge**.

This branch (`main`) is upstream G-Helper **0a1538a** plus one commit with the Slash changes.
The two touched files are `app/AnimeMatrix/SlashDevice.cs` and `app/AniMatrixControl.cs`; the patch was
written against upstream v0.288 but applies cleanly because those files did not change since.

> Not an official ASUS or G-Helper build. Firmware presets, keyboard backlight, fans, power limits and
> everything else stay upstream.

## Patterns

They appear in **G-Helper → Slash/Matrix area → animations dropdown**:

| Name in dropdown | `matrix_running` | Behaviour |
|---|---|---|
| **流水灯测试01** | 22 | 7-segment chaser, **0.25 s/step**; lit segments follow the performance mode (Silent → 1, Balanced → 2, Turbo/other → 3) |
| **流水灯02** | 23 | Same chaser at **0.15 s/step** |
| **蓝色大肥鱼01** | 24 | **60 ms/frame** binary (full-on / full-off only) procedural light show — long evolving scene chain; the name is just a name |
| **电池电量模式** | 25 | Battery gauge (details below) |

The Slash bar on these laptops is **monochrome brightness**, not RGB, so every pattern uses intensity only.

### 电池电量模式 (battery gauge)

- 7 segments cover **30 %–100 %** (30–39 % → 1 segment, 40–49 % → 2, …, 90–100 % → 7);
- the bar fills from the physical bottom: the **highest lit segment descends as the battery drops**
  (index `7 - a`), stays solid and never blinks;
- with `a` = current band, the cells below that marker play one lap as
  `[1, none] → [2, 1] → … → [a-1, a-2] → [none, a-1]` (`[full, 50 % trail]`, positions counted from the bottom),
  **0.1 s per step**, then rests **0.5 s** with only the marker before the next lap;
- while charging the full segment climbs bottom → top; on battery the whole lap runs in reverse.

## How it works

The Slash bar is a plain USB HID feature-report device (`VID 0x0B05`, `PID 0x19B6`, collection `col06`,
feature report `0x5D`, 128-byte reports, 7 individually addressable segments on GA403-class machines).
G-Helper already streams per-segment frames for its Audio visualizer; this fork reuses the same
`SlashDevice.SetCustom` / `ContinueCustom` primitives and adds:

- `app/AnimeMatrix/SlashDevice.cs` — `SlashMode.Stream01` (22), `Stream02` (23), `Fish01` (24),
  `BatteryShow` (25); dropdown names; `LedCount` property.
- `app/AniMatrixControl.cs` — host-driven timer, chaser builder, procedural binary show
  (`BuildCreativeFrame`) and battery gauge (`BuildBatteryFrame`).

## Installation

### Option A — prebuilt

1. Quit G-Helper from the tray.
2. Back up `C:\GHelper\GHelper.exe` (e.g. rename to `GHelper.exe.bak`).
3. Replace it with a `GHelper.exe` built from this branch.
4. Start G-Helper and pick one of the new patterns in the animations dropdown.

The release is a framework-dependent single-file build, so **.NET 10 Desktop Runtime (x64)** is required.
The binary is unsigned, so SmartScreen may warn. G-Helper is portable; config lives in `%APPDATA%\GHelper\`.

### Option B — build from source

Requirements: **.NET 10 SDK** (same as upstream CI).

```powershell
dotnet publish app/GHelper.sln -c Release -r win-x64 -p:PublishSingleFile=true --no-self-contained
# output: app/bin/x64/Release/net10.0-windows/win-x64/publish/GHelper.exe
```

## Compatibility / testing

- Tested on **ROG Zephyrus G14 GA403WR** (Windows 11, G-Helper 0.288): Slash controller `PID 0x19B6`,
  `col06`, report `0x5D`, 7 segments.
- Other machines with the same 7-segment Slash controller should work. Longer 35-segment bars use the
  same code path (`LedCount`) but were not tested here.

## Power notes

- The LED bar itself is the main draw; the host stream is cheap. Measured on a 24-thread machine
  (30 s samples): firmware presets ≈ **0.000 CPU s**, the host-driven stream ≈ **0.17–0.20 CPU s
  (≈0.6 % of one core, ≈0.03 % of the whole machine)**.
- Frames are written through an allocation-free path (reused 128-byte feature report and frame
  buffer) instead of allocating per frame.
- The stream respects G-Helper's gating: with `matrix_auto` on battery and `matrix_lid` on lid close
  the bar is blanked once and frame writes stop; they resume automatically when gating clears.
- Identical consecutive frames are skipped (e.g. the 0.5 s battery rest), and the battery gauge timer
  runs at 100 ms (still 0.1 s per step, 0.5 s rest), halving wake-ups.
- Optional knobs (in `%APPDATA%\GHelper\config.json`):
  `"slash_stream_battery_dim": 60` (0–100, default 100) dims frames while on battery;
  `"slash_stream_fish_ms": 100` (40–200, default 60) lowers the light-show frame rate to save more.
- For all-day use, firmware presets (EC-driven) or a lower `matrix_brightness` (0–3) save the most LED power.

## Notes / limitations

- Host-driven frames stop during sleep/lid-close (only firmware presets run there); G-Helper re-enters
  the selected pattern after resume.
- Do not let G-Helper and Armoury Crate drive the Slash bar at the same time (last writer wins).
- G-Helper can auto-update; updating replaces the patched exe. To keep the patch, add `"skip_updates": 1`
  to `%APPDATA%\GHelper\config.json` or rebuild after each upstream update.

## License and attribution

G-Helper is licensed under the **GNU GPLv3**; this fork is a derivative and stays under GPLv3
(see `LICENSE`). Copyright for the upstream code belongs to the G-Helper author (seerge) and contributors.
This is an unofficial fork, not affiliated with or endorsed by ASUS or the G-Helper maintainer.

## Changelog

- **v0.288-r1** — add 流水灯测试01 (0.25 s), 流水灯02 (0.15 s), 蓝色大肥鱼01 (binary show),
  电池电量模式 (30–100 % gauge with 50 % trail, 0.1 s steps, 0.5 s rest, AC-dependent direction).
- **v0.288-r2** — respect battery/lid gating, skip identical frames, 100 ms battery timer,
  optional `slash_stream_battery_dim` power saver.
