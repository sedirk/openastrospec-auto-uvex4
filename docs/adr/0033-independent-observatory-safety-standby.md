# ADR-0033: Finite observing sequences and independent observatory safety standby

Status: Accepted; installation and real-weather commissioning are separate acceptance steps.

Date: 2026-10-11

Authorization: the owner explicitly requested design change and repair, separation of
observing from safety standby, normal native sequence exit after completion/cancel,
retained AIWeather/dawn/cutoff protection, and idle-boundary installation without resume.

## Decision

Supersedes the in-sequence standby lifetime and cancellation/operator-handoff portions
of ADR-0019 / ADR-0026. Device ownership, weather authority, physical stop requirements,
motion ledgers and checked closeout order remain unchanged.

- The native night container executes finite target work and records a terminal outcome.
  Holding the roof open must not keep its `Execute` task or the native sequencer running.
  Failed outcomes remain failed; returning from a task is not acquisition success.
- After owners join, a selected keep-open completion/failure or ordinary cancellation
  transfers the existing exclusive orchestration lease to an independent safety owner
  within the same master N.I.N.A. process. A separate watch is armed and its intent
  journal saved before the old observing watch/reservation is released.
- The guardian has no exposure, guiding, target acquisition, calibration or resume
  entry point. It keeps an immutable copy of the original deadline, dawn/site settings,
  bound configuration and checked-closeout parameters. Editing/reusing the native card
  cannot mutate this request. Parent sequence cancellation does not cancel the guardian.
- AIWeather Unsafe events, unsafe/disconnected/rebound monitor readbacks, dawn, cutoff,
  or explicit safety-closeout request invoke the existing bounded checked closeout once.
  Cancellation itself sends no new mechanical command. Safety still overrides an
  ordinary no-close preference. Closeout failure is retained and notified, not retried
  indefinitely or labelled closed. Unknown physical stop retains monitor/diagnosis only;
  no mechanical permission is fabricated, and this deficient protection is explicit.
- A newly **explicitly started** native night can atomically take over waiting protection
  only with the same locked configuration, compatible real/simulation mode, safe
  readbacks, no due cutoff/dawn, and a confirmed idle/settled prior target boundary.
  Its safety watch is installed before retiring the previous one, without releasing the
  cross-process lease. If closeout has started, the new run must wait until it finishes;
  it cannot cancel a roof movement. Focus/standalone starts cannot race into standby.
- A new explicit night resolves its own deadline under ADR-0025. Merely loading/editing
  a file does not transfer protection or extend a cutoff. Two files do not auto-run or
  promise seamless handoff at an already-expired midnight cutoff.
- Observing and standby have separate journals and status. The global plugin panel
  exposes standby state and a dedicated closeout button even if the native sequence is
  unloaded. Begin-run resets transient night text/outcomes/flags before recording Running;
  no previous cancellation message is copied into the new run. Raw records stay immutable.
- This is **not** a second hardware owner, Windows watchdog or unattended protection
  across application exit. Closing N.I.N.A. ends its software monitor; maintenance requires
  safe idle and operator/independent protection. Restart never auto-restores motion or
  observations from a historical standby journal.

## Regression contract

Exercise real native sequential execution returning with a live simulated guardian;
cancel during target and after return; failed targets with preserved unresolved debt;
immutable settings and clean next-run journal; each safety trigger; exactly-once closeout;
failed closeout and persistence; new-watch failure; busy/unsafe/config-mismatched handoff;
old lease disposal after transfer; shutdown with no motion and no automatic restoration.
Keep existing acquisition/science and historical productive-record regressions.
UI verification must distinguish a stopped observing sequence from active protection.
