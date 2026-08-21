# Observation dock offline screenshot harness

This developer-only WPF executable renders the real production
`UvexAdv.Nina.Plugin.ObservationDockable_Dockable` `DataTemplate` with deterministic
mock data. It never constructs `ObservationDockable`, starts N.I.N.A., opens a camera,
contacts PHD2, opens COM5, or asks the mount to move.

Rendering is refused unless the caller supplies the explicit `--render` switch:

```powershell
.\.dotnet\dotnet.exe run --project .\tests\UvexAdv.Nina.Plugin.UiHarness\UvexAdv.Nina.Plugin.UiHarness.csproj --configuration Release -- --render
```

The default output is the ignored `tmp/ui-screenshots/` directory. Eleven PNG files
cover idle, startup requirements, running, the integrated ATR single-frame check,
failed/paused, PHD2 degraded,
direct-target supervised guiding, calibrated ghost-assistance, QHY/G3 fast pairing,
narrow-dock, and advanced bright-target settings states. The wide idle and narrow
scenarios also exercise target-import status, full button labels, responsive wrapping,
and run-time command disabling. To render one scenario:

```powershell
.\.dotnet\dotnet.exe run --project .\tests\UvexAdv.Nina.Plugin.UiHarness\UvexAdv.Nina.Plugin.UiHarness.csproj --configuration Release -- --render --scenario failure --output .\tmp\ui-screenshots
```

The `advanced` scenario selects the production Advanced Settings tab, expands the
bright-target wing-centroid section, and scrolls it into view for label/layout QA.

The `atr-manual` scenario selects `Live Images > ATR 2D / 1D Spectrum` and verifies
that camera identity binding and one-frame extraction diagnostics are integrated
beside the automatic-observation preview instead of being exported as a separate
placeholder dock. Its curve and camera identity are deterministic mock data.

The `ghost-assistance` scenario shows the compact operator summary for Auto mode;
the full calibration/policy hashes, applicability and centroid/covariance-only
authority boundary live in the collapsed Advanced Settings detail. It is synthetic
and does not read FITS or contact hardware.

The `phd2-direct-target` scenario proves that a Qualified calibration does not turn
the ultra-bright-target fallback into unattended authority: ordinary and direct
exposures are shown separately and the route remains explicitly supervised.

The harness applies a small offline approximation of the N.I.N.A. night palette.
Production XAML remains authoritative; the harness deliberately does not replace
the production template with a separate mock layout.
