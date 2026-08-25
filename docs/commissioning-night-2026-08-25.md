# 2026-08-25/26 M76 faint-target commissioning closeout

## Outcome

This observing night produced the first closed-loop non-stellar/faint-target
spectroscopy result for the project. One unattended start progressed through a
sparse target field, a bounded neighbouring-field solve, direct WCS correction,
fresh slit recognition, PHD2 off-slit guiding/exact-lock placement, adaptive ATR
exposure selection and one accepted 600 s science frame. No operator or model
correction was applied after the single start. Roof/weather automation was not
claimed.

The immutable run manifest is local, ignored observing evidence:

`output/commissioning/2026-08-25-night/m76-unattended-20260825T195239Z/manifest.json`

Target: M76, J2000 RA `25.58189905342°`, Dec `+51.57542638057°`.
Slit: wheel slot 2, nominal 15 µm.

## Acquisition and placement evidence

1. The direct G3 field did not produce PlateSolve3 WCS at 10 s. The 20 s tier
   also failed, without ending the route.
2. The configured bounded search visited a field 5 arcmin east. PlateSolve3
   returned a formal solution. Its approximately 3677 arcsec distance from the
   mount hint was physically consistent with the two optical axes and inside
   the configured 5° reach envelope; the old policy had incorrectly mixed this
   value with the much smaller motion budget.
3. N.I.N.A. issued a direct bounded WCS correction. A fresh post-move solve
   demonstrated the expected sky response before any final exact-lock action.
4. PHD2 used native full-frame off-slit guide selection and a fresh pier-east
   calibration. The dark physical slit midpoint and exact-lock residual were
   the final detector-plane authorities.
5. Target peak visibility was not required. M76 is a compact emission nebula,
   so catalogue/WCS geometry plus guide/lock/slit evidence is scientifically
   more appropriate than forcing a stellar FWHM or SNR gate at the target.

This result supports trusting a physically plausible formal PlateSolve3 result.
There is no project-level minimum source or match count. Scale, parity,
two-optical-axis reach and post-move response remain the independent checks.

## ATR adaptive exposure result

The ladder visited `0.01, 0.03, 0.1, 0.3, 1, 3, 10, 30, 60, 120, 300, 600 s`.
The 600 s tier was freshly recaptured before science authorization.

Accepted science frame:

- local path: `%USERPROFILE%\Documents\N.I.N.A\2026-08-25\LIGHT\2026-08-26_04-17-50_FILTER-_0.00_600.00s_0000.fits`
- SHA-256: `F234E1C0D02D71E0563E6BD9A1193431B8B30F617EC3F96E2247ABEC79CE82F4`
- full-frame saturated fraction: `4.219714506172839E-06`
- trace saturated fraction: `0`
- clipped dispersion-column fraction: `0`
- longest clipped run: `0`
- continuum SNR per resolution element: `2.0235`
- line SNR per resolution element: `19.8975`
- target/sky contrast: `1.2308`
- predicted full-scale fraction: `0.01450`

The preceding 600 s validation probe had line SNR about `16.19` and no
meaningful clipping. The low continuum SNR is expected for M76 through the
retained 15 µm nova slit and is not evidence of failed placement. Emission-line
and accumulated-stack SNR are the relevant faint-PN metrics. A wider slit may
increase throughput but must be selected explicitly because it trades spectral
resolution against signal.

## QHY concurrent evidence

The successfully paired R/5 s QHY frame contained seven detected stars, zero
saturation, median FWHM about 4.00 px and median ellipticity about 0.081. It was
captured by the QHY service while N.I.N.A. owned ATR and PHD2 owned G3, proving
that different physical cameras can operate concurrently without violating the
single-owner rule.

A later diagnostic invoked H/O/S 60 s QHY frames, but N.I.N.A. and PHD2 had
already been closed by the operator and the equipment was parked. Its manifest
therefore correctly reports failure because no ATR exposure started:

`output/commissioning/2026-08-25-night/m76-sho-parallel-20260825T203338Z/manifest.json`

Those three raw frames are immutable hardware/filter-wheel diagnostics only,
not M76 science data. They showed that the integrated wheel could select and
read back H, O and S with zero saturation. Production now supports a configured
repeating filter/exposure sequence inside one QHY job and maintains independent
quality baselines per filter; a future on-target replay must validate the
scientific sidecar route.

## Software decisions applied after closeout

- Formal PlateSolve3 success is accepted when coordinates, angle, residual and
  scale are finite/physical and the hint residual fits a configurable two-axis
  reach envelope. Match/source counts are telemetry.
- The two-axis reach setting is independent from all move budgets.
- The observation plan exposes `DirectStellar`, `FaintPointSource`,
  `CompactExtended`, `ExtendedNebula` and `InvisibleInG3` choices.
- Non-stellar modes use the locked C11 focus record rather than requiring the
  science target to provide focus stars.
- Catalogue-WCS target geometry is explicit; no target flux or SNR is
  fabricated. Off-slit guiding closes on fresh slit, guide/lock, exact-lock and
  settle evidence. Direct-target fallback still requires a real detection.
- QHY photometry can cycle configured `Filter:Seconds` steps such as
  `H:60,O:60,S:60` concurrently with ATR science.

The operator later confirmed that N.I.N.A./PHD2 were deliberately closed,
equipment was returned, and dark-frame acquisition was in progress. No further
hardware work was authorized or performed during the software/documentation
closeout.
