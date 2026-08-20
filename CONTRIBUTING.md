# Contributing to OpenAstroSpec Auto — UVEX4

OpenAstroSpec Auto — UVEX4 is released under `GPL-3.0-only`. By submitting a contribution, you represent
that you have the right to submit it and agree that it is distributed under the same
license. Do not submit third-party code, images, scientific data, SDK binaries, or
templates unless their provenance and compatible redistribution terms are documented.

## Workflow

1. Start from a clean `main` branch and create a focused branch such as `codex/acquisition-shadow-mode`.
2. Read `AGENTS.md` and the frozen observatory automation baseline before touching equipment-facing code.
3. Keep hardware-independent logic testable with simulators, recorded responses, synthetic images, or deliberately curated fixtures.
4. Run the repository checks before committing.
5. Use a concise commit subject such as `docs: freeze acquisition architecture` or `feat(plugin): add QHY shadow acquisition state`.
6. Do not commit raw observations, calibration libraries, generated reports, build products, machine-local paths required only on this observatory PC, or credentials.
7. Keep the two product boundaries explicit: Observatory may contain hardware-facing
   code under the ownership rules; Spectral Studio must remain offline-only.

## Frozen design changes

`docs/design/observatory-automation-baseline.md` and ADR-0001 define the accepted acquisition architecture. Ordinary refactoring cannot change their meaning.

If the owner explicitly changes the architecture:

1. Add a new ADR under `docs/adr/` that supersedes the affected decision.
2. Update the canonical baseline and any acceptance criteria.
3. Review safety, device ownership, data provenance, rollback, and commissioning consequences.
4. Run:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\update-design-baseline-hash.ps1 -ConfirmFrozenDesignChange
   ```

5. Commit the ADR, baseline, hash update, and related tests together with an explicit `design:` commit subject.

Changing the hash merely acknowledges an intentional design change; it is not a substitute for review.

## Data and test fixtures

Small FITS fixtures may be added only under `tests/fixtures/` or `reduction/tests/fixtures/`. Document their source, license/permission, expected checksum, and any privacy-sensitive Header removal. Prefer deterministic synthetic arrays where possible.

Never modify observational source files in place. A reduction or repair operation must write a new product and preserve input checksums and provenance.

## Pull-request checklist

- [ ] The change conforms to the frozen design baseline or includes an explicitly approved superseding ADR.
- [ ] No physical device is opened by a second owner.
- [ ] Hardware actions remain bounded, cancellable, logged, and safe after process failure.
- [ ] New configuration fields have explicit units and safe defaults.
- [ ] Tests cover success, timeout, cancellation, stale state, and rollback where applicable.
- [ ] No raw data, generated products, logs, secrets, or local runtime state are staged.
- [ ] Documentation distinguishes current behavior from planned behavior.
- [ ] New source and assets are compatible with `GPL-3.0-only`, and required third-party notices are included.
- [ ] No machine-local identity, absolute home path, private network location, or vendor SDK binary is introduced.
