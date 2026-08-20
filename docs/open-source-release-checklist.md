# Open-source release checklist

OpenAstroSpec — UVEX4 is licensed `GPL-3.0-only` as one repository containing two products. Adding
`LICENSE` is necessary but not sufficient for a safe public release.

## 1. Source and privacy audit

- [ ] `git status --short` is clean and the intended tag is signed or otherwise recorded.
- [ ] Search all tracked text for user names, absolute home paths, machine GUIDs, stable
      USB/camera IDs, private IPs, RTSP URLs, API keys, and credentials.
- [ ] Convert machine configuration to documented examples with placeholders; keep
      effective values under `%ProgramData%` or ignored `*.local.*` files.
- [ ] Confirm that `output/`, `reduction/output/`, raw FITS, databases, logs, `.astroproj`,
      SDK bundles, `artifacts/`, and virtual environments are absent from Git history and
      release assets.
- [ ] Review historical scientific reports separately: publish only data, images, and
      catalog material for which the project has permission and provenance.
- [ ] Audit every reachable Git commit and release ref separately. The working-tree
      audit below covers only cached and non-ignored untracked files; a clean result
      does not prove that earlier commits are clean.
- [ ] Do not rewrite Git history as part of routine release cleanup. If historical
      credentials or private identifiers are found, stop publication, rotate/revoke
      affected credentials first, and obtain an explicit maintainer-approved incident
      remediation plan before any history operation.

Suggested local audit (review every result; do not blindly rewrite evidence):

```powershell
git grep -n -I -E 'C:\\Users\\|/home/|192\.168\.|10\.[0-9]+\.|rtsp://|api[_-]?key|password|usb#vid_|QHYminiCam8M-[0-9a-f]+'
git ls-files | Select-String -Pattern '\.(fit|fits|fts|db|sqlite|log|dll|exe|zip)$'
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\audit-public-release.ps1 -Strict
```

## 2. Licensing

- [ ] Confirm every contributor/copyright holder agrees that the original project
      source is conveyed under `GPL-3.0-only`.
- [ ] Generate an exact NuGet and Python dependency/license inventory for the tag.
- [ ] Review N.I.N.A., PHD2, ASCOM drivers, scientific catalogs/templates, icons/fonts,
      and every bundled native SDK under their upstream terms.
- [ ] Do not claim that observation data or third-party binaries are GPL merely because
      they are used by the software.
- [ ] Do not distribute a QHY/ToupTek/vendor SDK binary until redistribution permission
      has been verified and recorded.

## 3. Product verification

### OpenAstroSpec Auto — UVEX4

- [ ] Frozen design hashes pass.
- [ ] Full `.NET` solution build and tests pass on the supported Windows/N.I.N.A. machine.
- [ ] Simulator-only smoke test confirms that choosing simulation opens no hardware.
- [ ] N.I.N.A. template loads in a narrow dock and all buttons/switches have visible labels.
- [ ] Failure injection for QHY solve, G3 focus/slit/guide, ATR quality, and manifest writes
      shows a reason, metrics, preview choice, and evidence directory.
- [ ] Installer preflight is tested without altering installed services.

### OpenAstroSpec Spectral Studio — UVEX4

- [ ] Pinned Python 3.11 environment passes `ruff`, `pytest`, and `pip check`.
- [ ] A curated, redistributable FITS fixture exercises neutral 2D preview, zoom/pan,
      extraction, 1D plot, and failure diagnostics without touching source data.
- [ ] The launcher works on a clean user account and reports a missing Python environment
      in human-readable language.

## 4. GitHub presentation

- [ ] Root README presents exactly two products and links each product definition.
- [ ] `LICENSE`, `THIRD_PARTY_NOTICES.md`, `CONTRIBUTING.md`, `SECURITY.md`, known issues,
      and hardware-safety warnings are visible.
- [ ] Repository topics/descriptions do not imply generic unattended safety certification.
- [ ] Release notes separate source, Observatory artifacts, and Spectral Studio artifacts.
- [ ] Checksums and source commit are included for every published binary.
