#!/usr/bin/env python3
"""Simulated IS4420-M1 UART output, for testing host programs without a module.

Sends the IS4420-M1 line about every 103 ms, 115200 8N1, formatted as the IS4420-M1
datasheet defines it (37 characters + CR LF, with the IS4420 temperature), or the
earlier line without the temperature with --legacy (29 characters + CR LF).

Linux:    python3 fake_is4420.py --pty /dev/ttyUSB90 [scenario]
          (creates a pseudo-terminal and a symbolic link to it)
Windows:  python fake_is4420.py --port COM11 [scenario]
          (needs pyserial and a virtual null-modem pair such as com0com;
           the program under test opens the other port of the pair)

Scenarios:
  (default)            demo cycle: ramp 4-20 mA, over-range, no measurement,
                       below 4 mA, open loop, sine, steps
  --ma 12.34           constant current with a little noise
  --script SPEC        steps "value:seconds,...", repeated; value is a current in mA,
                       over, error, silence or ramp (4 to 20 mA over the step);
                       "value@T:seconds" also sets the temperature to T (C) for the step
  --temp T             IS4420 temperature in C (default: 27 C plus self-heating);
                       "err" sends +ERR
  --legacy             the 29-character line without the temperature (earlier firmware)
  --corrupt 0.02       fraction of lines sent with one character missing
  --stray              send one 0x00 byte before the first line (as at power-up)
  --old                the 41-byte line with START_ and _END (early firmware)
  --garbage            random bytes instead of lines (a device that is not an IS4420)
  --silent             open the port and send nothing
"""
import argparse
import math
import os
import random
import signal
import sys
import time

PERIOD = 0.103          # s between lines (one 100 ms measurement plus the line)
OVER_RANGE_MA = 21.66   # where EOVER starts with VDD = 3.3 V (simulation value)


def temp_field(temp):
    """Sign and 3 digits, as MODULE_TEMP: rounded half away from zero, limited to +-125,
    never -000; +ERR without a measurement (temp None)."""
    if temp is None:
        return "+ERR"
    t = int(math.floor(abs(temp) + 0.5))
    t = min(t, 125)
    sign = "-" if (temp < 0 and t != 0) else "+"
    return "%s%03d" % (sign, t)


def make_line(na, kind="value", temp=25.0, model="IS4420M1", rev=0, old=False, legacy=False):
    if kind == "error":
        body = "ERRORmA_+ERROR%"
        temp = None                     # the firmware sends +ERR with every ERROR line
    elif kind == "over":
        body = "EOVERmA_+EOVER%"        # the temperature is still sent
    else:
        na = max(0, int(round(na)))
        ma100 = min((na + 5000) // 10000, 9999)
        d = na - 4000000
        p10 = min((abs(d) + 8000) // 16000, 1279)
        sign = "-" if (d < 0 and p10 != 0) else "+"
        body = "%02d.%02dmA_%s%03d.%d%%" % (ma100 // 100, ma100 % 100, sign, p10 // 10, p10 % 10)
    if not (legacy or old):
        body += "_" + temp_field(temp) + "deg"
    text = body + "_%s_ver%d" % (model, rev)
    if old:
        text = "START_" + text + "_END"
    return (text + "\r\n").encode("ascii")


def demo(t):
    """(kind, mA) at time t of a 36 s cycle."""
    t = t % 36.0
    if t < 8:
        return "value", 4 + 16 * t / 8
    if t < 11:
        ma = 20 + 2.5 * (t - 8) / 3
        return ("over" if ma >= OVER_RANGE_MA else "value"), ma
    if t < 13:
        return "error", 0
    if t < 16:
        return "value", 3.0
    if t < 18:
        return "value", 0.07
    if t < 28:
        return "value", 12 + 6 * math.sin(2 * math.pi * (t - 18) / 5)
    return "value", [4, 8, 12, 16, 20][min(4, int((t - 28) / 1.6))]


def parse_script(spec):
    steps = []
    for item in spec.split(","):
        value, seconds = item.split(":")
        value = value.strip().lower()
        temp = None
        if "@" in value:
            value, t = value.split("@")
            temp = t
        steps.append((value, temp, float(seconds)))
    return steps


def scripted(steps, t):
    """(kind, mA, temperature or None to keep the default) at time t."""
    total = sum(s for _, _, s in steps)
    t = t % total
    for value, temp, seconds in steps:
        if t < seconds:
            if value in ("over", "error", "silence"):
                return value, 0, temp
            if value == "ramp":
                return "value", 4 + 16 * t / seconds, temp
            return "value", float(value), temp
        t -= seconds
    return "silence", 0, None


def parse_temp(text):
    """A temperature in C, or None for 'err'."""
    return None if text.strip().lower() == "err" else float(text)


class PtyOut:
    def __init__(self, link):
        import tty
        self.master, self.slave = os.openpty()
        tty.setraw(self.slave)          # CR and LF must pass through untouched
        self.link = link
        if os.path.lexists(link):
            os.remove(link)
        os.symlink(os.ttyname(self.slave), link)
        os.set_blocking(self.master, False)

    def write(self, data):
        try:
            os.write(self.master, data)
        except (BlockingIOError, OSError):
            pass

    def drain(self):
        # Anything the program under test writes would land here; this port only listens.
        try:
            os.read(self.master, 4096)
        except (BlockingIOError, OSError):
            pass

    def close(self):
        try:
            os.remove(self.link)
        except OSError:
            pass
        os.close(self.master)
        os.close(self.slave)


class SerialOut:
    def __init__(self, port):
        import serial
        self.s = serial.Serial(port, 115200, write_timeout=0)

    def write(self, data):
        try:
            self.s.write(data)
        except Exception:
            pass

    def drain(self):
        pass

    def close(self):
        self.s.close()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    where = ap.add_mutually_exclusive_group(required=True)
    where.add_argument("--pty", help="Linux: create a pseudo-terminal linked at this path")
    where.add_argument("--port", help="serial port to write to (pyserial)")
    ap.add_argument("--ma", type=float, help="constant current in mA")
    ap.add_argument("--script", help="steps value:seconds,...")
    ap.add_argument("--corrupt", type=float, default=0.0, help="fraction of corrupted lines")
    ap.add_argument("--stray", action="store_true", help="0x00 byte before the first line")
    ap.add_argument("--old", action="store_true", help="41-byte line with START_ and _END")
    ap.add_argument("--legacy", action="store_true", help="29-character line without the temperature")
    ap.add_argument("--temp", help="IS4420 temperature in C, or err")
    ap.add_argument("--garbage", action="store_true", help="random bytes instead of lines")
    ap.add_argument("--silent", action="store_true", help="send nothing")
    ap.add_argument("--period", type=float, default=PERIOD, help="seconds between lines")
    ap.add_argument("--rev", type=int, default=0, help="MODULE_REV digit after 'ver'")
    args = ap.parse_args()

    out = PtyOut(args.pty) if args.pty else SerialOut(args.port)

    def stop(*_):
        out.close()
        sys.exit(0)

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    steps = parse_script(args.script) if args.script else None
    rnd = random.Random(1)
    start = time.monotonic()
    k = 0
    if args.stray:
        out.write(b"\x00")
    while True:
        k += 1
        target = start + k * args.period
        delay = target - time.monotonic()
        if delay > 0:
            time.sleep(delay)
        out.drain()
        if args.silent:
            continue
        if args.garbage:
            out.write(bytes(rnd.randrange(256) for _ in range(rnd.randrange(10, 40))))
            continue
        t = time.monotonic() - start
        step_temp = None
        if args.ma is not None:
            kind, ma = "value", args.ma
        elif steps:
            kind, ma, step_temp = scripted(steps, t)
        else:
            kind, ma = demo(t)
        if kind == "silence":
            continue
        if step_temp is not None:
            temp = parse_temp(step_temp)
        elif args.temp is not None:
            temp = parse_temp(args.temp)
        else:
            # IS4420 temperature: 27 C plus the module's own heating, which grows with the
            # loop current, and a little noise.
            temp = 27.0 + 0.25 * max(0.0, min(ma, 22.0)) + rnd.uniform(-0.6, 0.6)
        na = ma * 1e6 + rnd.uniform(-1500, 1500)
        data = make_line(na, kind, temp=temp, rev=args.rev, old=args.old, legacy=args.legacy)
        if args.corrupt and rnd.random() < args.corrupt:
            cut = rnd.randrange(len(data) - 2)
            data = data[:cut] + data[cut + 1:]
        out.write(data)


if __name__ == "__main__":
    main()
