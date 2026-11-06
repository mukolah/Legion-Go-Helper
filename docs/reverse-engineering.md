# How the interfaces were found

This is the method used to work out how Legion Space controls the Legion Go S. It is meant to help anyone who wants
to add other Legion devices or more features. All analysis was static, plus read-only probing on the device. Every
write was tested with read-back, and the original value was restored afterwards.

## 1. Unpack Legion Space
The Legion Space 1.4.9.3 package (`LegionSpace -Package`, Inno Setup) contains:

1. An Inno Setup wrapper. Extract it with [innoextract](https://constexpr.org/innoextract/) to get `setup.exe`.
2. Inside `setup.exe`, a 7-Zip self-extracting archive. Extract it with `7z x setup.exe` to get about 2,100 files.

| Binary | Role |
|---|---|
| `LegionOSSetting.dll` (native C++) | All firmware access: the `WinSdk` class and `LN*` exports that wrap WMI |
| `LSDaemon.exe` | SYSTEM service; the UI talks to it over IPC |
| `HuaqinUsb\LEGOQHWHandle.dll` | Legion Go S controller HID protocol (146 exports) |
| `SapientiaUsb\*.dll` | Original Legion Go controller (17EF:61xx) |
| `HTML\js\*.js` | The UI (Vue). Contains the button-id tables (`LRH_*`) and the Windows VK key-code tables |
| `libcef.dll` | Chromium Embedded Framework, about 250 MB |

## 2. Firmware (WMI)
- **Feature IDs:** the export names in `LegionOSSetting.dll` describe themselves (`CustomTDPSet`, `SetFanTableEx`,
  `GetPowerChargeLimit`, …). Disassembling each export (Python with `pefile` and `capstone`) and following calls
  two levels deep shows the constant passed to `CWmiLenonoOther::SetFeatureValue`. That constant is the feature ID,
  for example `mov edx, 0x102FF00`.
- **Fan table:** `SetFanTableEx` builds its 64-byte buffer from a UTF-16 template string stored in the DLL, then
  patches fan values in at offset 6 and temperatures at offset 31.
- **Ranges:** min/max/default values come from `LENOVO_CAPABILITY_DATA_01`, which you can read live from WMI.
  Values differ between AC and battery.
- **Verification:** every ID was read on the device with `Get-CimInstance` / `Invoke-CimMethod`, then written,
  read back and restored. The charge limit's getter always returns 0, so it was verified by watching the battery
  stop charging at 80% and resume when the limit was turned off.
- **Charge targets:**
  - The neighbouring writable ID `0x03010002` was probed on AC with values 1 and 50 while monitoring
    `BatteryStatus.ChargeRate`. Charging didn't change, so it isn't a threshold.
  - Turning the 80% limit on above 80% stops charging and holds the level, which is how Legion Helper offers
    85-95% targets.

## 3. Controller (HID)
- **Finding the device:** `LEGOQHWHandle.dll` is the only binary that references VID `0x1A86` / PID `0xE310`.
  Its device-open routine filters HID interfaces by usage page `0xFFA0`, and its writer sends 65-byte reports.
- **Packet format:** every export builds a command object with fields `cmd` (+0x3C) and `sub` (+0x40). A serializer
  writes `[0x00, cmd, sub, payload…]`. A script that scans all exports for those two stores recovers the whole
  command table. Gyro commands go through a small builder: `cmd = 0x0D` to read and `0x0E` to write.
- **Payloads:** the lighting profile layout and the setting names were cross-checked against the Linux kernel
  driver `hid-lenovo-go-s.c`.
- **Button remapping:**
  - The `SetKeysMap` function parses JSON (`buttonCode`, `keyType`, `keyCode`) and emits a 7-byte entry,
    `[button, 0x80 | type, codes…]`.
  - The type is the index of the lookup table where the key was found: keyboard, mouse or media.
  - Those tables are filled at start-up by an initializer that stores packed (VK, HID usage) byte pairs.
  - The write has to be followed by a commit, `12 05`.
- **Testing:** a small console tool sent queries and printed replies. Reads came first. Every write was then
  checked by read-back and restored.

## 4. Per-LED lighting
- **Finding the mode:** Legion Space and the Linux driver only use RGB mode 1 ("custom"). Writing mode 0 made the
  controller re-enumerate, and Windows offered Dynamic Lighting. That showed mode 0 exposes a **HID LampArray** on
  `mi_04`.
- **Reading it:** its feature reports were parsed with `HidP_GetValueCaps`, `HidP_GetUsageValue` and
  `HidP_SetUsageValue`, giving 6 lamps.
- **Mapping it:** each lamp was lit alone in red, then all six in different colours, while the rings were filmed.
  That produced the clock-position map in `controller-protocol.md`.
- **Gotcha:** the RGB master switch (`04 06`) must be on, or the LampArray updates are accepted but nothing lights.

## 5. What did not come from Legion Space
- **Joystick mouse:** Legion Space doesn't offer it on the Go S. Legion Helper implements it with XInput.
- **Gyro mouse:** the firmware mode drifts because the IMU reports a large constant bias (about -9 / 7 / 11 °/s at
  rest). Legion Helper reads the controller IMU as a standard Windows gyrometer (`HID\VID_1A86&PID_E310&MI_05`)
  through the COM Sensor API. It calibrates the bias out and turns rotation rate into cursor motion.
- **F13-F24 mapping:** these keys aren't in Lenovo's table, but the controller accepts their HID usages
  (`0x68`-`0x73`).

## Safety notes
- Always read the current value first, write a value inside the reported range, read it back, and restore it.
- The firmware needs about 0.5-1 s after a write before a read reflects the new value.
- Only one controller command should be in flight at a time.
