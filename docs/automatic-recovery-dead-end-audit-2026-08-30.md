# Automatic-recovery dead-end audit — 2026-08-30

Status: source closure and offline fault-injection report. This document does
not authorize equipment movement, acquisition, installation, service changes,
or a N.I.N.A./PHD2 restart. It does not change the frozen device-ownership
baseline.

## Goal

Repeated real runs showed that several locally recoverable failures were being
reported as terminal stage failures. The safe correction is not a generic
`catch/retry`: it is an exact, bounded recovery selected by stage, structured
gate code, device ownership, current epoch and durable action state.

This audit separates terminal outcomes into five classes:

1. repeat an identity-pinned, idempotent connection or readback;
2. discard only derived state and obtain fresh immutable evidence;
3. rebuild the earliest safe dependency chain;
4. reconcile a durable physical-action debt from fresh readback without
   resending an ambiguous command; or
5. stop for operator review because safety or physical provenance cannot be
   proved.

## Supervisor invariants

Automatic recovery is whitelist-only. A new or misspelled gate code cannot
inherit a retry from similar message text.

- Each exact `(stage, code, action)` has its own reviewed attempt limit.
- A run-local recovery session has an additional eight-attempt total fuse, so
  alternating codes cannot manufacture an infinite loop.
- Nested failures inside a dependency-rebuild chain re-enter the same
  supervisor with their real inner stage. The outer chain is re-armed after a
  successful inner recovery, so a failed prerequisite cannot be skipped.
- Fresh-evidence retries never reset G3/PHD2 movement attempts, cumulative
  distance, elapsed-time limits, return reserve, lineage or durable debt.
- A passing warning is never retried.
- Cancellation is never converted into recovery.

## Closed dead ends

### Stage-level orchestration

| Previous terminal path | Bounded recovery | Bound | Safety property |
| --- | --- | ---: | --- |
| Recoverable failure occurred inside the QHY → G3 → slit → guide → photometry resume chain | Classify the inner stage/code through the same supervisor and re-run the outer dependency chain | Exact inner bound plus eight-attempt session fuse | A failed prerequisite cannot be skipped and unrelated errors do not consume each other's exact allowance |
| Missing accepted QHY source/solve before coarse centering | Reacquire one immutable QHY field and solve before retrying centering | 1 | No stale QHY frame can authorize a correction |
| Missing G3 target/field before placement or guiding | Reacquire G3 and rebuild only the required downstream slit/guide proof | 1 | Old detector evidence cannot cross a movement or guide epoch |
| Lost/unstable guiding before photometry, ATR tier selection or science | Fresh G3 → slit placement → guide epoch → optional photometry | 1 | No new ATR shutter action occurs before the rebuilt guide proof passes |
| ATR science stage lost its selected exposure tier | Invalidate the derived tier and run the existing bounded reprobe | 1 | An old or absent tier cannot silently authorize science |
| Checked terminal cleanup was incomplete | Repeat only stop/OFF/close operations | 2 | Finalization never reopens or resumes an owner |

### G3 / PL3 / target and slit evidence

| Previous terminal path | Bounded recovery | Bound | Safety property |
| --- | --- | ---: | --- |
| Stale, missing, unreadable or temporarily unreadable G3 mount binding during `AcquireG3SlitField` | Acquire a new immutable G3 evidence set at the unchanged durable position | 1 | Hash/context/epoch/pier/topology mismatches remain hard stops |
| The same disposable binding failed later in `PlaceTargetOnSlit` | Re-enter the existing dependency chain and reacquire G3 before placement | 1 | Placement never consumes a stale field; no motion ledger is reset |
| No coherent source and cloud/transparency cannot be distinguished from an empty field | Wait five seconds and recapture at the unchanged position | 3 | No blind mount search is authorized by the cloud retry |
| Bright-target evidence contains only annular ghosts or has unproved topology | Obtain one independent immutable G3 evidence set | 1 | Morphology alone never fabricates catalogue identity or motion authority |
| Slit identity geometry is temporarily unavailable while locked identity remains unchanged | Rebuild one fresh evidence set | 1 | Detector/calibration/position/parity mismatches remain hard stops |
| QHY WCS Sync was accepted but its mount readback could not verify the Sync | Retain the fresh formal QHY WCS as a hint-only authority for the next G3 solve | 1 evidence transition | The ambiguous Sync is never resent and never claimed as verified mount state |
| A reviewed PHD2 capture, FITS/SHA source-read or configured-solver transient occurred inside the PL3 exposure ladder | Abandon that reserved path, record a structured non-motion attempt and advance to the next commissioned tier with a new immutable path | Finite configured ladder; one outer fresh-ladder retry after pure-transient exhaustion | Cancellation, disconnect, identity, profile, access, cryptographic/hash and configuration failures remain hard; a pure-transient ladder cannot authorize neighbouring-field motion |

### PHD2 slit placement and durable return

| Previous terminal path | Bounded recovery | Bound | Safety property |
| --- | --- | ---: | --- |
| A runtime lock-position read returned no point during recovery/return | Repeat only `get_lock_position` while the same connected Guiding epoch remains current | 3 production reads, API bound 1–5 | No guide, stop, selection or exact-lock mutation is emitted; LostLock/epoch change stops immediately |
| The lock point drifted after a durable return intent was precharged but before dispatch | Persist the fresh actual lock and replan inside the existing attempt/pixel/time envelope | Existing durable envelope | The old exact-lock vector is never sent; precharged attempts and pixels are never rolled back |
| A locally issued exact-lock or operation-bound settle legitimately advanced `GuideEpoch` | Rebind the durable pending record only after same-connection fresh readback/settle attestation | Per existing stage | Lineage, origin, budgets, debt and earliest `StartedUtc` remain unchanged |
| Wind prevented settle from entering the requested circle while the same guide operation remained alive | Keep that one guide operation and require a fresh GuideStep/FITS residual window under supervised opt-in | One guide RPC | No guide/stop loop; disconnect, LostLock or epoch change cannot use wind degradation |
| Selection/loop configuration invalidated cached calibration attestation | Re-read actual calibration immediately before every guide/settle operation | Every guide boundary | Valid calibration continues; invalid calibration uses the single existing forced-recalibration path and then fresh G3 evidence |

### Equipment and optional channels

- The UVEX service distinguishes an unexpected configured-COM5 transport loss
  from an explicit operator disconnect. Unexpected loss invalidates live
  position/output trust, then the hosted service reopens only COM5 and rereads
  identity, slit inventory and live positions. Manual disconnect never arms
  background reconnect.
- Night Setup retries only reviewed identity-pinned connection/readback gates.
  Every retry re-runs owner/profile checks and cannot authorize a slew,
  exposure, cover/roof move or UVEX output.
- Optional QHY health, preview, pause, resume and photometry failures isolate
  the optional channel while retaining its owner-job handle for terminal
  cleanup. They do not block ATR/G3/UVEX spectroscopy.
- Optional QHY terminal readback during finalization is attempted twice using
  read-only job status. If it remains unavailable, finalization records a
  warning and continues UVEX OFF, PHD2 stop, cover and roof cleanup; it never
  reconnects the QHY camera or starts/resumes a job.
- If the locked optical-cover adapter is disconnected during cleanup, the
  runner revalidates the current Profile selection and reconnects that exact
  adapter once without a cover command. It rereads identity and state before
  entering the existing idempotent close plus terminal-readback path.
- A verified UVEX slit-illumination OFF followed by lease-release failure is a
  warning. An OFF command that still cannot be command-completed and
  readback-verified after its exact-COM5 recovery remains a hard stop.

## Deliberate hard stops

The following are not dead ends; they are boundaries where automatic software
cannot prove that another physical action is safe.

### Safety, identity and configuration

- explicit unsafe weather, rain, Safety Monitor failure, protected horizon,
  roof/cover error or parked/untrackable mount;
- device owner, camera/profile, COM-port, commissioning, Night Setup or stable
  identity mismatch;
- uncommissioned optics, parity, slit destination, guide mode or supervision
  authority;
- unknown/changed pier side or coordinate epoch where a pending vector could be
  reinterpreted; and
- LED OFF not command-completed and readback-verified.

### Immutable evidence and topology

- FITS/evidence/manifest/self-hash change;
- source, run, configuration, detector geometry or topology mismatch;
- target/ghost candidates still non-unique after the allowed fresh evidence;
- catalogue/WCS authority missing where morphology cannot establish identity;
  and
- a reused frame or stale target/slit continuity proof.

### Physical action and durable debt

- an ambiguous movement or exact-lock mutation that fresh readback cannot
  reconcile;
- multiple active/outstanding lineages or a missing/corrupt durable ledger;
- failed return, unknown endpoint or safety change after intent was written;
- single-step, cumulative distance, attempt, return-reserve or elapsed-time
  exhaustion; and
- an origin reached but stop state not positively confirmed.

### PHD2 hard ceilings

- true disconnect or `LostLock` after the one bounded full G3/PHD2 rebuild;
- real calibration hard-ceiling failure after the single forced recalibration;
- guide/settle failure that cannot satisfy the same-operation supervised wind
  rules;
- native guide-star selection exhaustion where no commissioned fallback is
  authorized; and
- profile, installation epoch, topology, target identity or fresh residual
  evidence that cannot be proved.

## Offline verification

The closure uses fault injection and source-structure assertions; it does not
open a physical camera or COM5, start PHD2/N.I.N.A., slew a mount, move UVEX,
operate a roof/cover or update installed software.

Key scenarios include:

- exact retry exhaustion and the eight-attempt alternating-code fuse;
- nested dependency failure returning to the same supervisor;
- cloud frames recovering without motion and then stopping at the bound;
- stale placement bindings rebuilding G3 exactly once while hash/epoch/pier
  mismatches remain hard;
- same-epoch PHD2 lock readback succeeding after transient nulls and stopping
  immediately on LostLock;
- pre-dispatch lock drift replanning without command resend or budget rollback;
- locally attested guide-epoch rebind preserving every durable budget field;
- supervised settle timeout issuing one guide and no stop/re-guide loop; and
- UVEX unexpected transport recovery versus explicit manual disconnect.

Final merged verification on 2026-08-30:

- frozen-design baseline, product layout, public branding and coordinate-command
  safety checks passed;
- the complete Release solution built with 0 warnings and 0 errors;
- all 12 .NET test assemblies passed: 904 passed, 0 failed, 0 skipped,
  including Observatory 306/306, N.I.N.A. Plugin 349/349, PHD2 118/118 and
  the production-template UI harness 8/8;
- the reduction application's Ruff check passed and Pytest passed 66/66; and
- N.I.N.A. plugin artifact version `0.4.0.80` was atomically installed locally;
  all seven required installed DLL hashes match the published artifact. N.I.N.A.
  was deliberately not started, so plugin-load and live-panel replay remain pending.

## Remaining engineering candidates

These require a separate design/replay cycle and are not hidden behind the
generic retry supervisor:

- accept a stable actual mount arrival for fresh optical verification when it
  is inside all durable bounds but outside an unrealistically strict command
  residual;
- add a bright/ghost-specific fresh-frame path that reuses valid run-scoped
  detector-fixed slit geometry instead of repeating both HDR sequences;
- distinguish an externally initiated PHD2 `GuidingStopped` from a local checked
  stop before treating it as relock evidence; and
- replace the remaining aggregate `PHD2_RELOCK_G3_REACQUISITION_BLOCKED` and
  generic safe-failure wrappers with typed `FailedStage`/`CauseCode` data before
  considering any additional automatic recovery. Message parsing is not an
  authorization mechanism.

The previously listed native `find_star` no-candidate and returned-origin stop
dead ends are closed in `0.4.0.80`: only a successful null selection consumes the
four-frame fresh-selection budget, and a freshly verified origin permits one
additional idempotent checked-stop readback without guide/lock/mount commands.
