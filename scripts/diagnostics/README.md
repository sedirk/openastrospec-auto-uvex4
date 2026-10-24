# Saved G3 sparse-field experiments

`replay_sparse_g3.py` only reads saved FITS. It has no hardware, service,
observation-control or network client. Run it in the existing star-detection
environment; do not add SEP to the independently pinned reduction environment.
All generated FITS and reports belong in ignored `tmp/` or `output/`, never Git.

Required common arguments are `--observations <immutable observation root>`,
`--output <new experiment directory>`, and one or more `--source <saved FITS>`.
An existing output directory, an output inside the observation tree, or an output
inside a source directory is rejected. Source SHA-256 is checked again at exit.

Modes:

- `extract`: local-background SEP positions and image statistics.
- `register`: same-camera, orientation-preserving triangle registration. Exactly
  three spread-out stars fit the final similarity; other stars are held out.
  Scale/rotation limits are checked again after that refit. Equally supported
  but incompatible transforms (for example a repeated stellar pattern) are
  rejected as ambiguous instead of choosing the first triangle.
  The default requires three held-out stars within 2.5 pixels. Experiments with
  only two can explicitly request `--minimum-holdout 2`; this is diagnostic,
  not a new production identity policy. A fitting triangle cannot validate itself.
  `--exclude-roi x y width height` excludes a target from every matching step.
  `--target-point x y` propagates that held-out coordinate without fitting it.
- `stack`: a derived mean at the first source geometry, only after all field
  registrations pass. Missing sky coverage is not invented.
- `mosaic`: a derived union of independently registered overlapping fields.
  Failed/absent overlap emits a refusal report and no FITS mosaic.
- `solve`: copies PL3 executable, library and configuration to an isolated output
  directory, creates a disposable hint/binning input and invokes the saved local
  catalogue configuration. It needs `--solver-dir`, `--ra`, `--dec`, `--scale`
  (detector arcseconds/pixel) and optional `--binning` (default 2). `--normalize`
  is explicitly labelled background-normalized diagnostic input, not raw data.
  Executable/config hashes, exact command, bounded timeout, native result and
  installed-file immutability are saved in `report.json`.
  Native success requires exit code zero, no timeout and the native `True`
  result together. Once a safe new output directory exists, exceptions retain
  a failure receipt and recheck every requested source; the original exception
  remains an error, even if a later verification or receipt write also fails.

Every report also records the script SHA-256, Python executable/version and SEP
version. The reduction environment is not modified or used as the SEP backend.
PL3 uses the catalogue directories referenced by the copied original
configuration; this tool does not hash every catalogue data file. Results are
repeatable against that saved local configuration, not a claim of byte-for-byte
reproducibility if the external star catalogue changes.

Example shape (substitute actual local saved files and solver location):

```powershell
$obs = Join-Path $env:LOCALAPPDATA 'UVEX-ADV\observations'
$python = '.\output\star-detection-venv\Scripts\python.exe'
& $python .\scripts\diagnostics\replay_sparse_g3.py register `
  --observations $obs --output .\tmp\new-registration `
  --source $referenceFits --source $newFits `
  --exclude-roi 900 350 150 150 --target-point 970.6 420.7 `
  --minimum-holdout 2
& $python .\scripts\diagnostics\replay_sparse_g3.py solve `
  --observations $obs --output .\tmp\new-solve --source $newFits `
  --solver-dir $localPl3Directory --ra 351.095 --dec 21.401 `
  --scale 0.38285 --binning 2
& $python -m unittest discover -s .\scripts\diagnostics -p 'test_*.py'
```

This is offline optical evidence, not a replay of a physical closed loop. A
registered bright core is not automatically the catalogue object. A derived
stack is not an additional exposure, and a failed solve is retained honestly.
It does not authorize mount corrections, import old target positions into a live
run, weaken morphology/companion checks, or replenish any observing budget.

## 2026-10-24 whole-chain audit

The audit scripts are offline diagnostics, not alternate production controllers.
The findings and evidence limitations are recorded in
[`wide-narrow-fusion-audit.md`](../../docs/reports/2026-10-24-wide-narrow-fusion-audit.md).
All outputs below must be new, ignored paths; source observations stay immutable.

```powershell
$python = '.\output\star-detection-venv\Scripts\python.exe'
$data = Join-Path $env:LOCALAPPDATA 'UVEX-ADV'
& $python scripts/diagnostics/audit_observation_corpus.py `
  --observations (Join-Path $data 'observations') --output tmp/new-corpus-audit
& $python scripts/diagnostics/audit_dual_reachability.py `
  --data-root $data --output tmp/new-dual-audit --verify-source-hashes `
  --corpus tmp/new-corpus-audit/corpus.json
& $python scripts/diagnostics/audit_dual_photometry_inventory.py `
  --data-root C:\ProgramData\UVEX-ADV\qhy\data --output tmp/new-photometry-inventory.json
& $python scripts/diagnostics/audit_dual_stream_tracking.py `
  --inventory tmp/new-photometry-inventory.json --output tmp/new-stream-audit --frames 20
& $python -m unittest discover -s scripts/diagnostics -p '*test*.py'
& $python -m unittest discover -s scripts/diagnostics/tests -p 'test_*.py'
```

`audit_dual_production_math.ps1` additionally calls the already-built, pure
Observatory policy assembly. Run it under PowerShell 7 with explicit
`-DataRoot`, `-ObservatoryAssembly` and a new `-OutputPath`. Its future second
image and zero-drift scenarios are labelled **counterfactual**, not acquired
observations or a recovered-target success rate. The census counts real-labelled
run segments, not unique physical targets. See
[`audit_tracking_README.md`](audit_tracking_README.md) for the five G3 pixel cases,
exact C# registration adapter and the distinction between signal, position and
identity. Stream registration uses existing **guided-science** QHY frames, not
previously missing acquisition-stage exposures; residuals are in QHY pixels.

No script installs dependencies, uses a device SDK, connects to equipment,
changes the active camera owner, or imports diagnostic models into production.
