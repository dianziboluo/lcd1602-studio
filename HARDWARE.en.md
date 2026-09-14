# Tested hardware & environment (English)

This document records the exact hardware and environment the author of this repository used.
Use it for **reproduction, wiring and troubleshooting**. Any difference (different board/LCD/bus
voltage) may change the results — please check this list before opening an issue.

---

## 1. Host platform (PC side)

| Item | Tested value |
|---|---|
| Form factor | Laptop |
| CPU | **AMD Ryzen 7 8845HS** (Zen4 / Hawk Point, 8 cores / 16 threads) |
| GPU | **AMD Radeon 780M** (integrated, same die as the CPU) |
| RAM | 2 × 12GB DDR5 **Crucial CT12G56C46S5.M4B1** (SPD Hub temperature readable) |
| Storage | **KIOXIA-EXCERIA SSD** ×2 (1TB [C:] + another [D:], SMART temps readable) |
| OS | Windows 11 (64-bit) |
| Sensor tools | LibreHardwareMonitorLib **0.9.6** (built-in), HWiNFO64 **8.52-6060** (optional, shared memory) |

> Verified on this platform: **CPU real temp (Tctl/Tdie), iGPU temp/load, memory/SPD temp, SSD temp** are readable;
> **fan speed has no sensor in either LHM or HWiNFO** (0 readings) — a hardware limit, see "Known pitfalls".

## 2. Dev board — 合宙 AirM2M CORE ESP32-C3

| Item | Tested value |
|---|---|
| Model | **CORE-ESP32-C3** (silk: CORE-ESP32-C3 / AirM2M.CORE) |
| Chip | Espressif **ESP32-C3** (QFN32, single-core RISC-V 160MHz, Wi-Fi + BT 5) |
| USB-UART | onboard **CH343** ("USB-Enhanced-SERIAL CH343", **COM4**, VID_1A86/PID_55D3) |
| Native USB | unused (the USB_DM/DP pins are routed to the CH343 solution) |
| Onboard LEDs | **D4 = GPIO12**, **D5 = GPIO13** (active-high) |
| I2C pins | **GPIO4 = I2C_SDA (pin 28)**, **GPIO5 = I2C_SCL (pin 27)** |
| Power | USB-C 5V input, onboard LDO → 3.3V |
| Flashing | must **hold the onboard BOOT button** while uploading (auto-download circuit is unreliable; failed multiple times) |

**Relevant pins (right side, 17–32):**

```
25 GND    26 3.3V    27 GPIO5 (I2C_SCL)    28 GPIO4 (I2C_SDA)
```

## 3. Display — LCD1602A (16×2 character LCD)

| Item | Tested value |
|---|---|
| Model | **LCD1602A** (actually assembled: yellow-green backlight / black text; 3.3V blue and 5V yellow variants also in the kit) |
| Controller | **HD44780-compatible** (ST7066U-class) |
| Spec | 16 columns × 2 rows, 5x7 dot matrix glyphs |
| Power | this project runs the whole module at **3.3V** (a 3.3V blue version / 5V yellow version also exist) |
| Character set | ❌ **no ℃ glyph in ROM** (has ° = 0xDF); non-ASCII cannot be displayed |
| Contrast | **blue potentiometer** on the adapter; check it first when the screen is lit but blank |

16 pins: VSS/VDD/VO(contrast)/RS/RW/E/D4~D7/A(backlight+)/K(backlight−).

Both wirings are supported by this project (**change one macro in the firmware; the
PC-side software needs no change at all**):
- **Option A (default)**: plugged into the PCF8574 adapter, only 4 wires → section 5
- **Option B**: no adapter, the LCD's 16 pins go straight to the dev board, 6 signal wires → section 6

## 4. I2C adapter — PCF8574 (for LCD1602/2004)

| Item | Tested value |
|---|---|
| Model | **PCF8574 I2C adapter** (55782 kit, supports 1602/2004) |
| Chip | **PCF8574** (I2C → parallel port expander) |
| Default address | **0x27** (usually 0x27 or 0x3F; the firmware auto-scans at boot) |
| Bit map | En=0x04, Rw=0x02, Rs=0x01, Backlight=0x08, D4~D7=0x10~0x80 |
| On-board controls | **blue potentiometer (contrast)**, backlight jumper (shorted by default) |
| Power | whole module at **3.3V** (both PCF8574 and LCD support it) |

> ⚠️ Do **not** wire 5V: ESP32-C3 GPIOs are not 5V-tolerant. 3.3V drive is the safe choice;
> if your LCD needs 5V for enough contrast, add a level shifter and separate power instead.

## 5. Wiring option A: I2C adapter (default)

```
PC (USB-C cable)
  │
  ├── USB power + CH343 serial (COM4, 115200) ── flashing & sync
  │
ESP32-C3                        PCF8574 adapter        LCD1602A
  ├── 3.3V  (pin 26) ───────────── VCC ─────────────── VDD
  ├── GND   (pin 25) ───────────── GND ─────────────── VSS/K
  ├── GPIO5 (pin 27, I2C_SCL) ────── SCL
  ├── GPIO4 (pin 28, I2C_SDA) ────── SDA
  └──                                     └── (header socket to the LCD's 16 pins)
```

Only 2 GPIOs, easiest wiring; contrast via the blue potentiometer on the adapter.
Keep the firmware default `#define LCD_DRIVER  LCD_DRIVER_I2C`.

## 6. Wiring option B: direct 4-bit parallel (no adapter)

**Feasible, and it needs no extra parts** apart from a 10k contrast potentiometer.
The cost is **6 GPIOs** (I2C needs only 2); the gain is one less module, no I2C
address/contact issues, and a faster bus.

```
        ESP32-C3                  LCD1602A (16 pins)
  ├── GND   (pin 25) ───────────── 1  VSS
  ├── 3.3V  (pin 26) ───────────── 2  VDD
  ├── 10k pot wiper ────────────── 3  V0     (pot ends to 3.3V / GND)
  ├── GPIO5 (pin 27) ───────────── 4  RS
  ├── GND ──────────────────────── 5  RW     (tied low = write-only, saves a wire)
  ├── GPIO4 (pin 28) ───────────── 6  E
  │                               7~10 D0~D3 (unused in 4-bit mode, leave open)
  ├── GPIO6  ───────────────────── 11 D4
  ├── GPIO7  ───────────────────── 12 D5
  ├── GPIO10 ───────────────────── 13 D6
  ├── GPIO3  ───────────────────── 14 D7
  ├── 3.3V ── 100~220Ω ─────────── 15 A      (backlight+; many boards have the resistor on-board)
  └── GND ──────────────────────── 16 K      (backlight−)
```

Pin choice: avoid **GPIO2/8/9** (strapping pins + UART0) and **GPIO18/19** (USB).
GPIO4/5 are exactly the pins the I2C wiring used, so you can reuse the same wires.
To change pins, edit `PIN_RS/PIN_EN/PIN_D4~PIN_D7` at the top of `firmware/LcdParallel.h`.

Firmware switch — one line in `lcd_monitor.ino`:

```cpp
#define LCD_DRIVER  LCD_DRIVER_PARALLEL   // direct wiring, no adapter
```

**The PC side (LCD1602 Studio) does not change at all**: the serial protocol
(`L0/L1/CG`) is identical, and "sync to LCD / flash firmware" keep working.

The parallel driver is **self-contained** (`firmware/LcdParallel.h`, ~100 lines) and
**needs no LiquidCrystal library** — the esp32 core does not bundle one either, so
this project implements it directly to stay dependency-free.

### About 5V displays (important)

This project powers everything from **3.3V**. A 5V-spec 1602 will almost always
display fine at 3.3V (the yellow-green screen measured here is exactly that kind,
and it runs at 3.3V through the adapter too); you only need to adjust contrast with
the 10k pot, and the backlight is slightly dimmer (use a smaller series resistor).

If you insist on feeding the LCD's VDD from **5V**, all 6 signal lines **must** go
through a level shifter: HD44780 VIH = 0.7×VDD = 3.5V while the ESP32-C3 GPIO only
outputs 3.3V — out of spec, and 5V must never reach the GPIO. One **74HCT245** (or an
8-channel **TXS0108E** module) is enough; only the 3.3V→5V direction is needed
since RW is tied to GND.

## 7. Software toolchain (tested versions)

| Tool | Version | Purpose |
|---|---|---|
| Arduino IDE | 2.x | firmware editing/upload (Tools: **esp32 → AirM2M CORE ESP32C3**, **USB CDC On Boot = Enabled**) |
| esp32 core | **3.3.11** | ESP32-C3 board support |
| LiquidCrystal_I2C | YwRobot 2011-05-17 modified | 1602 I2C driver (shipped with the adapter) |
| LibreHardwareMonitorLib | **0.9.6** | PC-side sensors (MPL-2.0, auto-downloaded at build) |
| HWiNFO64 | **8.52-6060** (optional) | detailed sensors (shared memory; must be installed & enabled manually) |
| csc (.NET Framework 4.8) | ships with Windows | compiles the GUI/host (zero install) |

## 8. Known pitfalls (all discovered in real use)

1. **Upload fails with "No serial data received"** → the chip is not in download mode:
   hold BOOT, tap RST.
2. **LiquidCrystal_I2C `init()` resets the I2C pins back to defaults (8/9)** →
   the firmware must call `Wire.begin(4,5)` and then **use `begin()` directly**, never `init()`.
3. **AMD mobile CPU Tctl reads 0 in LHM forever** (known open-source issue) → use the HWiNFO
   backend for `{temp}`; until then the SoC iGPU temp (`{soc}`) is shown instead.
4. **Fan speed is unreadable by any path on this machine** (0 fan entries among 282 HWiNFO
   readings) → `{fan}` / `FAN ----` is a hardware limitation.
5. **HD44780 CGRAM bit order: MSB (bit4) = leftmost column** → if a custom glyph looks mirrored,
   reverse 5 bits before sending (the firmware `bitRev5()` already does this — do not reverse again on the host).
6. **1602 has no ℃** → custom glyph in CGRAM slot 1 (same bitmap on simulator and real LCD).
7. **USB cable must support data** — some charge-only USB-C cables don't expose the CH343 port.
8. **5V-spec LCD + 3.3V logic** → powering everything from 3.3V works in practice (measured);
   if you move the LCD's VDD to 5V, the signal lines must go through a 74HCT245/TXS0108E
   level shifter (VIH = 0.7×VDD = 3.5V > 3.3V).
9. **Direct parallel wiring: lit screen, single row of blocks** → contrast pot not adjusted,
   or RS/E mis-wired; if the screen is completely blank with pin 3 floating, set the pot to
   mid-position first, then fine-tune.
10. **Defining a `class` inside the `.ino` breaks the build** (Arduino generates function
    prototypes) → keep the driver class in a separate header (`firmware/LcdParallel.h`);
    headers are not preprocessed for prototypes.
