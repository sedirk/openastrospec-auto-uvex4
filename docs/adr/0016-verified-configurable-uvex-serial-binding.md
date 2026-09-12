# ADR-0016: Verified configurable UVEX serial binding

**Status:** Accepted; supersedes only ADR-0001's fixed COM5 endpoint and the old
blanket prohibition on explicitly authorized maintenance identification. All
device ownership, motion, lease and observation safety rules remain unchanged.
**Date:** 2026-09-12
**Authorization:** The owner explicitly requested bounded sequential identification
after relocating the UVEX USB cable, followed by repair, installation and N.I.N.A. restart.

## Problem

The configured port and two code guards assumed COM5 forever. After relocation,
RRCI used COM5. Six present devices shared CH340 VID/PID, so the old registry-only
check could not distinguish a roof from UVEX and also accepted phantom USB entries.
Port-open denial prevented communication in the reported attempts; it was not a
successful UVEX-specific identity guard.

## Decision

- Keep `UvexAdv.Service` as the only UVEX owner. `Uvex:PortName` is configurable,
  paired with `ExpectedUsbInstanceId`; COM5 is merely a legacy example.
- Before opening, require a unique **present** matching USB endpoint and reject
  ASCOM-reserved ports, even when the other owner is currently disconnected.
- Before any normal control, require a fresh `IVE1` firmware and `IDE1` UVEX4
  description pair. VID/PID and an operating-system port number are insufficient.
- Explicit maintenance identification is a command-line mode of the same service
  executable, with the installed service stopped. It accepts 1–8 explicit ports,
  checks reservations before each open, uses 115200 8N1 with DTR/RTS disabled,
  waits for the commissioned serial-open delay, then sends only the two identity
  queries. Each query has a 2-second timeout and each candidate a 10-second bound.
  No axis, output, initialization, calibration or firmware command is sent.
- Opening a generic USB serial adapter is not guaranteed physically inert on all
  hardware. Device reservations are therefore excluded rather than tried, and
  this mode requires explicit operator authorization, never an automatic retry.
- Return all positive and negative evidence and close each candidate. Do not
  select the first match, steal occupied ports, stop another owner, or auto-save
  a binding. Multiple matching instruments require explicit operator selection.
- Normal connect and unexpected-loss recovery use only the saved endpoint; no
  hidden scan, hot handoff or arbitrary-port fallback is introduced.

## Identity and calibration consequences

A topology-derived CH340 instance can change after a USB move and is not a serial
number. Re-identification is mandatory rather than pretending it is portable
physical identity. Existing raw data, calibration rows and commissioning packages
are immutable. Old port-bound calibration entries are not silently accepted under
a different port, and a changed focus/USB binding must be separately revalidated.

## Commissioning and rollback

Test missing/duplicate/changed endpoints, reserved roof/mount/cover/switch ports,
echo-only or non-UVEX replies, timeout/cancellation and closure without hardware.
Then explicitly identify only unreserved candidates, save the selected endpoint
in the backed-up local service configuration, install, and test normal readback.
This is not motion or full observation acceptance. Startup remains disconnected.
Rollback restores the backed-up executable and configuration only at idle; never
restore an old COM5 binding while COM5 belongs to the roof.
