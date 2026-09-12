# ADR-0015: Persist an explicitly selected supervised slit-quality policy

**Accepted:** 2026-09-11, following the owner's request to fix next-session
repeatability after the same target failed with a silently restored strict policy.
Supersedes only ADR-0013's process-local lifetime for scientific-quality consent.

## Context

The 0.4.0.167 frontend completed 10 Lac with explicitly approved precision-warning
probing. Restarting N.I.N.A. discarded that choice. The next manual run approached
the same target but repeatedly required an entire window within 2 pixels, returned,
rebuilt acquisition, and finally reported an inherited time-budget failure. The
choice was hidden under the model-control settings rather than normal run controls.

## Decision

- An explicitly selected strict/warning-probe policy is saved in the current
  N.I.N.A. Profile, including an immediate Profile save and visible save failure.
  A new Profile remains strict. Do not infer a choice from historical data or
  migrate all installations to warning mode.
- Display and edit this choice in the fixed run-control header. Both UI and model
  invoke the same commands; no active run can change its frozen policy.
- Enabling it through the backend still requires the separate scientific-quality
  attestation. Existing hardware control arming remains process-local, resets on
  restart, and requires its own current attestation. Saving quality policy never
  starts a run, connects a device, grants movement, or enables unattended operation.
- Dockable and Advanced Sequencer continue to capture the same RealRunConfiguration.
  The choice remains in its hash and is now also named in manifest labels.
- ADR-0013's fresh-frame, measured finite slit, guide/lock, target-identity,
  probe-eligibility, spectrum-quality, FITS warning and raw-data rules are unchanged.
- Exhaustion of the completion window follows the original charged return path.
  After verified return and checked stop, retain a specific quality/time outcome
  with measured residuals. Do not classify it as a recoverable LostLock and spend
  another full-field rebuild that cannot renew the original placement clock.

## Safety, rollback and acceptance

No owner, motion limit, return reserve, safety or horizon gate changes. Strict
acceptance can be explicitly restored and saved while idle. Original observations
and failures are immutable; a new run does not relabel them.

Tests cover new-profile default, settings recreation, strict rollback, frozen
configuration/source parity, visible control placement, localization and the
non-rebuilding timeout outcome. The installed build must additionally demonstrate
a Profile save/restart/readback, followed by fresh real frontend runs. Compilation,
simulation or one successful target alone is not universal on-sky acceptance.
