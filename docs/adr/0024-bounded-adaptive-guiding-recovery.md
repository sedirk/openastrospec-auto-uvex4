# ADR-0024 Bounded adaptive guiding recovery

- Status: Accepted
- Date: 2026-10-10
- Authorization: the owner requested cloud/fog star-loss waiting, followed by star reselection and, if guiding still fails, automatic forced PHD2 calibration rather than immediately ending the night.
- Supersedes: the prohibition on automatic forced calibration in ADR-0020 and ADR-0023, only for the bounded runtime recovery below. Device ownership, advisory calibration metadata, return responsibilities and safety interlocks are unchanged.

## Decision

The native target card saves `AutomaticGuideRecovery` (default true) and `GuideStarWaitSeconds` (default 300, range 30–600). These choices are locked with the target plan, cloned and serialized without changing the shared Profile. Standalone automatic observation uses the same recovery with the 300-second default. Disabling it retains the earlier bounded recovery behavior; it does not disable safety or enable uncontrolled retries.

Only structured guiding loss and reviewed initial-settle/optical-window failures enter recovery. Device disconnection, missing pulse output, RPC errors, changed ownership, calibration metadata warnings, safety failures and unresolved movement are not classified as clouds or bad calibration. A current, local checked-stop receipt and settled movement ledgers are required before fresh star detection. A stopped guide is not evidence of physical return.

At the unchanged pointing, PHD2 takes immutable 2-second full frames through the already-commissioned native `capture_single_frame` interface, with the slit lamp off and the original detector identity. Implementation correction, 0.4.0.266: the initial loop/save implementation could cause PHD2 to reselect its old star during a preview frame, contradicting recovery's unchanged-selection requirement. It is not an inert capture and is no longer used here; unsupported native capture does not silently fall back. The stopped connection/guide epochs, selection, lock, calibration and frame receipt must remain consistent. No search move, lock shift or calibration is sent while waiting. Two consecutive fresh frames must each contain at least three usable unsaturated stellar detections, median SNR at least 5 and sampled saturation below 1%. Empty, weak, clipped, invalid or stale frames cannot trigger calibration. The description remains **suspected cloud/fog or insufficient field signal**, not a weather diagnosis. A star field is not proof of target identity or slit placement.

Once usable stars return, the existing acquisition chain takes new target/slit evidence and asks PHD2 to select a guide star again. If another eligible guiding failure occurs and new clear frames again pass the check, reserve one `guide(recalibrate=true)` request before dispatch. PHD2 alone performs calibration; the plugin does not write calibration coefficients or tune aggression/guide rates. After an observed calibration, checked stop and fresh target/slit acquisition remain mandatory before placement and science.

There is at most one reselection recovery and one forced calibration per logical target, including meridian continuations. An explicit starting calibration or an already observed native calibration consumes the same automatic-calibration allowance. Timeout, cancellation or failed dispatch never restores that allowance. The recovery session expires after 900 seconds, each star-wait episode also has its saved limit and a 42-frame ceiling. Existing action, exposure, return and elapsed-motion budgets are not reset or paused by waiting.

When recovery or waiting is exhausted, confirmed idle and settled motion permit the native sequence to apply that target's existing failure policy. `SkipQualityFailure` skips only the failed target after independent idle/ledger checks; `EndNight` still honors the operator's explicit choice. No recovery path closes or reopens the roof. AIWeather Unsafe, dawn, deadlines and configured emergency/final closeout remain authoritative. Unconfirmed stop or unresolved return remains a hard blocker.

## Verification and commissioning

New native target cards default to `SkipQualityFailure`. An existing sequence's explicitly serialized `EndNight` policy remains unchanged; changing recovery options must not silently rewrite that choice.

Verify clear/weak/saturated/replayed frames, interrupted continuity, one-shot action reservation, explicit calibration interaction, clock rollback, total timeout, per-target serialization and meridian sharing. Verify production wiring through checked stop, original PHD2 capture, unchanged mount binding, normal acquisition and independent native skip checks. Unknown exceptions and output/ownership faults must not enter the quality allow-list.

The source/build and isolated UI tests issue no exposures or movement. Real-sky acceptance requires a separately authorized run: observe a star-loss wait without pulses, restored stars followed by selection, a failed selection followed by exactly one native calibration, and safety/cancellation at each boundary. Do not claim on-sky recovery from simulated or static test results.

PHD2 event and RPC semantics follow its [official event monitoring interface](https://github.com/OpenPHDGuiding/phd2/wiki/EventMonitoring). `StarLost` alone does not establish cloud cover; PHD2 lists multiple lost-star causes in its [troubleshooting guide](https://openphdguiding.org/man/Trouble_shooting.htm).
