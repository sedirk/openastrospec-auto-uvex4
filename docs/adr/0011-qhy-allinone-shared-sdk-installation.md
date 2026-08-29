# ADR-0011: One official QHY AllInOne installation and no private service SDK copy

**Status:** Accepted  
**Decision date:** 2026-08-29  
**Supersedes:** the QHY deployment detail that copied the vendor SDK into the
UVEX-ADV service directory; it does not change ADR-0001 device ownership

## Context

The QHYminiCam8M is owned exclusively by `UvexAdv.Qhy.Service`, but the first
hardware deployment copied `qhyccd.dll` and the rest of the vendor x64 SDK into
`C:\Program Files\UVEX-ADV\QhyService\native`. The installed QHY AllInOne tree
therefore contained one SDK copy while the service loaded another. Updating one
copy could leave the Windows driver, ASCOM components, QHY applications and the
production acquisition service at different release levels.

Two acquisition incidents on 2026-08-26 and 2026-08-28 returned an all-zero
frame after an abnormally long readout, followed by loss of SDK controls and a
USB state that only a physical power cycle recovered. Those incidents do not by
themselves prove an SDK defect, but they make an undocumented split installation
unacceptable for diagnosis and reproduction.

The observatory owner explicitly chose complete official QHYCCD package
installation as the durable update rule and rejected a second private SDK copy.

## Decision

1. Install or update QHY software by running the complete official Windows
   AllInOne distribution. Record the source URL, distribution version, file
   SHA-256 and Authenticode status; an official download URL is not represented
   as a digital signature.
2. The production QHY service loads the x64 `qhyccd.dll` directly from the
   official vendor installation, normally
   `C:\Program Files\QHYCCD\AllInOne\sdk\x64\qhyccd.dll`.
3. The machine-local QHY configuration continues to bind that exact DLL by
   absolute path and SHA-256. A complete-package update intentionally changes
   the hash and requires a corresponding machine-local configuration update.
4. `install-qhy-service.ps1` verifies the configured vendor path and hash but
   does not copy vendor binaries into the service deployment. A hardware
   artifact that already contains a private `native\qhyccd.dll` is rejected.
5. QHYminiCam8M ownership does not change: N.I.N.A. and the UVEX plugin still do
   not load the QHY SDK or open this camera. A shared on-disk vendor installation
   is not shared runtime ownership.
6. Driver or SDK updates are commissioned with the camera disconnected during
   installation, followed by exact USB identity, SDK version, firmware/FPGA
   version when readable, and bounded no-motion camera verification. Firmware is
   never guessed from the Windows driver version and is not blindly reflashed.

## Consequences

- QHY driver, vendor tools and the service have one documented installation
  source and one vendor SDK tree.
- Rollback restores the prior complete AllInOne package and its recorded shared
  SDK hash; it does not restore an orphaned service-local DLL.
- Updating the shared vendor SDK can affect every application that deliberately
  loads that installation. Installation and rollback therefore require explicit
  maintenance mode and version evidence.
- The service retains fail-closed exact device identity and SDK hash validation.
  Removing the private copy does not relax provenance or allow ordinal camera
  selection.
