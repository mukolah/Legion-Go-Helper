# Legion Go S - hardware control interface

Device: **LENOVO 83L3, Legion Go S 8ARP1, AMD Ryzen Z2 Go**. Source: Legion Space 1.4.9.3
(`wwls0d1ewyx3aff0.exe` → Inno Setup → inner `setup.exe` 7z payload → `LegionOSSetting.dll`).

Status legend: **[verified]** write/read-back tested on the device · **[RE]** taken from Legion Space disassembly,
read-only tested · **[unknown]** not resolved yet.

## Where the logic lives in Legion Space

| Binary | Role |
|---|---|
| `LegionOSSetting.dll` (native C++) | All hardware access. Exports a `WinSdk` class plus `LN*` C exports wrapping WMI (`CWmiGameZone`, `CWmiLenonoOther`, fan methods) |
| `LSDaemon.exe` | SYSTEM service; the UI calls it over IPC (`setPowerChargeLimit`, …) |
| `LegionSpace.exe` + `libcef.dll` (~250 MB Chromium) | UI, store, and launcher. This is where the RAM goes |
| `SapientiaUsb/`, `HuaqinUsb/`, `LegionHandle.dll` | Controller (gamepad) HID: deadzones, stick curves, firmware updates |
| `LegionSettingMenu.dll` (.NET) | Quick-settings overlay |

## WMI (`root\WMI`)

### Modes - `LENOVO_GAMEZONE_DATA` [verified]
`GetSmartFanMode()` / `SetSmartFanMode(Data)`: `1` Quiet, `2` Balanced, `3` Performance, `255` Custom.
Legion Space's `ThermalModeSet` calls `SetSmartFanMode` directly. Mode changes fire
`LENOVO_GAMEZONE_SMART_FAN_MODE_EVENT`.

### Features - `LENOVO_OTHER_METHOD.GetFeatureValue(IDs)` / `SetFeatureValue(IDs, value)`
ID layout `0xDD FF MM TT` (device, feature, mode, type).

| ID | Legion Space name | Meaning | Status |
|---|---|---|---|
| `0x0102FF00` | `CustomTDP` | SPL (sustained), W | [verified] |
| `0x0101FF00` | `CustomSPPT` | SPPT (slow boost), W | [verified] read/apply |
| `0x0103FF00` | `CustomFPPT` | FPPT (fast boost), W | [verified] read/apply |
| `0x010201/02/0300` | `GetThermalModeList` | Per-mode SPL readout (Quiet/Balanced/Performance) | [RE] |
| `0x03010001` | `PowerChargeLimit` | Battery charge limit (80%) on/off | [verified] by behaviour: charging stops at 80% within ~15 s and the level holds (no discharge to 80); **the getter always returns 0** |
| - | - | **Charge targets above 80%** | [verified] Turning the 80% limit on while the battery is above 80% stops charging and **holds** the level; it does not discharge to 80%. Observed: 85.2% on AC, limit on, charge rate 0 mW. Legion Helper uses this to emulate 85/90/95% targets, and the user confirmed charging stops at the target |
| `0x03010002` | - | Unknown (capability 7, reads 0) | Tested on AC at 57%: writing `1` or `50` has no effect on charging and reads back 0. It is **not** a charge threshold |
| `0x04020000` | `FullSpeedFan` | Fans at 100% on/off | [RE] |
| `0x04030001` | `GetFanRPM` | Fan RPM | [RE] reads e.g. 1894 |
| `0x05040000` | `GetCPUTemperature` | CPU temp °C | [RE] reads e.g. 46 |
| `0x05010000` | `GetCPUVRTemperature` | CPU VR temp °C | [RE] |

Ranges (min/max/default/step) come from `LENOVO_CAPABILITY_DATA_01`, keyed by the same IDs. **The firmware
reports different custom-mode ranges depending on the power source**, so re-read them when it changes:

| Custom limit | On AC (observed) | On battery (observed) |
|---|---|---|
| SPL | 5-40 W | 5-33 W |
| SPPT | 5-43 W | 5-33 W |
| FPPT | 5-53 W | 5-35 W |

Write → read-back needs a short delay (~0.5-1 s); an immediate read can return the old value.
`GetCPUTemp`/`GetGPUTemp` on `LENOVO_GAMEZONE_DATA` return 0 on this unit. Use `0x05040000` instead.

### Fan curve - `LENOVO_FAN_METHOD` [verified]
- `Fan_Get_Table(FanID=0, SensorID=0)` → `FanTable[10]` (duty %), `SensorTable[10]` (°C: 10,20,…,100).
- **There is a single fan table shared by every mode.**
  - The last table written drives Quiet, Balanced and Performance too, not only Custom. This was verified by writing
    a 100% table while in Quiet (the fan ramped to ~5000 RPM), and by a "fan off below 50 °C" table that kept the
    fan at 0 RPM in all four modes.
  - The factory table on the Go S is `25,27,32,36,41,45,51,55,59,64` % at 10…100 °C.
  - Legion Helper therefore writes the user's curve only for Custom (unless "Use in all modes" is on), and puts the
    factory table back when entering a preset.
- Below about 60 °C the curve seems to be applied as steps rather than interpolated, and a running fan only stops
  after some hysteresis.
- `Fan_Set_Table(byte[64])`. Layout from Legion Space's `WinSdk::SetFanTableEx` template
  `"255,1,10,0,0,0, <fan u16 x10>, 1,10,0,0,0, <temp u16 x10>"`:

| Offset | Value |
|---|---|
| 0 | `0xFF` |
| 1 | `1` |
| 2-5 | `10` (u32 point count) |
| 6-25 | fan duty % × 10 (u16 LE) |
| 26 | `1` |
| 27-30 | `10` (u32 point count) |
| 31-50 | temperature °C × 10 (u16 LE) |
| 51-63 | 0 |

Verified by changing one point (+5%), reading it back, and restoring it.

## Still to investigate
1. The controller is documented separately in [controller-protocol.md](controller-protocol.md).
2. Features `0x0104xxxx` (range 14-160) and `0x0106xxxx` (range 2-30) in `LENOVO_CAPABILITY_DATA_01`.
