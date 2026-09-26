# esp-cdc-keyboard

ESP32 USB HID+CDC implementation, it is a composite device that supports HID and CDC on a single USB port.

It recieve messages from the CDC interface, then send back HID reports from the HID interfaces.

The commonly used to simulate keyboard and mouse the hardware way.

## Usage

### Hardware Required

An ESP32-S3 development board that have USB-OTG supported.

The board has two USB ports, one is USB-OTG, the other is USB-UART(for debugging).
Connect both of them to your computer.

### Build and Flash

On Windows, configure the user-level ESP-IDF environment once. This uses the
global Python 3.12 installation and deliberately does not use ESP-IDF's
`python_env` virtual environment:

```powershell
.\setup-global-env.ps1
```

Open a new terminal after the setup. Build the project with either script:

```powershell
.\build.ps1
# or
.\build.bat
```

Build, flash, and monitor with a specific port:

```bash
idf.py -p PORT flash monitor
```

The equivalent project-local commands are:

```powershell
.\build.ps1 -Action flash -Port COM7
.\build.ps1 -Action monitor -Port COM7
```

To build, flash, and start the monitor in one command:

```powershell
.\build.ps1 -Port COM21 -Monitor
```

### Host HID test

The CDC virtual port is used to send HID reports to the firmware. Install
`pyserial` into the global Python installation if needed:

```powershell
python -m pip install pyserial
```

The current device was detected as `COM3` (`VID_303A&PID_4009`):

```powershell
python .\host_test.py --port COM3 mouse --x 20 --y 30
python .\host_test.py --port COM3 key --text "Hello"
python .\host_test.py --port COM3 demo
python .\host_test.py --port COM3 figure8
```

The mouse movement is relative. The test sends report ID `0x01` for keyboard
and report ID `0x02` for mouse, matching `main.c` and `usb_descriptors.c`.
The `figure8` command sends movement only with `buttons=0`; it does not click.

### Take Control from Host

python example:

```python
def key_report(modifiers: int, keys: List[int]) -> bytes:
    keys += [0] * (6 - len(keys))
    return bytes(
        [0x01, modifiers, 0x00, keys[0], keys[1], keys[2], keys[3], keys[4], keys[5]]
    )

def mouse_report(buttons: int, x: int, y: int) -> bytes:
    return bytes([0x02, buttons, x, y, 0x00, 0x00])

def main():
    ser = serial.serial_for_url("alt://COM1")
    # move mouse relatively to x=20, y=30
    report = mouse_report(0, 20, 30)
    ser.write(report)
```

