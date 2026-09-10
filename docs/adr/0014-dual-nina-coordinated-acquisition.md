# ADR-0014: Dual N.I.N.A. acquisition with one observatory coordinator

- Status: Accepted design target; the operator explicitly requested program implementation and the photometry-only device boundary on 2026-09-07.
- Date: 2026-09-07
- Activation: Source integration and offline validation do not authorize installation, connection or hardware commissioning.
- Supersession: The fixed QHY-service ownership assignment in
  ADR-0001 and the QHY-owner/deployment-specific clauses of ADR-0011. All other
  single-owner, immutable-evidence, motion, and commissioning requirements remain.
- Current effect: The accepted source/design target and repository ownership rules are updated. Existing installed configurations require an explicit idle-boundary migration; no automatic deployment or ownership switch is performed.

## Context

The operator previously used two N.I.N.A. instances with separate profiles, one
for ATR spectroscopy and one for QHY photometry. The
[2026-08-25/26 commissioning record](../qhy-photometry-commissioning-2026-08-25.md)
documents a manually connected second N.I.N.A. and API-driven QHY acquisition,
filter, and focus operations. This is component evidence, not proof of automatic
cross-instance coordination, meridian-flip recovery, or unattended operation.

The current production architecture assigns QHY acquisition to a dedicated
service. The selected next direction instead reuses a separate N.I.N.A. profile's
native acquisition, saving, filter management, and reviewed Advanced Sequencer
template. A small coordination adapter may still be necessary; two independent
whole-observatory schedulers would introduce conflicting control of shared gear.

Neither process isolation nor the historical manual run proves SDK reliability,
timing accuracy, or photometric calibration. The historical filter-identity
correction and withdrawn H-alpha conclusions remain unchanged.

## Decision

1. Use a spectroscopy N.I.N.A. instance as `SpectroscopyMaster`, owning ATR585M,
   and a separate-profile N.I.N.A. instance as `PhotometryWorker`, owning
   QHYminiCam8M, its verified filter-wheel route, and the GS350/ToupTek focuser.
   PHD2 remains the owner of G3M2210M; `UvexAdv.Service` remains the sole owner
   of UVEX4 COM5. Preserve the three focus domains in
   [ADR-0003](0003-slit-illumination-and-focus-domains.md).
2. Keep one OpenAstroSpec observatory coordinator in the master. Target order,
   acquisition, slit placement, shared mount movement, guiding lifecycle, and
   site-mode safety remain master-coordinated. The worker has no independent
   mount, guiding, roof, cover, UVEX, or whole-night scheduling authority.
3. Route all production QHY exposures through the worker's native N.I.N.A.
   acquisition/save pipeline, including wide-field acquisition witnesses and
   same-pointing QHY/G3 pairs, not only the final photometry block. Preserve
   [ADR-0012](0012-qhy-wcs-mount-coordinate-authority.md): a worker WCS result
   does not itself authorize movement or Sync. Do not hand the camera between
   the service and N.I.N.A. halfway through a run.
   Spectroscopy acquisition takes priority over optional photometry: pause at
   the native frame/save/focus boundary, confirm retirement, then refresh master
   action gates before a witness. Record the interruption and resume photometry
   only after master-controlled recovery. Never override operator intervention
   or steal an unrelated run's job.
4. Execute a reviewed worker Advanced Sequencer template via a versioned
   adapter. Bind commands to both process sessions, exact profiles, physical
   device identities, parent run/child job IDs, configuration/template hashes,
   and revisions. Idempotency includes uncertain native command outcomes;
   query evidence before retrying a mutation. Reuse visible native commands and
   the shared production route, without computer-use automation or a hidden
   second implementation of the complete observation workflow.
5. Require locally bounded worker permission to initiate exposures, including
   nested loops, autofocus, and filter probes. Master loss must eventually stop
   new exposures even if the native template still has iterations remaining.
   Prove actual API/plugin interception capability before claiming unattended
   operation. Heartbeats do not prove roof safety or grant the worker shared
   equipment authority; master failure still requires the commissioned
   independent site safeguards and explicit supervision mode.
6. Treat pause, cancel, flip, and finalization as evidenced state transitions,
   not successful RPC receipts. Manual intervention takes precedence. Preserve
   motion obligations and budgets across restarts, resolve old-side obligations
   before a normal flip, and reacquire new-side evidence. Preserve the authorized
   supervised-quality policy in
   [ADR-0013](0013-supervised-slit-quality-warning-probe.md); do not turn routine
   photometry-quality warnings into automatic whole-observatory shutdowns.
7. Keep native raw files immutable. Bind actual capture IDs, sourced exposure
   times and uncertainties, filter identities, hashes, and one-to-many
   spectroscopy/photometry interval associations to the common run. Acquisition
   completion, scientific acceptance, and site cleanup are separate outcomes.
8. Migrate only at an authorized idle boundary: back up profiles/configuration,
   stop and close the old QHY owner, prove handle release, prevent its automatic
   reconnection, then connect and commission the worker. Rollback requires the
   reverse exclusive handoff. Do not implement automatic owner failover or hot
   switching after an in-run worker failure.

## Driver and SDK consequences

[ADR-0011](0011-qhy-allinone-shared-sdk-installation.md) describes the current
service's direct use of the official QHY AllInOne SDK. A chosen N.I.N.A. native
driver or ASCOM path may have a different SDK loading and deployment chain.
Acceptance must explicitly settle that difference instead of claiming the old
service-specific deployment rule already covers the worker.

Record and verify the selected driver route, actual loaded DLL source/version/
hash, Windows driver, and readable firmware identity. Preserve official-package
provenance and reproducibility. This proposal does not authorize copying private
SDK DLLs into N.I.N.A., combining arbitrary SDK versions, or changing firmware.
Changing acquisition software does not prove that an earlier USB/SDK fault has
been resolved.

## Alternatives and consequences

- **Keep the dedicated QHY service:** remains the valid production route until
  migration. It retains existing commissioning but requires more bespoke work
  to match the operator's native N.I.N.A. photometry-sequence workflow.
- **Run two independent whole-observatory sequences:** rejected for this plan;
  independent dithering, centering, guiding, flipping, or cleanup can conflict
  with slit spectroscopy on the shared mount.
- **Switch one instance's primary camera or hot-transfer QHY ownership:** rejected
  for the initial design because it undermines stable parallel acquisition and
  makes recovery and device responsibility ambiguous.

The selected design reuses familiar per-camera sequence tools, but adds explicit
profile/endpoint isolation, recursive template validation, cross-process state
coordination, and a local worker stop mechanism. It is not equivalent to merely
starting two windows. Native API hooks and driver behavior must be verified on
the chosen versions rather than assumed from this proposal.

## Acceptance and rollout

The first source implementation is documented in
[dual-N.I.N.A. implementation and configuration](../dual-nina-implementation.md).
It uses a current-user named pipe, the existing QHY job coordinator with a native
N.I.N.A. camera/save adapter, and one restricted receiver item in reviewed native
sequential containers. It rejects all other leaves, conditions and triggers;
arbitrary templates and autofocus loops are not implemented capabilities.
The master's synchronized-photometry switch is frozen per run and does not disable
acquisition witnesses. Source/offline delivery is separate from real commissioning.

The detailed contract, fault matrix, template migration, and staged acceptance
criteria are in the
[dual-N.I.N.A. coupling roadmap](../nina-advanced-sequencer-coupling-roadmap.md).
Start with contract/simulator tests, then authorized read-only connections and
bounded actions, then the real shared frontend route. Certify single-target
parallel capture, flips, multi-target sequencing, and unattended operation
separately. Neither the historical manual dual-instance run nor a successful
current-service run certifies this worker route.

The implementation change updates the canonical baseline and repository ownership
instructions and deliberately acknowledges those changes with:

```powershell
.\scripts\update-design-baseline-hash.ps1 -ConfirmFrozenDesignChange
```

Do not rewrite frozen historical ADRs to imply that the worker was always the
owner. Design acceptance does not waive the explicit handoff, driver verification,
hardware authorization, or staged commissioning before production migration.
