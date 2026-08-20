# Security and hardware-safety policy

OpenAstroSpec Auto — UVEX4 can control observatory equipment. A software defect may move hardware, expose
an unauthenticated loopback API if misconfigured, or damage an observing run. Please do
not test a suspected vulnerability on unattended or moving equipment.

## Reporting

Until a dedicated private security address is published, do **not** open a public issue
containing credentials, private network locations, exact production configurations, or
details that make active equipment unsafe. Contact the repository owner privately and
provide a minimal simulator reproduction where possible.

Include the affected product, commit/version, operating system, gate/error code, and
sanitized logs. Do not attach raw observations or device identifiers without permission.

## Supported scope

- The loopback services are designed for `127.0.0.1`; exposing them to a LAN or the
  internet is unsupported.
- Simulator, replay, read-only status, and synthetic-image reproductions are preferred.
- Never bypass single-owner device rules, leases, motion bounds, or immutable evidence
  checks to demonstrate a report.
- N.I.N.A., PHD2, ASCOM drivers, camera SDKs, firmware, and operating-system components
  have their own security channels and update policies.

Public issue templates and a private contact channel should be finalized before the
first public GitHub release.
