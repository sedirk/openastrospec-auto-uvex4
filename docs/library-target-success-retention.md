# Successful-target frame retention

The optional target-success retention worker implements
[ADR-0053](adr/0053-successful-target-frame-retention.md). It is an offline
filesystem worker: it does not connect a camera, PHD2, mount, roof or UVEX device,
and it never starts or changes an observation.

## Deleted after a successful target

- QHY acquisition FITS used by the wide-field solve/acquisition route;
- G3 plate-solve, slit, target-confirmation and lock-residual FITS;
- ATR `PROBE` FITS that were not credited as an accepted science frame.

## Always retained

- every ATR science FITS and every probe reused/credited as science;
- QHY photometry FITS;
- calibration data, reduction products, `.astroproj` files, manifests, JSON/JSONL
  evidence and logs;
- any frame referenced by another run, model, project or operator pick/rating;
- any file whose hash, run identity, quality acceptance or filesystem state is
  incomplete or changed.

Only target runs that start after the durable enable watermark and later complete
with at least one verified accepted science FITS are eligible. Enabling the
policy never retrospectively selects older or currently running targets.

The worker checks the library every 15 minutes at below-normal process priority.
Every deletion batch needs a fresh complete index, dependency revalidation and an
exact plan. A durable intent/result journal is written outside registered data
roots; uncertainty defers that batch.

## Enable or disable

After the current observing run has ended:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\set-target-success-retention.ps1 -Mode Enable
```

Disable future batches without killing an in-progress exact-file batch:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\set-target-success-retention.ps1 -Mode Disable
```

Status is written to:

```text
%LOCALAPPDATA%\UVEX-ADV\library\target-success-retention-status.json
```

Previews and immutable deletion receipts are under
`%LOCALAPPDATA%\UVEX-ADV\library\cleanup-audit\`.
