# Known issues

This is the public, operator-oriented issue register. It distinguishes usability defects
from intentional safety gates. Machine-local identifiers, private paths, and raw
observations do not belong here.

## Operator interface

| ID | Status | Issue and impact | Current handling |
|---|---|---|---|
| UX-001 | Fixed in source; deployment required | The automatic-observation dock required at least 920 px of horizontal space. In a normal narrow N.I.N.A. dock, mode controls, previews, and diagnostics appeared missing. | The dock is now a single vertical flow with no horizontal minimum. QHY, G3, and ATR views are tabs. Rebuild and reinstall the N.I.N.A. plugin to receive it. |
| UX-002 | Fixed in source; deployment required | Simulation/real selection was hidden inside an advanced expander while two unrelated start buttons remained visible. A disabled real button did not explain why it was disabled. | The first card now has two explicit, fully labelled mode selectors and one mode-aware start button. Selecting real mode performs no hardware action. Every real-mode blocker is shown as a bullet list. |
| UX-003 | Fixed in source; deployment required | Failures were recorded in gates/manifests, but the UI did not connect a failure to the relevant image, metrics, evidence, or next action. | A persistent failure-diagnosis card now shows stage, code, full message, numeric metrics, suggested QHY/G3/ATR view, failure evidence, latest evidence, and the run directory. Evidence is pushed to the dashboard as it is published. |
| UX-004 | Partially addressed | Existing live preview windows already supported zoom and pan, but their entry buttons could be off-screen and there was no historical thumbnail browser. | Entry buttons are now visible in each preview tab. The evidence tab and folder buttons expose the current run. A cross-run thumbnail browser remains future work. |
| UX-005 | Open | Several commissioning parameters remain technical because they are immutable safety/provenance records rather than routine controls. | Normal operators should import a reviewed `.bindings.json`. Manual fields remain under an explicitly labelled **Advanced** expander. A guided commissioning wizard remains future work. |

## Automation and commissioning

| ID | Status | Issue and impact | Current handling |
|---|---|---|---|
| OBS-001 | Intentional blocker | A real run cannot start without a valid Night Setup, commissioning preset, hardware fingerprint, PHD2 evidence, and live safety/identity gates. | The UI now lists every missing item. Do not convert these gates into confirmation dialogs or bypass switches. |
| OBS-002 | Fallback implemented; transfer schema pending | The GS350/QHY-to-C11/G3 field transfer is not a rigid constant and may change after thermal flexure, pier changes, or reassembly. | The current real runner requires explicit `Skip`, records `TransferSkipped`, then uses a fresh direct G3 solve and a configured bounded local star-field search with safe return. `AutoIfValidElseSkip` remains blocked until an independent versioned transfer record exists. Never reuse the distinct G3-pixel-to-mount transform as an optical-axis offset. |
| OBS-003 | Graded active-calibration path implemented; historical repository open | PHD2 calibration, focus, slit geometry, and transforms can expire or fail after mechanical/environmental changes. A single 10° orthogonality cutoff also rejected otherwise usable 11.7° evidence without considering settle, residual, identity or age. Production currently receives one active PHD2 calibration, not a historical candidate set. | Use the versioned [PHD2 calibration quality grades](phd2-calibration-quality-grades.md) to grade the single active calibration with exact profile/equipment/topology/pier, age, direction/rates/symmetry, operation-bound settle and fresh residual. UI/evidence explicitly say `single active evaluated`; `SelectBest` remains a tested algorithm API for a future integrity-checked history store. Degraded and direct-target guiding are explicit supervised-only modes; never use `Assume Orthogonal` to hide a bad calibration. |
| OBS-004 | Site integration incomplete | Full unattended qualification depends on trustworthy roof/dome, weather, safety-monitor, optical-cover, horizon, and clock inputs. | The current system remains supervised where a required source is absent. The operator's statement that the roof is open is not a reusable software safety attestation. |
| OBS-005 | Known observing limitation | Thin/high cloud can reduce detected stars without obvious saturation and invalidate focus/solve fits. | QHY/G3 gates now reject noise peaks and preserve raw frames. The failure card points to star count, FWHM, SNR, background, saturation, and the relevant preview. |
| OBS-006 | Implemented; commissioning values required | A normal QHY solve after catalog slew can be hundreds of arcseconds from the target, while G3/slit corrections must remain small. Reusing the fine-motion limit would reject valid coarse centering; simply widening it would make slit motion unsafe. | QHY coarse centering now has an independent schema-1 single/cumulative/attempt/time envelope in the action hash, manifest and UI. It can split a large WCS correction, requires a fresh QHY solve after every move, and reserves a bounded return to the saved origin. All values default to zero and block real mode until explicitly commissioned; the fine G3/slit gate remains unchanged. |
| OBS-007 | Supervised recovery limitation | QHY coarse-centering keeps its pending return state in the running N.I.N.A. process. A process or power loss after a coarse move therefore cannot prove the saved origin and remaining envelope from a durable journal. | Normal failures perform a bounded return from fresh reported coordinates. After a process/power loss, do not infer or replay a return: reconcile the mount position in N.I.N.A., reacquire a fresh QHY frame/WCS, and start a new supervised run. Durable cross-process recovery is implemented for G3 acquisition and slit-placement motion, but remains future work for the QHY coarse family. |

## Packaging and public release

| ID | Status | Issue and impact | Current handling |
|---|---|---|---|
| REL-001 | Current snapshot clean; reachable history not certified | The current tracked and non-ignored untracked source snapshot passes the strict public-release scan, but older reachable commits and refs have not been sanitized or certified for publication. | Run the strict public-release audit before every source bundle. Publish a reviewed clean snapshot or deliberately sanitize history before exposing the existing Git graph; do not claim that a clean working tree makes old commits safe. Immutable observation products remain ignored and must never be rewritten merely for source publication. |
| REL-002 | Open release blocker | Redistribution rights for vendor SDK/driver binaries are separate from OpenAstroSpec's GPL license. | Source releases must not include vendor binaries by default. Review and document each binary's redistribution terms before attaching a binary bundle. |
| REL-003 | Open release blocker | The full third-party dependency inventory and generated notices are not yet frozen for a tagged release. | `THIRD_PARTY_NOTICES.md` defines the boundary; generate and review the exact NuGet/Python/application license inventory before the first public binary release. |
| REL-004 | Known CI limitation | Hosted CI does not have N.I.N.A. 3.2 assemblies, so it cannot compile/load-test the N.I.N.A. plugin. | Hardware-independent projects and Spectral Studio run in hosted CI. The plugin requires a Windows release check on a machine with the supported N.I.N.A. version. |
| REL-005 | Open | Public installers are not code-signed and the two GitHub release bundles have not been automated. | Keep source builds reproducible first. Add signing and release packaging only after license/privacy audits pass. |

## Reporting a new issue

Include the product name, version/commit, run ID, exact quality-gate code, and sanitized
manifest/evidence references. Never attach credentials, private network URLs, raw
observations without permission, or a machine-local production configuration.
