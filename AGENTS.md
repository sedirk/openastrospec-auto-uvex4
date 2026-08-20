# UVEX-ADV repository rules

These rules apply to every change in this repository, including automated-agent work.

## Required reading

Before changing acquisition, equipment, sequence, guiding, camera, mount, or UVEX code, read:

1. `docs/design/observatory-automation-baseline.md`
2. `docs/adr/0001-single-owner-device-orchestration.md`
3. `docs/commissioning.md` when real hardware may be involved

The first two files are frozen design records. `scripts/verify-design-baseline.ps1` protects them with SHA-256 hashes.

## Frozen invariants

- N.I.N.A. is the sole owner of ATR585M.
- PHD2 is the sole owner of G3M2210M and must not be used as the QHYminiCam8M acquisition service.
- The planned QHY acquisition/photometry service is the sole owner of QHYminiCam8M.
- `UvexAdv.Service` is the sole owner of UVEX4 COM5 and must never scan other serial ports.
- Different physical cameras may operate concurrently; the prohibition is duplicate ownership of the same physical device.
- Raw observations are immutable inputs. Never rewrite, rename, move, or delete them as part of source-code work.
- Generated FITS, diagnostics, logs, databases, calibration libraries, SDK bundles, build artifacts, and `.astroproj` files do not belong in Git.
- Never commit credentials, RTSP URLs containing credentials, private network secrets, API keys, or machine-local configuration.

Do not edit a frozen design record or its hash manifest unless the user explicitly requests a design change. Normal implementation work must conform to it. A genuine architectural change requires a new superseding ADR, corresponding baseline update, and deliberate execution of `scripts/update-design-baseline-hash.ps1 -ConfirmFrozenDesignChange`.

## Hardware authorization

Source inspection, simulation, compilation, tests, and read-only status checks do not authorize equipment movement or acquisition. Unless the current user request explicitly authorizes the relevant action, do not:

- slew or pulse the mount;
- open, close, or move the roof/dome;
- move the UVEX grating, slit wheel, or M2;
- home an axis;
- start an exposure or calibration-library run;
- open a physical camera or COM5;
- update camera firmware or SDK installations;
- stop, restart, or reconfigure N.I.N.A., PHD2, DRIVER.UVEX4, or an installed Windows service.

Real-hardware work must preserve the per-device ownership table and follow staged commissioning: simulator, read-only connection, bounded manual action, shadow analysis, then closed loop.

## Repository boundaries

- `src/` and `tests/`: C#/.NET 8 acquisition, control, N.I.N.A. plugin, protocol, and spectroscopy-loop code.
- `reduction/`: independent Python 3.11 post-processing application. It must not open COM5 or physical cameras.
- `config/`: safe examples only. Effective machine configuration belongs under `%ProgramData%` or another ignored local file.
- `docs/`: durable design, commissioning, scientific reports, and SOPs.
- `output/`, `reduction/output/`, `artifacts/`, `tmp/`, `.dotnet/`, and virtual environments: local/generated and ignored.

Keep commits narrowly scoped. Add or update tests with behavior changes. Do not combine generated observing products with source changes.

## Checks

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-design-baseline.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1

cd .\reduction
.\.venv\Scripts\ruff.exe check src tests
.\.venv\Scripts\pytest.exe -q
```

The Python environment is deliberately pinned for ASPIRED/RASCAL compatibility. Do not upgrade individual scientific dependencies in place.
