"""Host-side test tool for the ESP32 TinyUSB HID+CDC firmware.

The CDC stream carries one complete HID report per write:
  0x01 + keyboard report (modifiers, reserved, six key usages)
  0x02 + mouse report (buttons, x, y, wheel, pan)
"""

from __future__ import annotations

import argparse
import math
import string
import time
from typing import Iterable

import serial


KEYBOARD_REPORT_ID = 0x01
MOUSE_REPORT_ID = 0x02
SHIFT = 0x02


def key_report(modifiers: int, keys: Iterable[int]) -> bytes:
    """Build the 9-byte keyboard packet expected by main.c."""
    key_list = list(keys)
    if len(key_list) > 6:
        raise ValueError("A boot keyboard report supports at most 6 keys")
    key_list += [0] * (6 - len(key_list))
    return bytes([KEYBOARD_REPORT_ID, modifiers & 0xFF, 0x00, *key_list])


def mouse_report(buttons: int, x: int, y: int, wheel: int = 0, pan: int = 0) -> bytes:
    """Build the 6-byte mouse packet expected by usb_descriptors.c."""
    for name, value in (("x", x), ("y", y), ("wheel", wheel), ("pan", pan)):
        if not -128 <= value <= 127:
            raise ValueError(f"{name} must be between -128 and 127")
    return bytes(
        [
            MOUSE_REPORT_ID,
            buttons & 0xFF,
            x & 0xFF,
            y & 0xFF,
            wheel & 0xFF,
            pan & 0xFF,
        ]
    )


LETTER_USAGE = {letter: 0x04 + index for index, letter in enumerate(string.ascii_lowercase)}
DIGIT_USAGE = {
    "1": 0x1E,
    "2": 0x1F,
    "3": 0x20,
    "4": 0x21,
    "5": 0x22,
    "6": 0x23,
    "7": 0x24,
    "8": 0x25,
    "9": 0x26,
    "0": 0x27,
}
SPECIAL_USAGE = {
    " ": 0x2C,
    "\n": 0x28,
    "\t": 0x2B,
    "-": 0x2D,
    "=": 0x2E,
    "[": 0x2F,
    "]": 0x30,
    "\\": 0x31,
    ";": 0x33,
    "'": 0x34,
    "`": 0x35,
    ",": 0x36,
    ".": 0x37,
    "/": 0x38,
}


def char_usage(char: str) -> tuple[int, int]:
    """Return (modifier, usage) for a printable ASCII character."""
    if len(char) != 1:
        raise ValueError(f"Expected one character, got {char!r}")
    if char in LETTER_USAGE:
        return 0, LETTER_USAGE[char]
    if char.isalpha() and char.lower() in LETTER_USAGE:
        return SHIFT, LETTER_USAGE[char.lower()]
    if char in DIGIT_USAGE:
        return 0, DIGIT_USAGE[char]
    if char in SPECIAL_USAGE:
        return 0, SPECIAL_USAGE[char]
    shifted_symbols = {
        ")": (SHIFT, 0x27),
        "!": (SHIFT, 0x1E),
        "@": (SHIFT, 0x1F),
        "#": (SHIFT, 0x20),
        "$": (SHIFT, 0x21),
        "%": (SHIFT, 0x22),
        "^": (SHIFT, 0x23),
        "&": (SHIFT, 0x24),
        "*": (SHIFT, 0x25),
        "(": (SHIFT, 0x26),
        "_": (SHIFT, 0x2D),
        "+": (SHIFT, 0x2E),
        "{": (SHIFT, 0x2F),
        "}": (SHIFT, 0x30),
        "|": (SHIFT, 0x31),
        ":": (SHIFT, 0x33),
        '"': (SHIFT, 0x34),
        "~": (SHIFT, 0x35),
        "<": (SHIFT, 0x36),
        ">": (SHIFT, 0x37),
        "?": (SHIFT, 0x38),
    }
    if char in shifted_symbols:
        return shifted_symbols[char]
    raise ValueError(f"Unsupported character: {char!r}")


def send_report(ser: serial.Serial, report: bytes) -> None:
    ser.write(report)
    ser.flush()
    print(f"TX: {report.hex(' ')}")


def send_text(ser: serial.Serial, text: str, interval: float) -> None:
    for char in text:
        modifiers, usage = char_usage(char)
        send_report(ser, key_report(modifiers, [usage]))
        time.sleep(interval)
        send_report(ser, key_report(0, []))
        time.sleep(interval)


def send_figure_eight(ser: serial.Serial, size: int, steps: int, interval: float) -> None:
    """Move the cursor through one horizontal figure-eight without pressing."""
    if not 1 <= size <= 127:
        raise ValueError("size must be between 1 and 127")
    if steps < 8:
        raise ValueError("steps must be at least 8")

    previous_x = 0
    previous_y = 0
    for index in range(1, steps + 1):
        angle = 2.0 * math.pi * index / steps
        # x=sin(t), y=sin(t)*cos(t) produces two connected lobes.
        target_x = round(size * math.sin(angle))
        target_y = round(size * 0.55 * math.sin(angle) * math.cos(angle))
        delta_x = target_x - previous_x
        delta_y = target_y - previous_y
        if delta_x or delta_y:
            # buttons=0 is intentional: this is movement only, never a click.
            send_report(ser, mouse_report(0, delta_x, delta_y))
            time.sleep(interval)
        previous_x = target_x
        previous_y = target_y


def parse_int(value: str) -> int:
    return int(value, 0)


def main() -> int:
    parser = argparse.ArgumentParser(description="Test ESP32 USB HID through its CDC port")
    parser.add_argument("--port", required=True, help="CDC COM port, for example COM3")
    parser.add_argument("--baudrate", type=int, default=115200)
    subparsers = parser.add_subparsers(dest="command", required=True)

    mouse_parser = subparsers.add_parser("mouse", help="move the mouse relatively")
    mouse_parser.add_argument("--x", type=int, required=True)
    mouse_parser.add_argument("--y", type=int, required=True)
    mouse_parser.add_argument("--buttons", type=parse_int, default=0)
    mouse_parser.add_argument("--wheel", type=int, default=0)
    mouse_parser.add_argument("--pan", type=int, default=0)

    key_parser = subparsers.add_parser("key", help="send keyboard text or raw usages")
    key_parser.add_argument("--text", help="ASCII text to type")
    key_parser.add_argument("--modifiers", type=parse_int, default=0)
    key_parser.add_argument("--keys", type=parse_int, nargs="*", default=[])
    key_parser.add_argument("--interval", type=float, default=0.03)

    demo_parser = subparsers.add_parser("demo", help="move the mouse and type a short test")
    demo_parser.add_argument("--interval", type=float, default=0.15)

    figure_parser = subparsers.add_parser(
        "figure8", help="move the cursor in a figure-eight without clicking"
    )
    figure_parser.add_argument("--size", type=int, default=100, help="horizontal radius in pixels")
    figure_parser.add_argument("--steps", type=int, default=120)
    figure_parser.add_argument("--interval", type=float, default=0.01)

    args = parser.parse_args()

    try:
        with serial.Serial(
            port=args.port,
            baudrate=args.baudrate,
            timeout=1,
            write_timeout=1,
        ) as ser:
            print(f"Opened {args.port} at {args.baudrate} baud")
            if args.command == "mouse":
                send_report(ser, mouse_report(args.buttons, args.x, args.y, args.wheel, args.pan))
            elif args.command == "key":
                if args.text is not None:
                    send_text(ser, args.text, args.interval)
                elif args.keys:
                    send_report(ser, key_report(args.modifiers, args.keys))
                    time.sleep(args.interval)
                    send_report(ser, key_report(0, []))
                else:
                    key_parser.error("use --text or --keys")
            elif args.command == "demo":
                send_report(ser, mouse_report(0, 20, 30))
                time.sleep(args.interval)
                send_report(ser, mouse_report(0, -20, -30))
                time.sleep(args.interval)
                send_text(ser, "HID", args.interval)
            elif args.command == "figure8":
                send_figure_eight(ser, args.size, args.steps, args.interval)
    except (serial.SerialException, ValueError) as exc:
        parser.error(str(exc))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
