# ADR-0053: Successful-target parse and trial retention

- Status: Accepted
- Date: 2026-11-06
- Supersedes: the blanket raw-file immutability sentence only for the exact
  successful-target files defined below.
- Authorization: the observatory owner explicitly requested that every
  successful target delete its solve frames and trial frames, while retaining
  the final trial that was accepted as science.

## Context

A successful target currently retains the complete acquisition history: dozens
of QHY wide-field solve frames, G3 plate-solve/slit frames, and every ATR
exposure probe. Accepted spectra and simultaneous QHY photometry must remain,
but the discarded solve/trial frames consume disk and have no remaining
scientific role unless a later project or model explicitly references them.

The existing optional retention worker covered only rejected ATR probes. It did
not clean QHY/G3 solve frames, did not implement the final accepted trial rule,
and was not operational at startup.

## Decision

1. Evaluate cleanup per target observation run, never per sequence. A run is
   eligible only when it started after the durable policy-enable watermark, its
   immutable manifest and indexed state both explicitly report `Completed`, and
   at least one accepted science FITS is present and hash-verified. Enabling the
   policy is forward-only and never performs a retroactive historical purge.
2. For an eligible run, exact-file candidates are limited to:
   - QHY acquisition FITS used for wide-field solve/acquisition;
   - G3 FITS used for plate solving, slit geometry, target confirmation,
     lock residual or guide selection;
   - ATR `PROBE` FITS not credited as accepted science.
3. Never select ATR science FITS, a probe credited/reused as science, QHY
   photometry FITS, calibration data, reduction products, `.astroproj` files,
   logs, manifests, JSON evidence or any file with unknown quality/identity.
   The last/final trial accepted into science therefore remains present under
   its original `PROBE` and `SCIENCE` provenance.
4. Every candidate must match the run manifest's exact absolute path, SHA-256,
   capture identity where available and terminal run record. FITS header
   `OBSRUNID` is checked when present; any `PROBE`/`SCIENCE` marker on a proposed
   parse frame protects it. Unknown or conflicting metadata protects the file.
5. Reuse by another run, an active/unknown related run, a saved project,
   calibration/model dependency, operator pick/rating or changed filesystem
   identity vetoes deletion. The worker re-discovers dependency metadata and
   revalidates the complete plan immediately before each locked-handle
   permanent deletion.
6. Execution is a separate offline Windows worker at below-normal process
   priority. It never opens a camera, PHD2, mount, roof or UVEX device and never
   changes N.I.N.A. observation state. A fresh index scan, durable per-file
   intent/result journal and source hashes are mandatory. Any uncertainty
   defers cleanup instead of interfering with acquisition.
7. The worker is opt-in and policy-versioned. The target-success policy uses
   its own enable flag, startup shortcut, status file and single-instance lock.
   Enabling it retires only the older rejected-probe opt-in files; disabling it
   lets an in-progress exact-file batch finish with its audit.

## Consequences

- Successful targets free the bulk of acquisition and probe FITS while retaining
  the spectra, photometry and complete textual audit history.
- Failed, active, cancelled or incomplete targets are not automatically
  cleaned. Their frames remain diagnostic evidence until a separate explicit
  policy is requested.
- A file referenced by future work is retained even if it would otherwise match
  this policy. This can leave some parse/trial frames behind; that is preferable
  to breaking a dependency.
- The manifest still lists deleted frames and their original hashes. Deletion
  receipts identify the exact removed path, size, policy and time.

## Verification

Synthetic filesystem tests must cover successful, failed and active runs;
accepted science and credited trial preservation; QHY photometry preservation;
unknown-quality probes; changed hashes; project/model dependencies; and an
actual Windows permanent batch. Existing rejected-probe policy and all broader
library cleanup tests must continue to pass.
