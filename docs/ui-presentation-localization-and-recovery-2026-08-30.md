# Operator UI, localization and bounded-recovery convergence — 2026-08-30

## Scope

This change began as an offline presentation and recovery audit of the N.I.N.A.
plugin. It does not change the frozen device-ownership design, open a camera,
open COM5, connect PHD2, move the mount or UVEX, or start an exposure. After the
complete offline verification passed, the final `0.4.0.80` artifact was installed
atomically at the operator's explicit request. N.I.N.A. was not started or
restarted, so no plugin-load or equipment-connected claim is made here.

The operator request had three parts:

1. remove duplicated and visually inconsistent status/error/progress displays;
2. separate Chinese and English according to N.I.N.A.'s selected UI language;
3. while tracing error paths, repair safe self-recovery dead ends that could be
   proven offline and bounded without weakening a safety, identity, evidence or
   motion gate.

## Previous presentation problems

- The same blocker could appear as the fixed header status, pause reason,
  failure card, quality-gate row, timeline row, global footer error, N.I.N.A.
  notification and Windows notification.
- The fixed state chip stayed green even when the run was paused for attention.
- Quality-gate rows carried disposition/severity data but rendered them with
  one yellow style.
- The main progress bar advanced only when a whole observation stage completed.
  `RealObservationStageRunner.Report(...)` was connected to an empty
  `Progress<ApplicationStatus>` and therefore never reached the dock.
- Advanced settings were a single long scroll containing ordinary preferences,
  dangerous opt-ins, identity hashes, legacy compatibility parameters and
  live diagnostics at the same visual level.
- Static XAML was Chinese while coordinator/adapter `Message` values were often
  complete English sentences. Raw gate/event text was displayed directly.
- The old operator guidance classified hundreds of codes with broad substrings
  such as `G3`, `PHD`, `QHY` or `ATR`, so very different failures received the
  same generic Chinese recommendation.

## Canonical UI hierarchy

The observation dock now uses these layers:

1. **Fixed run bar** — mode, semantic state chip, current and next stage,
   completed/total stage count, overall percentage, current bounded operation,
   optional operation percentage, and controls.
2. **Diagnostics and evidence** — one top-level workspace with nested tabs for
   the current issue, quality gates, timeline and evidence files. The previous
   separate top-level Failure diagnostics and Quality gates/evidence pages were
   merged.
3. **Current issue card** — specific summary, stage/disposition, stable machine
   code, current impact, bounded automatic-handling statement, localized quality
   values, recommended action, evidence links and a collapsed raw technical
   message.
4. **Local UI-operation feedback** — the bottom strip is no longer another run
   blocker. It reports command/form feedback in an informational or amber card;
   any raw adapter sentence is placed in collapsed technical details.
5. **Advanced settings** — save state remains at the top. Content is divided into
   run admission/data archiving, acquisition/slit/guiding, parallel observing/site,
   device identity/calibration evidence, a collapsed acquisition-algorithm and
   recovery-budget section, and a collapsed identity/hash/path engineering
   section. Duplicate ghost diagnostics were removed.

Semantic tones are centralized as `Neutral`, `Info`, `Success`, `Warning`,
`Recovering`, `Attention` and `Fault`. A bounded retry is visually distinct from
an exhausted blocker.

## Language boundary

The authoritative language is:

```text
IProfileService.ActiveProfile.ApplicationSettings.Language
```

The dock listens to `IProfileService.LocaleChanged`. A `zh-*` culture selects
Chinese; all other cultures currently use the neutral English presentation.
`CultureInfo.CurrentUICulture` is only a fallback before a N.I.N.A. profile is
available.

### Static XAML

`ObservationStaticTextLocalization` applies a reversible translation catalog to
static `Text`, `Content`, `Header`, `ToolTip`, image empty-state strings and
static `Binding.StringFormat` values. It preserves the original template value,
including values whose WPF value source is `ParentTemplate`, so an in-process
language change can switch in either direction.

Every current Chinese static literal in `Templates.xaml` and
`EmbeddedImageViewer.xaml` (including accessibility names) has an English catalog
entry and a coverage test. The image viewer's code-generated level/stretch status
and pop-out empty state use the same language source. A newly added unmapped Chinese literal is made
conspicuous in English and fails the catalog-coverage test instead of silently
creating a mixed-language UI.

### Dynamic run diagnostics

The state machine remains language-neutral. These values are never translated or
mutated:

- error/event codes;
- raw adapter messages and exception text;
- evidence JSON/FITS fields;
- file paths, hashes, `DeviceId`, profile IDs and action hashes;
- motion ledgers and numeric budgets.

`ObservationUiPresentation` converts stage, run state, gate disposition, common
metric names and reviewed high-frequency error codes into operator text. The
raw message is retained only in `TechnicalDetails`. The Chinese primary summary
does not promote a mixed Chinese-prefix/English-sentence adapter message.

The initial exact catalog covers the repeated on-sky errors, including:

- `PHD2_NATIVE_GUIDE_GEOMETRY_REJECTED`;
- `PHD2_OFF_SLIT_NATIVE_SELECTION_EXHAUSTED`;
- `PHD2_SLIT_PLACEMENT_FAILED_SAFE` with display-only nested cause handling;
- `PHD2_PLACEMENT_SETTLE_STALE`;
- `PHD2_CALIBRATION_PRE_GUIDE_REJECTED`;
- `PHD2_RECALIBRATION_DID_NOT_BECOME_ACTIVE`;
- `G3_FRAME_REUSED`;
- `G3_CATALOG_WCS_AUTHORITY_INVALID`;
- `G3_CLOUD_OR_TRANSPARENCY_INVALID`;
- `G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED`;
- G3 frame/mount binding staleness/missing/unreadable/readback failures;
- `G3_PLATE_SOLVE_LADDER_TRANSIENT_EXHAUSTED`;
- bright-target annular-ghost/topology failures;
- `QHY_MOUNT_COORDINATE_SYNC_READBACK_FAILED`;
- `UVEX_NOT_READY` / pinned connection failures;
- `FINALIZE_INCOMPLETE`;
- `ATR_TIER_NOT_SELECTED`;
- safety, identity, hash, topology and budget families.

Unknown codes receive a conservative localized fallback and keep the full raw
message. Localization never decides whether a code is recoverable.

Notifications use the same presenter and show only stage, code, localized
summary and the instruction to open Diagnostics and evidence. Their fingerprint
still uses stable run/state/stage/code/raw identity, so changing language does not
create a second alert.

## Progress model

The dock now subscribes to the runner's real `IProgress<ApplicationStatus>`.
It presents two distinct values:

- overall completed stages / total stages and overall stage percentage;
- current operation text and an optional 0–100 operation percentage.

Unknown operation progress remains absent instead of fabricating a percentage.
NaN, infinity and out-of-range reports cannot reach the WPF bar. Stage changes
reset only the operation bar; overall progress does not move backwards. The ATR
calibration-library progress bar uses the same height, palette and explicit
percentage label.

## Newly closed recovery dead ends

### PHD2 native `find_star` returned no candidate

Previously, a successful JSON-RPC `find_star` call with a null result threw a
generic `Phd2Exception` and escaped on the first frame, even though geometry
rejections already had a four-frame bounded reselection loop.

The client now emits structured `Phd2NoGuideStarException` only when RPC itself
succeeded but returned no point. The placement runner treats only this exception
as one candidate rejection, saves/waits for a fresh looping FITS and calls
`find_star` again, up to four total attempts. RPC, protocol, connection and
malformed-point errors are not reclassified and do not inherit the retry. No
guide, runtime-lock or mount command is sent before a candidate is accepted.

### PHD2 lock failure already returned to durable origin

`PHD2_LOCK_FAILURE_RETURNED` is emitted only after fresh residual verification of
the durable origin, persistence of the settled lineage, and successful checked
stop. It now authorizes one existing dependency rebuild (fresh G3 → placement)
under the same session and durable budget. It cannot issue a new attempt/pixel/
elapsed-time budget, and the exact pair exhausts after one rebuild.

### Origin reached but first checked-stop readback failed

After a fresh origin proof, the runner now retries only the idempotent PHD2
checked-stop/readback once. It does not resend guide, lock-position or mount
commands. Two failures retain `PHD2_LOCK_ORIGIN_REACHED_STOP_UNCONFIRMED` as an
operator blocker.

## Deliberate hard stops and deferred structural work

The following remain hard stops: safety/weather/rain/horizon, identity/profile/
hash/topology drift, ambiguous physical position, unreturned lineage, exhausted
motion/return/time budgets, UVEX output state not verified, and a failed PHD2
calibration after the one existing forced cycle.

Two aggregate PHD2 paths still need a future structural cause-code change before
they can safely inherit inner recovery:

- `PHD2_RELOCK_G3_REACQUISITION_BLOCKED` currently embeds the inner G3 code in a
  message rather than a typed `FailedStage/CauseCode` result;
- generic `PHD2_GRADED_GUIDING_FAILED_SAFE` and
  `PHD2_SLIT_PLACEMENT_FAILED_SAFE` may contain transport, calibration, topology
  or stop failures with different recovery semantics.

They are intentionally **not** whitelisted by message parsing. Display-only
nested-code extraction does not authorize recovery.

## Offline verification

The verification set includes:

- exact Chinese/English stage, run-state, gate and frequent-code presentation;
- raw-message isolation and mixed-language leakage checks;
- reversible static XAML catalog coverage;
- production-template WPF screenshots for Chinese and English failures, narrow
  layout, running state and advanced settings;
- materialized English template visible-text assertion with no CJK leakage;
- PHD2 null-candidate vs RPC/connection/protocol distinction and four-attempt
  exhaustion;
- returned-origin dependency-rebuild bound and checked-stop-only retry source
  safety;
- existing automatic-recovery exact-pair and eight-attempt session-fuse tests;
- the repository's full .NET, design-baseline and Python reduction checks.

Final verification on 2026-08-30:

- frozen design baseline: 4 files verified;
- Release solution build: 0 warnings, 0 errors;
- .NET tests: 904 passed, 0 failed, 0 skipped;
- reduction Ruff: passed;
- reduction pytest: 66 passed;
- `git diff --check`: passed (only the existing line-ending notices);
- published and locally installed N.I.N.A. plugin artifact: `0.4.0.80`;
- all seven required installed DLL SHA-256 values match the artifact; the plugin
  DLL is `3352B8CBE5077AB2A86C0875B34EA966D86A4EBC79FBAF8F26C7CA301BD1685C`;
- N.I.N.A. remained stopped and all equipment remained untouched; load verification
  and live production-panel replay are still pending.
