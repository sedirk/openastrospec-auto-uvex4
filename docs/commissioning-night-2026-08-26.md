# 2026-08-26/27 software and real-sky commissioning closeout

## Outcome

This night did not produce a new accepted end-to-end observation. The run was
ended after the QHYminiCam8M disappeared from Windows USB enumeration during an
abnormally long, all-zero acquisition. Later maintenance found no obvious QHY
cable or connector defect, so the evidence does **not** establish that a cable
physically fell out. Hardware closeout remained operator/maintenance work and
was deliberately not attempted by the software closeout.

The night was nevertheless a substantial software commissioning pass. It
aligned the installed N.I.N.A. front end with the G3-first acquisition route
that had already completed on sky, removed several routine setup blockers,
corrected the QHY filter identity, improved live diagnostics, fixed two QHY
service defects, and repaired the public CI audit. The release candidate is
`OpenAstroSpec Auto — UVEX4 0.4.0.28`.

The preceding successful Mirfak/Algol and M76 runs remain the evidence for
unattended slit acquisition and science capture. Nothing in this closeout
relabels the interrupted 2026-08-26/27 attempt as a completed observation.

## Software changes closed during the night

### Operator preparation and routine setup

- The target-planning page scrolls again in an ordinary N.I.N.A. dock. A
  600-second exposure is no longer confused with the plan's former ten-minute
  factory horizon window; untouched legacy values migrate to a 60-minute
  planning window.
- Imported target display names may remain localized while a direct English
  catalogue designation is retained for N.I.N.A. target/FITS naming when the
  source supplies one.
- Routine setup is presented as a profile choice instead of dozens of free-text
  engineering fields. The automatic site profile can load the newest complete,
  hash-valid commissioning package and its Night Setup without a repeated
  manual import. Unsupported fields from a newer local template are ignored
  with an explicit compatibility warning rather than aborting the whole load.
- M2 now has an explicit **keep current position** operating mode. It does not
  silently authorize UVEX motion; the locked position and spectral quality are
  still checked.
- The site horizon default is 30 degrees with no hidden extra start/continue
  margin. The configured horizon remains an action gate and is not inferred
  from this report.
- Commissioning records no longer acquire an arbitrary automatic expiry merely
  because time passed. A hardware, installation, profile, optical, focus or
  motion-identity change invalidates the applicable evidence immediately.
- **Operator weak supervision** is the default local safety capability choice.
  Missing roof, weather, safety-monitor and optional cover adapters are visible
  warnings rather than invented attestations. Any connected adapter that
  explicitly reports unsafe, rain, excessive cloud/humidity/wind, a closed
  cover or an error still blocks the action. This mode is never described as
  unattended.
- ATR cooling starts through the N.I.N.A.-owned camera as soon as exact identity
  is known and proceeds in parallel with acquisition preparation. The first ATR
  probe remains blocked until coherent temperature/set-point/cooler telemetry
  has been stable for the required consecutive samples.

### Acquisition route and diagnostics

- [ADR-0008](adr/0008-g3-first-large-acquisition-corrections.md) makes the
  on-sky-validated route the production default: QHY supplies an immutable
  no-motion wide-field WCS witness; a fresh G3 WCS owns the large N.I.N.A.
  correction; another fresh G3 solve proves the optical result. The legacy QHY
  motion envelope remains readable only for old evidence/recovery.
- The static two-arcsecond frame/mount binding tolerance is no longer reused as
  a large-slew optical-arrival tolerance. A separately bounded reported endpoint
  may authorize one fresh verification frame; only its fresh G3 WCS may finish
  coarse acquisition.
- Formal, physically plausible PlateSolve3 solutions remain authoritative
  without a project-authored minimum source/match count. Sparse fields retain
  the bounded overlapping neighbouring-field fallback.
- PHD2 remains the native guide-star selector and G3 owner. Commissioned PHD2
  meridian-flip handling may adapt a source-side topology to the current pier
  side for a new guide epoch; an outstanding exact-lock ledger cannot be
  reinterpreted across a flip.
- QHY, G3 and ATR preview tabs now keep their image actions inside the viewer.
  Maximum-pool downsampling prevents compact QHY stellar cores from vanishing
  in the operator preview without altering raw FITS or science metrics. G3
  acquisition/solve frames are published to the live tab as soon as the
  PHD2-owned frame becomes available.
- The slit HDR analyzer no longer treats a clipped long LED exposure as an
  automatic identity failure when the short exposure independently resolves
  both physical dark-aperture edges. A clipped long frame cannot act as shared-
  PSF width authority. Reflective-ridge width is still never substituted for
  the black physical aperture.

## QHY filter identity and historical correction

The eight positions were closed by stellar-colour and nebular-morphology tests:

```text
software P0/P1/P2/P3/P4/P5/P6/P7 = u/O III/Hα/S II/z/i/r/g
physical  1/2/3/4/5/6/7/8       = u/g/r/i/z/S II/Hα/O III
```

The relation is a reverse direction plus a one-position cyclic offset, not a
random filter installation. The tracked production example now uses
`U0,O1,H2,S3,Z4,I5,R6,G7`. Existing raw FITS were not renamed, moved or edited;
the correction tool emits separate SHA-256 provenance sidecars. The previously
reported QHY `H-r` result for Nova Sge 2026 is withdrawn because those frames
were actually O III. UVEX/ATR spectra are unaffected. Full evidence and the
scientific retraction boundary are in the
[filter-identity incident report](incidents/2026-08-26-qhy-filter-identity-correction.md).

## QHY USB transport/enumeration incident

The affected QHY acquisition requested `gain=20`, `offset=20`, 1×1 binning and
a 0.5-second R exposure. It started at `04:08:37.934`, but the immutable frame
did not finish until `04:09:01.842`: about 23.9 seconds elapsed for a nominal
0.5-second exposure. Its minimum, maximum, mean and median were all zero,
`zeroFraction=1`, and the frame carried `ZERO_CLIPPING`. Windows recorded QHY
removal at `04:09:01.153`, before that frame was recorded as complete, and later
configured a device-descriptor failure on the same host port. A device-only
software restart could not restore enumeration.

The later message that the camera did not report `gain` is therefore a
downstream symptom of an invalid or lost camera handle, not evidence that the
requested gain caused the failure. The first lease-renewal HTTP 500 was logged
at `04:09:08`, after the USB removal, so it was an independent service defect
and not the cause of the transport loss. Logs also show no duplicate-owner
event: N.I.N.A. owned ATR585M, the QHY service owned QHYminiCam8M, and N.I.N.A.
only enumerated the QHY device during startup.

The most specific supported classification is `QHY_USB_TRANSPORT_LOSS`: a USB
transport/enumeration failure during an abnormal acquisition. The physical root
cause remains unconfirmed. A host-port transient, power or signal-integrity
fault, intermittent contact, or a camera firmware/FPGA/SDK/driver stall remain
possible. Re-enumeration on another host USB socket restored communication, but
that recovery does not distinguish among those causes.

### Maintenance follow-up and serial re-enumeration

The serial-port changes observed during maintenance were intentional and do not
indicate a second unexplained device loss. The same flat-panel CH340 controller
that had previously enumerated as `COM15` appeared briefly as `COM3` and finally
as `COM7` after it was unplugged and tried in two sockets. The flat-panel driver
auto-search subsequently connected successfully, confirming `COM15 -> COM7` as
one physical controller. A saved `COM1` registry value is stale/default state,
not authoritative device identity.

After the QHY was moved to a different host USB socket, Windows kept it present
and healthy in repeated read-only samples. N.I.N.A. then opened the
QHYminiCam8M and its integrated eight-position filter wheel and released both
normally. This proves current basic communication and recovery, not the root
cause of the earlier loss or readiness of the updated acquisition service.

Two software defects were corrected around this incident:

1. Required native controls (`gain`, `offset`, `exposure`) are now attested once
   when the exact QHY handle is initialized, together with finite range data.
   A transient availability query can no longer misclassify an established
   control after a frame. Every real parameter write is still executed and any
   native I/O failure remains a hard stop; the cache does not convert transport
   failure into success.
2. The lease-renewal request had both a modern string-token constructor and a
   legacy GUID compatibility constructor. System.Text.Json therefore rejected
   HTTP renewals as ambiguous and the service logged repeated 500 errors. The
   modern constructor is now the explicit JSON constructor, and the loopback
   HTTP test renews a live job lease before cancellation.

One diagnostic improvement remains open: an acquisition with grossly excessive
elapsed time, an all-zero immutable frame and contemporaneous Windows PnP loss
should be reported primarily as USB transport/enumeration loss, rather than as
an unsupported-gain error from a handle that is no longer valid.

No raw frame, manifest or service diagnostic was placed in Git.

## CI failure and correction

The CI run for commit `12d83f7` failed only in **Audit the public source
snapshot**. Build-independent Spectral Studio tests passed. The strict audit's
secret-assignment expression interpreted N.I.N.A.'s public filename placeholders
`$$TARGETNAME$$` and `$$IMAGETYPE$$` as secret-token assignments. The audit now
excludes only the exact `$$...$$` placeholder prefix while retaining ordinary
quoted token/key/password/secret detection. The current source snapshot has zero
text findings and zero binary/data candidates.

The workflow actions were also moved from deprecated Node.js-20 generations to
their current Node.js-24-compatible major releases. The pushed closeout commit
`e4d6dcc` subsequently passed both hosted jobs, including the strict public
source audit and the Spectral Studio test suite, so this CI incident is closed.

## Verification boundary

The local release checks cover:

- frozen design-hash verification;
- product-layout, GPL metadata, branding and strict public-snapshot audit;
- Release builds and hardware-independent .NET tests;
- QHY adapter/API regression, including real HTTP lease deserialization;
- N.I.N.A. plugin tests and all 12 deterministic UI render scenes;
- pinned reduction Ruff and pytest suites.

Maintenance has restored Windows enumeration and proved a normal N.I.N.A.
open/release cycle for the QHY camera and integrated filter wheel. The fixed QHY
service was not installed during that check because the elevation prompt was
cancelled, so the corrected acquisition path still requires a real-frame replay.
The shortest sufficient commissioning check is:

1. install and start the built QHY service revision;
2. verify that Windows enumerates the QHY device normally and let the service
   bind the exact commissioned stable identity;
3. read back the expected filter position;
4. acquire two consecutive short frames with the same non-zero gain/offset;
5. confirm that both frames finish near their requested exposure duration, are
   not all zero, and have plausible image statistics;
6. verify a lease renewal across the interval and inspect both immutable frame
   records;
7. only then resume an automatic observation.

Hardware shutdown and roof state are outside this software closeout and remain
under the operator's control.
