# UVEX-ADV repository rules

These rules apply to every change in this repository, including automated-agent work.

## Required reading

Before changing acquisition, equipment, sequence, guiding, camera, mount, or UVEX code, read:

1. `docs/design/observatory-automation-baseline.md`
2. `docs/adr/0001-single-owner-device-orchestration.md`
3. `docs/commissioning.md` when real hardware may be involved

The first two files are frozen design records. `scripts/verify-design-baseline.ps1` protects them with SHA-256 hashes.

## Frozen invariants

- The spectroscopy/master N.I.N.A. instance is the sole owner of ATR585M and coordinates all shared observatory equipment.
- PHD2 is the sole owner of G3M2210M and must not be used as the QHYminiCam8M acquisition service.
- Under ADR-0014, a separate photometry/worker N.I.N.A. instance is the sole production owner of QHYminiCam8M, its photometry filter wheel and GS350 photometry focuser. Its Profile and plugin control surface must exclude all other device categories; future photometry accessories require explicit, bounded integration, not generic device-command access.
- The legacy QHY service remains for simulation, historical compatibility and explicit idle-boundary rollback; it must be stopped/released before the worker can connect. Never hot-switch owners within an active run.
- Under ADR-0016, `UvexAdv.Service` is the sole owner of the explicitly bound UVEX4 serial device. Normal connection/recovery never scans ports. An explicitly authorized, bounded, read-only maintenance identification may inspect present, unreserved candidates while the installed service and vendor owner are stopped; it never probes roof/mount/cover/switch ports or grants motion authority.
- Different physical cameras may operate concurrently; the prohibition is duplicate ownership of the same physical device.
- Raw observations are immutable inputs. Never rewrite, rename, move, or delete them as part of source-code work.
- Generated FITS, diagnostics, logs, databases, calibration libraries, SDK bundles, build artifacts, and `.astroproj` files do not belong in Git.
- Never commit credentials, RTSP URLs containing credentials, private network secrets, API keys, or machine-local configuration.

Do not edit a frozen design record or its hash manifest unless the user explicitly requests a design change. Normal implementation work must conform to it. A genuine architectural change requires a new superseding ADR, corresponding baseline update, and deliberate execution of `scripts/update-design-baseline-hash.ps1 -ConfirmFrozenDesignChange`.

## Hardware authorization

### Exploration-first development (owner, 2026-10-11)

The current goal is to explore equipment capability and try useful observing
strategies, not to guarantee perfect spectra or absolute astrometric certainty
before every experiment. Prefer obtaining and honestly labelling imperfect data
over abandoning a target because a preferred quality metric was missed.

- Do not add a blocker merely because confidence, centering, morphology, SNR or
  a heuristic is below its preferred value. Prefer a visible warning, a bounded
  alternative and retained quality/provenance flags. Review existing blockers
  for the same distinction when touching that workflow.
- When the target field cannot solve, use available neighbouring-field WCS,
  measured mount displacement, fresh pixel measurements and applicable local
  wide-field/slit-camera pairs before repeating a full solve ladder. Keep
  inferred positions distinct from measurements; do not demand another whole-
  frame solve solely to rediscover a position already supported by continuity.
- A target acquisition/quality budget failure with a confirmed stopped boundary
  should follow the saved skip-target policy, not terminate the entire night.
  Test that the next native target actually executes, not just that a classifier
  returns true. Do not silently reset the failed target's movement budget.
- Every new hard stop needs a specific adverse next action and a tested recovery
  or skip route. An uncertain scientific result is not by itself a hardware
  hazard. It must not be disguised as a perfect/verified result either.
- This preference does not waive actual weather danger, electronic/mechanical
  limits, exclusive device ownership, operator cancellation or an unconfirmed
  physical stop. Equipment and weather still have real risks; preserve these
  independently rather than using them to justify unrelated quality blockers.

Architectural relaxations still require an explicit superseding ADR and the
deliberate frozen-baseline update described above. This section records that the
owner has requested the neighbour-inference/continuity change in ADR-0030.

### Standing authorization: reported observation blockers (owner, 2026-09-14)

When the owner reports another blocker in this project's N.I.N.A. observation or
focus workflow, including by sending a failure screenshot, treat it as a request
to **fix the software, verify it, install the exact plugin artifact and restart
N.I.N.A.** Do not stop after diagnosis or repeatedly ask whether to implement,
install or restart. This standing authorization remains effective until the owner
revokes it or gives a conflicting instruction. It is the narrow exception for
N.I.N.A. installation/restart to the explicit-current-request rule below.

- Read the evidence needed to make a sound fix, preserve raw observations and
  existing changes, and run focused regressions plus the required release checks.
  "Directly fix" does not mean guessing at the cause or removing identity/safety
  checks merely to make a run pass.
- Before deployment, use the existing frontend/backend cancellation boundary to
  stop the blocked run without starting mechanical recovery, confirm acquisition
  and device motion are idle, back up Profile and plugin, then exit N.I.N.A.
  normally. Install, restart, verify the loaded version and actual plugin panels,
  and report the result. Do not force-kill a busy or unconfirmed device owner;
  report that exceptional blocker if normal safe shutdown cannot be confirmed.
- This authorizes neither a new observation/exposure nor a slew, home, focus move,
  roof/cover movement, automatic resume, firmware update or unrelated service/
  PHD2 restart. Those still require a separate applicable observing or maintenance
  authorization. Do not assume restart means continuing an old run.
- Do not turn replay or installation checks into a claim of real-sky success.

Source inspection, simulation, compilation, tests, and read-only status checks do not authorize equipment movement or acquisition. Except for the standing N.I.N.A. blocker-maintenance authorization above, unless the current user request explicitly authorizes the relevant action, do not:

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

## Prefer non-UI tools

- Avoid `computer-use` whenever a reliable alternative exists: prefer repository scripts, purpose-built APIs, CLIs, structured status/evidence and logs for inspection, changes and verification. Plugin installation and panel acceptance do not require `computer-use`: use the supported frontend bridge, exact artifact/version checks, runtime panel state, startup/binding logs and offline UI regressions first. Use `computer-use` or operator visual review only when a specific visual issue cannot be verified that way, and keep it minimal. Report unverified visual coverage honestly; this does not relax hardware authorization, device ownership or normal safe shutdown.

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
