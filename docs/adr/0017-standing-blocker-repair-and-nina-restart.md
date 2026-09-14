# ADR-0017: Standing authorization to repair reported blockers and restart N.I.N.A.

**Status:** Accepted by explicit owner request  
**Date:** 2026-09-14  
**Supersedes:** the per-message N.I.N.A. installation/restart approval requirement
in `AGENTS.md`, only for the maintenance scope below. No equipment ownership or
runtime motion/science gate is superseded.

## Context

The owner repeatedly requested that a reported observation blocker be handled
through implementation, verification, installation and N.I.N.A. restart, not a
diagnosis-only response followed by another approval question. The owner explicitly
requested this standing instruction be recorded in the repository's agent rules.

## Decision

A newly reported N.I.N.A. observation/focus blocker, including a failure screenshot,
authorizes the assisting maintainer to inspect evidence, fix the responsible code,
verify it, back up the local Profile and installed plugin, install the exact built
artifact, restart N.I.N.A. normally, and verify the loaded version and real panels.
Do not require the owner to repeat “modify/install/restart” for each such report.
This remains effective until revoked or overridden by a newer owner instruction.

Deployment must reach a confirmed idle/no-exposure/no-motion boundary first. Use
the existing no-mechanical-action cancellation path for a paused run. Preserve
pending recovery ledgers and original observations. Do not force-kill a busy or
unconfirmed owner; a genuinely unconfirmed shutdown is an exceptional blocker
that must be reported, not an excuse to install into a live process.

This is an external maintenance instruction, **not** a self-modifying runtime
recovery loop, scheduled monitor, automatic observing authorization or permission
to bypass a failed gate. It does not authorize new exposures, motion/home/focus,
roof/cover actions, automatic resume, firmware changes, unrelated service restarts
or a second owner of a device. Independently authorized observing tasks retain
their own scope, weather conditions and stop instructions.

## Verification and consequences

- Changes retain focused regressions, production-route parity, complete build,
  XAML render checks and installed N.I.N.A. panel/log checks.
- Machine-local backups and raw evidence remain out of Git. Old failures are not
  rewritten as successes; replay and installation are distinguished from sky tests.
- A newer “do not restart”, pause, closure or diagnosis-only instruction overrides
  this default. Revert or supersede the rule explicitly if the owner withdraws it.
- The device ownership architecture, ADR-0001/0014/0016, and ADR-0002 runtime
  pause/resume semantics remain unchanged.

`AGENTS.md` is covered by the frozen-design manifest. This explicit owner-requested
maintenance decision is recorded here, summarized in the baseline, and acknowledged
using the prescribed hash-update script; no frozen check is disabled.
