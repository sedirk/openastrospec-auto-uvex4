# UVEX USB relocation repair — 2026-09-12 / 0.4.0.178

## Cause and scope

The owner moved the UVEX USB cable and assigned its former socket/COM5 to the
roof controller. Both the service and frontend contained fixed-COM5 checks;
the old USB-family check could not distinguish several CH340 devices. The
reported connections failed with access denied before opening the roof's port.

The owner authorized bounded sequential identity queries, repair, installation
and N.I.N.A. restart. [ADR-0016](adr/0016-verified-configurable-uvex-serial-binding.md)
records the narrow design change. This work did not authorize or execute a new
observation, exposure, home, UVEX motion, or roof/cover movement.

## Implementation

- Normal UVEX connections use one explicit port and exact present USB-instance
  binding, then require a fresh firmware/UVEX4-description pair. They do not scan.
- The maintenance mode belongs to the same service executable. The installed
  service must be stopped; only explicitly listed, present, unreserved candidates
  are opened. Identity queries are bounded and each candidate is closed.
- Standard ASCOM reservations and additional machine-local reservations exclude
  other devices before opening a port. VID/PID alone is never sufficient.
- Frontend connection checks and live M2 focus evidence use the service-confirmed
  endpoint/USB instance. User-visible errors distinguish missing devices,
  reserved ports, changed bindings and non-UVEX responses.

The full build and 1,618 .NET tests passed before deployment. All 38 offline UI
scenarios rendered; the changed manual UVEX panel was visually inspected. This
does not replace the pending complete live three-panel UI acceptance.

## Installation and read-only verification

The first administrator launch was cancelled; no serial identification occurred
in that attempt. After explicit renewed approval:

1. Backed up the old service, manager, plugin and machine configuration.
2. Stopped only the UVEX service for maintenance identification. The candidate
   list contained COM3; COM4–COM8 were excluded as other device reservations.
3. COM3 returned firmware `2.3` and description `Microcontoler Arduino for UVEX4`.
   Saved the exact USB instance only in local configuration/evidence.
4. Installed the new service/manager and .178 plugin. N.I.N.A. exited normally;
   no forced termination was needed.
5. The installed service's explicit read-only connection succeeded at 20:45
   local time. It reported `Ready`, `SerialIdentityVerified=true`, live positions,
   slit slot 2, grating −1923 steps and M2 12500 steps. The temporary control lease
   was released. No axis or illumination command was issued.

Installed hashes:

| Artifact | SHA-256 |
|---|---|
| N.I.N.A. plugin | `F716CCCDDFDCF3621B73A50E1CEC218FBDDEDBB1354A86D3E506F0BEB7047F40` |
| UVEX service | `CA4C857EBE5FCA2872415E1D608D4DE9725EA8A56A8F5D8C50418A16920DE0D6` |

## The second binding layer

After the service/plugin replacement, the existing selected Night Setup still
bound UVEX M2 to COM5 and the previous USB instance. The owner's subsequent
frontend attempt failed `FOCUS_UVEX_SPECTRAL_IDENTITY` at Night Setup validation.
Service connectivity alone therefore did not complete this repair.

A new identity-only package was generated using the official commissioning tool:

- The owner reported a USB-only relocation. Fresh protocol identity and exact
  unchanged slit/grating/M2 readback were required before deriving the new version.
- Only the UVEX physical endpoint/USB binding, version identifiers and dependent
  hashes/references changed. The old Night Setup and measurement definition were
  not overwritten. Provenance identifies both original hashes and the readback.
- Original focus values, quality metrics, their measurement dates and evidence
  hashes remain historical measurements; this is not a new optical calibration.
- All other device bindings, PHD2 cadence, quality thresholds and motion/action/
  return/time limits were compared and retained. The official preset validation,
  including current PHD2 profile evidence validation, returned no issues.
- At an exposure-free paused boundary, N.I.N.A. exited normally again. A backed-up
  Profile received only nine existing package-reference fields; target, exposure,
  policy and authorization fields were not altered by this update.

The final startup selected `DF-UVEX4-FIELD-20260912-VERIFIED-USB-REBIND-COM3`
and explicitly reported successful static SHA-256, reference and identity checks.
The last N.I.N.A. process was PID 12140, version 3.2.0.9001, with plugin .178 and
the expected artifact hash. Its observation state was `Idle`, session real-control
authorization was false, and the new startup log contained no matched XAML,
binding or unhandled-error entries.

## Evidence and remaining acceptance

- Deployment backup/readback evidence: repository-local ignored
  `output/deployment-backups/uvex-serial-rebind-20260912/`.
- New immutable package and activation audit: machine-local
  `%LocalAppData%/UVEX-ADV/commissioning/uvex-usb-rebind-20260912/`.
- Final startup log:
  `%LocalAppData%/NINA/Logs/20260912-205640-3.2.0.9001.12140-202609.log`.

No full observing run was initiated by this repair. The new run must still check
all live device, guide, optical-quality and environmental conditions. The live
calibration-library/embedded-spectral-panel acceptance was not performed through
computer use, and the backend surface does not currently expose those panel
switches; do not label the full three-panel check as passed.

Rollback is an idle-boundary operation. Do not restore the original COM5 machine
configuration or select the old COM5 Night Setup while the roof uses that port.
