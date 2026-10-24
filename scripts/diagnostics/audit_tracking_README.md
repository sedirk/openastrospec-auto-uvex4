# Historical fine-target tracking audit

Image-only exploration. It does not install software, open equipment or create
motion/target identity authority. Raw FITS are read and hashed before/after.

The five fixed cases cover successful guiding, a false small peak, guiding-time
source displacement, short-frame detector misses, and persistent multi-lobes.
`actorScope=offline-history-replay`; these are not new observing successes.
Forced-aperture SNR uses the SEP background error only, not calibrated photon
noise. Saturated flux and template similarity are diagnostics, not acceptance.

Use the existing star-detection Python environment; do not alter the pinned
reduction environment. The optional C# adapter references the already-built
Observatory DLL and does not build a production project or access devices.

```powershell
$raw = Join-Path $env:LOCALAPPDATA 'UVEX-ADV\observations'
& .\output\star-detection-venv\Scripts\python.exe `
  scripts/diagnostics/audit_tracking_20261024.py `
  --observations $raw --output tmp/tracking-audit-input

# This invokes the exact built C# image registration, with independent holdouts
# and graph consistency; the preceding Python registration is a prototype only.
.\.dotnet\dotnet.exe run --project scripts/diagnostics/audit_tracking_core.csproj `
  --configuration Release -- `
  tmp/tracking-audit-input/report.json tmp/tracking-registration.json

& .\output\star-detection-venv\Scripts\python.exe `
  scripts/diagnostics/audit_tracking_20261024.py `
  --observations $raw --output tmp/tracking-audit-final `
  --registration-report tmp/tracking-registration.json

.\reduction\.venv\Scripts\ruff.exe check scripts/diagnostics/audit_tracking_20261024.py `
  scripts/diagnostics/audit_tracking_test.py
& .\output\star-detection-venv\Scripts\python.exe -m unittest discover `
  -s scripts/diagnostics -p audit_tracking_test.py -v
```

Every output directory/path must be new and outside observations. The final
report hashes the script, SEP module, C# registration report and built production
assembly. Each C# registration row must match the actual source SHA-256.

The target region is excluded from registration. The forced aperture does not
search for the brightest peak; optional original-stamp matching is restricted to
four pixels around the field prediction and grants no motion authority. An
unregistered aperture remains explicitly tied to its old anchor, not a claim
that the target did not move. Current production temporal admission, live owner,
weather, target/companion identity and complete observing workflow are not
reproduced by this spatial audit.

No unseen exposures, longer exposures, different focus, wide-field cadence or
counterfactual spectra are generated. A low-SNR aperture or persistent multi-lobe
may motivate a future experiment, not retroactively establish a good spectrum.
