# Spectroscopy library integration audit — 2026-10-24

## Scope and conclusion

Read-only inspection of the existing acquisition manifests, night journals,
reduction projects/products, and Athenaeum's public source. No acquisition,
device connection, raw-file mutation, deletion, movement, or archive was run.

The library can become a spectroscopy project manager by materializing the
provenance already produced by both applications. It should not create a second
reduction engine or infer a scientific project from directory names alone.

## Reference: Athenaeum

Useful interaction patterns are observing-night/session views, object-oriented
frame sets, equipment/calibration coverage, and visible product dependencies.
Athenaeum's [README](https://github.com/eg013ra1n/athenaeum/blob/main/README.md)
describes those capabilities. For this repository, source records are usually
stronger than generic coordinate clustering: explicit run/target/capture IDs
should win over an automatic nearest-coordinate grouping.

The confusing discard terminology must not be copied. In the reviewed
[Black Hole implementation](https://github.com/eg013ra1n/athenaeum/blob/main/crates/athenaeum-core/src/db/operations_blackhole.rs),
`add_to_black_hole` and the bulk equivalent insert catalog entries, whereas
`send_to_void` removes catalog records and invokes `std::fs::remove_file`.
The latter is permanent unlink, not a Windows Recycle Bin operation. The
[Black Hole screen](https://github.com/eg013ra1n/athenaeum/blob/main/src/pages/BlackHole.tsx)
uses separate restore/void actions. Our UI should state disk effects directly,
not require users to understand those metaphors.

## Existing contracts to reuse

| Record | Available relationship and facts | Existing implementation |
| --- | --- | --- |
| Observing-night journal | Ordered `outcomes`, `target`, `runId`, `ManifestPath`, `outcome`, `gate`, `failureScope`, `retainedScience`, original observing date and cutoff | `src/UvexAdv.Nina.Plugin/SequenceItems/UvexNightSequenceContainer.cs`, especially outcome publication and `SaveJournalAsync` |
| Acquisition run manifest | `observationRunId`, immutable plan/target/catalog identity, snapshot/stage, gates, counters, SHA-described evidence, terminal state/reason, revisioned journal | `src/UvexAdv.Observatory/ObservationRunJournalStore.cs` |
| ATR frame evidence | Exact FITS path/hash, capture ID, target, run, physical source role, effective science role, exposure, temperature, SNR/contrast, guiding/quality warnings | `src/UvexAdv.Nina.Plugin/AtrScienceWorkflow.cs`; `AtrFitsProvenance.cs` |
| QHY manifest and `frames.jsonl` | Explicit observation run, requested target, native frame ID, role, path/hash, start/midpoint/end timestamps, metrics, timing uncertainty | `src/UvexAdv.Qhy.Core/QhyModels.cs`; `QhyRunStore.cs` |
| Reduction run `*_run.json` | Target, exact science/bias/dark/flat/arc inputs, accepted/rejected science paths, trace and wavelength metrics, processing warnings, artifact paths | `reduction/src/uvex_reduce/pipeline.py`, `_write_run_manifest` |
| Full workflow | Explicit `standardRun`, `scienceRun`, optional flat-trial run, template, flat decision, accepted/rejected science, products and line-analysis summary | `reduction/src/uvex_reduce/workflow.py`, `run_full_workflow` |
| Calibration manifest | Science/standard products, standard name and template, source provenance, relative-response/normalization facts, second-order limitations, artifact paths | `reduction/src/uvex_reduce/calibration.py`, `write_calibration_products` |
| `.astroproj` | Existing operator project stage, approved stages, equipment preset, notes, parameters, output directory and last output | `reduction/src/uvex_reduce/project.py`, `AstroProject` |
| Exact library handoff | SHA-pinned selected frames with explicit target/role; missing or changed input never falls back to a directory scan | `reduction/src/uvex_reduce/library_selection.py`, `LibrarySelection` / `export_library_project` |
| Spectrum display | Established `SPECTRUM` reader keeps units, uncertainty and mask; relative/normalized products have a different column contract | `reduction/src/uvex_reduce/products.py`, `read_spectrum`; `library_preview.py` |

These contracts already permit: observing night → logical target session →
acquisition segments → unique raw science/probe/guide/photometry files → exact
reduction input selection → extraction/wavelength/response/line products.

## Representative historical evidence

Local paths below are inspection pointers only. They are not fixtures to copy
into Git and do not authorize scans of unrelated personal directories.

- Successful observing-night journal:
  `%LOCALAPPDATA%/UVEX-ADV/nights/night-20261021T182813455Z-f2f57b72d5eb4a8d81370fb747ad9d83.json`.
  The saved observing date is 2026-10-21. HD 18552, HD 21455, HD 21641 and
  HD 23552 are recorded as completed. HD 18552 has 2 accepted frames / 1200 s,
  including one reused final probe. Its run is
  `UVEX-20261021T182813Z-73b715bb6c9343b`.
- The HD 18552 run's `createdUtc` is 2026-10-22 02:28:13 +08:00, demonstrating
  why grouping at local midnight incorrectly splits a saved observing night.
  Its counters contain 2 accepted ATR frames, 155 QHY frames and 1 reused probe.
  Multiple evidence references to that probe describe one original file.
- Failed/aborted night:
  `%LOCALAPPDATA%/UVEX-ADV/nights/night-20261023T123550247Z-3aabaabb05a941b9b0f0ef5aeefc4e15.json`.
  It contains target outcomes with stage failure codes, zero-science targets,
  recovery decisions and retained original budgets. A project must remain
  visible when it contains no accepted spectrum: its acquisition diagnostics
  are meaningful project content.
- Historical extraction:
  `reduction/output/_internal/runs/standards/2026-02-21-regulus/Regulus_run.json`.
  The intended/reduced target is Regulus while the three source headers contain
  `OBJECT=NGC2392`; the existing pipeline records this conflict explicitly.
  A library that groups solely by FITS `OBJECT` would misfile this standard star.
- Historical full workflow:
  `reduction/output/_internal/runs/20260512-sharpcap/ngc6543-final/full_workflow.json`.
  Explicitly connects Vega, its reduction manifest, NGC6543's reduction manifest,
  seven accepted input spectra / 2700 s, response/calibrated spectra and line
  diagnostics. An earlier NGC2392 calibration is also present at
  `reduction/output/NGC2392/2026-02-21/metadata/calibration.json`.

Recent timestamps are not proof of real observing data. Existing .NET tests have
written simulated journals below the conventional production-looking roots.
Use explicit acquisition source metadata, and keep unknown source unknown.

## Implementable model and UI priorities

1. Keep the existing file index, reviews and links. Add a rebuildable project
   projection from indexed manifests rather than scanning/reopening every raw
   image at startup. One thread owns each SQLite connection.
2. Provide observing-night, target, acquisition-result and reduction-stage
   groupings over the same project membership; retain a normal Files view.
   Use the saved observing date when available; any fallback date rule must be
   labeled and must not substitute reduction creation time for observation time.
3. Preserve the logical target session's segment run IDs. Display accepted,
   attempted and retained frames independently of terminal success/failure.
   Do not sum duplicated `AcquisitionProgress`, `retainedScience` and run
   counters, or count a reused final probe twice.
4. Derive science-input membership only from exact run evidence / explicit
   science lists. A shared bias, dark, flat, standard or template is a dependency,
   not a reason to merge all its consumer projects into one object.
5. Present a project dossier: acquisition result/reason and warnings, accepted
   science/integration, probe ladder, guide/photometry evidence, calibration
   dependencies, `.astroproj`, extraction/wavelength/response products and log
   timeline. Existing `load_library_preview` remains the display adapter; do not
   rerun extraction merely to browse.
6. Keep all scientific claims specific: acquired ≠ accepted ≠ wavelength
   calibrated ≠ relative-flux calibrated ≠ absolute-flux calibrated. Preserve
   mask, pixel versus wavelength units, wavelength medium, second-order risk
   and flat-trial decisions; do not replace them with a single green success.
7. Create/open the existing `.astroproj` handoff with selected, SHA-pinned
   science and explicitly chosen calibration roles. Existing projects should
   reopen by their original path, without silently rewriting their selections.
8. Compute storage for unique original file IDs/paths, distinguish exclusive
   bytes from shared dependencies, and expose all file categories. An indexed
   byte count or marked-discard byte count is not immediately reclaimable space.

## Required discard and location semantics

Keep these as independent facts, not one overloaded status:

- Operator decision: unreviewed / kept / excluded **(catalog mark only)**.
- Acquisition/reduction quality: accepted / warning / rejected / unknown, with
  provenance and reason; do not overwrite the operator decision.
- Actual storage: present at original path / missing-or-offline / archived only
  when a verified archive record exists. Missing does not mean deleted and
  must never be labeled as sent to Recycle Bin without operation evidence.

Show a selectable absolute source path and a prominent **“在 Windows 文件管理器中定位”**
action on both file and project details. Existing `show_folder` merely opens
the parent of the first selected row; improve it to select the exact existing
file. For missing files, show the remembered path and optionally open the
nearest existing parent without implying restoration. Keep “copy path” and
manifest/project source locations available too.

At the time of this initial audit, the lifecycle module was deliberately dry-run-only. Continue to state
**“仅标记，原文件仍在原位置；未删除、未移入回收站”** and **“本次释放 0 B”**.
No archive destination has been selected. This audit neither authorizes a
deletion executor nor selects an archive disk.

### Subsequent implementation

The owner subsequently requested actual cleanup with preview and explicit confirmation.
The independent executor is now implemented under
[data-lifecycle design section 9](../design/observation-library-and-data-lifecycle.md#9-按项目清理预览明确确认逐项回执).
It distinguishes mark-only, native Windows recycling and permanent deletion, protects
raw FITS and scientific dependencies, and binds a single action button to the exact
reviewed plan. Archive execution remains unavailable. Historical-data acceptance was
preview-only; destructive regressions used isolated temporary synthetic files, not
observations. The original audit above records its earlier scope, not a claim that
the final application still lacks a cleanup executor.

## Regression cases

- Completed and failed targets from one night are both visible, correctly
  ordered and joined to their exact run/segment manifests.
- A failed target with an accepted retained frame still exposes that original.
- Midnight crossing keeps the saved observing-night date; missing date stays
  unknown rather than inheriting a reduction/scan date.
- Reused PROBE/SCIENCE evidence counts one file and one accepted exposure.
- Shared calibrations link multiple projects without merging their targets.
- Regulus-style header/manifest identity disagreement remains visible.
- `.astroproj` exact selection survives reopening; changed/missing input has
  no directory fallback and browsing never triggers processing.
- Marking/reverting exclusion changes only catalog review data. Raw size,
  path and SHA remain unchanged; no Recycle Bin or delete API is invoked.
- File-manager actions select the actual source path; missing files reveal a
  labeled remembered path and do not silently substitute another observation.
- Project/file/storage views retain mapped geometry/theme checks and actual
  screenshot review at the supported sizes/scalings.
