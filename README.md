*English · [ภาษาไทย](README.th.md)*

# Serial Loopback Pro

### ▶ [Walk through the UI in your browser](https://olay097056.github.io/serial-loopback/)

**A simulation, not a measurement.** A browser cannot open a serial port, so the
numbers are written in advance per scenario — healthy port, degrading above
115200, missing plug, dead port. What it faithfully shows is the sequence and
the states, including the loopback probe that runs before any sweep.

---

A serial port diagnostic tool that sweeps a COM port across 11 baud rates with a
loopback plug attached, and tells you which rates the port can actually carry —
continuously, with a live pass/warn/fail table.

Shipped in two implementations of the same tool, for two different worlds.

## Two implementations, and why

| | `serial_loopback_pro.py` | `SerialLoopbackPro.cs` |
|---|---|---|
| Runtime | Python 3 + customtkinter + matplotlib | .NET Framework 3.5, x86 |
| Runs on | A modern workstation | **Windows XP SP2 and up, with nothing installed** |
| Lines | 574 | 1,165 |

The Python version is the comfortable one — live matplotlib graphs, modern
widgets, quick to change.

The C# version exists because the machines that actually need serial diagnostics
are often the oldest machines in the building: industrial PCs and instrument
controllers running Windows XP, where you cannot install a Python runtime and
would not be allowed to if you could. So it targets .NET 3.5 / x86 and is
written under that constraint throughout — **no default parameters, no
`volatile double`, no string interpolation**, since none of those exist in that
compiler. It renders its own table and gauge by hand with GDI+ rather than
depending on any modern control, and falls back from Segoe UI to Tahoma and from
Consolas to Courier New when running on an XP SP2 box that has neither.

It compiles to a single ~40 KB executable with no installer and no dependencies.
Copy it onto the machine, run it, delete it.

## What it does

- **Enumerates COM ports via WMI**, so ports appear with their real device
  description rather than a bare `COM3`
- **Probes for the loopback plug first** — writes `A5 5A F0 0F` at 9600 and
  requires at least 3 of 4 bytes to echo. Without this, a missing jumper looks
  identical to a dead port, and the whole sweep reports garbage. When no plug is
  found the tool says **NO LOOP** and waits instead of producing a table of
  meaningless failures.
- **Sweeps 11 baud rates** from 1200 to 921600, scoring each independently as
  PASS / WARN / FAIL
- **Live status table and gauge**, both owner-drawn, plus a scrolling log
- **CSV export** of results, timestamped per port
- **Releases the port properly on stop** — the worker thread is joined before the
  button re-enables, and the log says so explicitly:
  `-- Port released -- safe to open Device Manager --`

That last one sounds minor and isn't. A diagnostic tool that keeps a handle on
the port after you press Stop makes the *next* tool you reach for fail, and you
end up debugging the debugger.

## Running it

**Python:**

```bash
pip install pyserial customtkinter matplotlib
python serial_loopback_pro.py
```

**C# — build with the compiler already present on any Windows machine:**

```bash
C:\Windows\Microsoft.NET\Framework\v3.5\csc.exe /target:winexe /platform:x86 /out:SerialLoopbackPro.exe /r:System.Management.dll SerialLoopbackPro.cs
```

No SDK, no Visual Studio, no NuGet — `csc.exe` ships with Windows.

## Hardware

You need a loopback plug: a DB9 connector with pin 2 (RX) shorted to pin 3 (TX).
Two centimetres of wire will do. Everything else the tool handles.
