# Terminal-target frame retention

The background retention worker implements
[ADR-0055](adr/0055-terminal-target-disposable-frame-retention.md). It is an
offline filesystem worker and never connects or controls N.I.N.A., a camera,
PHD2, the mount, the roof or UVEX.

After a target run and its immutable manifest both explicitly reach a terminal
state, the worker may permanently delete:

- QHY acquisition and continuous wide-field solve/tracking FITS, including their
  paired preview PNGs;
- G3 plate-solve, slit-placement, target-confirmation and lock-residual FITS;
- ATR `PROBE` FITS that were not credited or reused as accepted science.

`Completed`, `Faulted`, `Failed` and `Cancelled` target outcomes share the same
disposable-frame policy. Active, paused, taken-over, unknown and incomplete runs
are never selected.

Always retained: ATR science, the credited final trial, QHY `PHOTOMETRY` science
frames, calibration, reduction products, `.astroproj` files, manifests, frame
indexes, JSON/JSONL evidence, logs, operator picks, and every referenced
model/project dependency.

The activation watermark is forward-only. The worker uses fresh scans,
hash-bound exact-file plans, dependency revalidation, Windows locked-handle
permanent deletion and per-file receipts.

### Production verification and permissions

- For continuous QHY frames, the FITS `OBJECT` can append the literal
  ` / continuous acquisition witness`. Treat only that exact target warning
  as a presentation difference after the terminal job, complete frame index,
  run/job/frame IDs, FITS role, original target and SHA-256 agree. A QHY job
  manifest contains a rolling tail; absence from that tail is not absence from
  its complete frame index. Other warnings still protect the file.
- The target's own manifest `evidencePath` records an output. The same key in
  a model, reduction project or another run is not a deletion permit. A
  credited ATR capture remains protected even if it has multiple paths.
- Native control records can be retired by rename. An absent live record is
  accepted only for the known retirement types with a matching archive and
  receipt that records the archive's SHA-256; this is not a general exception for
  missing metadata. For a per-target guide ledger the archived final revision
  may be newer than the last scan: its file references must still be present
  in the index and are explicitly protected. The absent path, archive and
  receipt are guarded throughout deletion. No observatory state is changed.
- The ordinary worker does not elevate. Files it cannot open for deletion
  are reported via `access_denied_count` and `first_denied_path`, rather than
  starving unrelated eligible files. This is not a deletion receipt.
  To repair an approved batch, `scripts/grant-qhy-terminal-file-delete.ps1`
  takes the reviewed JSON list of `path`, `size`, `sha256`, its independent
  SHA-256 and the interactive user's SID. Only matching, single-link QHY
  acquisition FITS below the QHY runs root get a file-only DELETE ACE. It
  cannot grant permissions recursively or on science, previews, or directories.
  Re-run the normal unprivileged checked deletion; do not run the entire
  library with administrator rights. New service files do not inherit this ACE.

## Enable or disable

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\set-terminal-target-retention.ps1 -Mode Enable
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\set-terminal-target-retention.ps1 -Mode Disable
```

The enabled worker checks every five minutes at below-normal priority. Disable
removes future scheduling but does not interrupt an exact-file batch already
being audited.

Status:

```text
%LOCALAPPDATA%\UVEX-ADV\library\terminal-target-retention-status.json
```
