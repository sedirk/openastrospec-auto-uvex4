# OpenAstroSpec Auto — UVEX4

OpenAstroSpec Auto — UVEX4 is the **acquisition and observatory-control product**. One
installation consists of several cooperating processes because each physical device
must have exactly one owner:

- the N.I.N.A. plugin owns the operator workflow and requests ATR585M exposures
  through N.I.N.A.;
- `UvexAdv.Service` exclusively owns the explicitly verified UVEX4 serial binding
  (ADR-0016); normal connection never scans ports;
- under ADR-0014, a separate photometry N.I.N.A. exclusively owns the photometry
  camera, filter wheel and focuser; the legacy `UvexAdv.Qhy.Service` must be
  released at an explicitly authorized idle boundary before migration;
- PHD2 owns G3M2210M, while the plugin communicates through PHD2's event API;
- `UvexAdv.Phd2.Watchdog` provides the independent guiding safety lease;
- `UvexAdv.Admin` is the standalone UVEX manager.

The recorded sky tests still used the legacy QHY service, not the native photometry
worker. Never run both owners against the same physical camera. See the
[dual-instance implementation](../../docs/dual-nina-implementation.md) and
[latest software/sky closeout](../../docs/closeout-2026-10-11.md) for deployment
and acceptance boundaries.

Installed **0.4.0.277** keeps standalone and native-sequence observations on one
production route. Acquisition/return, PHD2 slit convergence, ATR probing and science
accounting now own distinct decisions/state through finite ports to the existing
device owners. See the [workflow boundaries and remaining debt](../../docs/reports/2026-10-11-observation-workflow-boundaries.md).
Software/historical replay checks pass; the three live .277 panels still need visual
verification, and no new-version real-sky success is claimed.

## Human entry points

1. Open **OpenAstroSpec Auto — UVEX4 Manager** for UVEX4 status and bounded manual service operations.
2. In N.I.N.A., open **OpenAstroSpec 自动观测** for complete target observations.
3. At the top of that panel explicitly choose either:
   - **模拟演练（不连接任何真实设备）**, or
   - **真实设备控制（必须通过全部安全硬门）**.
4. The operation panel always shows the latest failed gate, its metrics, a plain-language
   recommendation, the relevant QHY/G3/ATR preview, and the evidence directory.

Selecting real mode alone performs no device operation. Physical work begins only
after the operator presses the start button and every immutable and live interlock
passes.

The [native spectroscopy sequence](../../docs/native-spectroscopy-night-sequence.md)
also provides explicit startup cards for safety monitoring and real mode, independent
target cards, reusable night-clock windows, and a terminal closeout choice. Saved
quality-failure skips require a checked stopped boundary. Ordinary keep-open means
protected standby, not disabled weather/dawn/deadline closure.

The **Observation plan → Acquisition plan (exposure budget)** subtab holds the
science-frame goal, attempt cap and probe ladder. Save or restore drafts before
starting; plan editing is locked during active/paused runs. See the
[field definitions and examples](../../docs/acquisition-plan.md). The saved
supervised slit-quality policy is visible in the run header and does not grant
real-device control authority.

**Automatic preparation → Main-mirror SEP focus** adds a separate, bounded focus
operation for irregular reflection-guide star shapes. It reuses N.I.N.A. focuser
motion/backlash and curve fitting while PHD2 remains the guide-camera owner.
The visible buttons and backend bridge share the same commands. This entry is included
in the current plugin; a repeatable improved main focus still needs separate sky
acceptance. Do not infer autofocus success from a software installation; see the
[focus setup and acceptance boundaries](../../docs/sep-main-mirror-focus-commissioning.md).

## Source boundary

The product is implemented by the C# projects under `src/` except
`UvexAdv.Reduction.Launcher`, with matching projects under `tests/`, plus the
equipment-facing scripts and configuration examples. The normative workflow is in
[`docs/observatory-automation-sop.md`](../../docs/observatory-automation-sop.md).

Build and publish from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

Publishing does not install or restart any component. Hardware installation remains an
explicit, elevated, separate operation.

## Release contents

A future GitHub release for this product should contain source plus separately named
artifacts for the N.I.N.A. plugin, Manager, UVEX service, QHY service, PHD2 watchdog,
and commissioning tool. Do not publish a vendor camera SDK until its redistribution
terms have been reviewed and recorded.
