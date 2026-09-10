# Observation dock offline screenshot harness

This developer-only WPF executable renders the real production
`UvexAdv.Nina.Plugin.ObservationDockable_Dockable` `DataTemplate` with deterministic
mock data. It never constructs `ObservationDockable`, starts N.I.N.A., opens a camera,
contacts PHD2, opens COM5, or asks the mount to move.

Rendering is refused unless the caller supplies the explicit `--render` switch:

```powershell
.\.dotnet\dotnet.exe run --project .\tests\UvexAdv.Nina.Plugin.UiHarness\UvexAdv.Nina.Plugin.UiHarness.csproj --configuration Release -- --render
```

The default output is the ignored `tmp/ui-screenshots/` directory. Twenty-six PNG files
cover idle, startup requirements, running, the integrated ATR single-frame check,
Chinese and English failed/paused presentation, bounded recovery in progress, PHD2 degraded,
direct-target supervised guiding, calibrated ghost-assistance, QHY/G3 fast pairing,
narrow-dock, and advanced bright-target settings states. The wide idle and narrow
scenarios also exercise target-import status, full button labels, responsive wrapping,
and run-time command disabling. To render one scenario:

```powershell
.\.dotnet\dotnet.exe run --project .\tests\UvexAdv.Nina.Plugin.UiHarness\UvexAdv.Nina.Plugin.UiHarness.csproj --configuration Release -- --render --scenario failure --output .\tmp\ui-screenshots
```

The `advanced` scenario selects the production Advanced Settings tab, expands the
engineering acquisition-algorithm group and bright-target wing-centroid section,
then scrolls it into view for label/layout QA.

The `failure-en` scenario sets the presentation culture to `en-US` before the
production template is materialized. Its tests inspect the actual visible text and
reject any CJK leakage. Other scenarios use `zh-CN`.

The `atr-live`, `atr-levels` and `atr-narrow` scenarios select the spectroscopy
camera page and check its full-width preview, separate display-control row,
display-only spectral-band fit and collapsed manual tools. `atr-manual` expands
the integrated camera binding and single-frame extraction tools below the
automatic preview. Its curve and camera identity are deterministic mock data.
The independent diagnostic curve must not be stretched with the image, and
opening controls must refit the spectral band to the changed viewport.

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

`photometry-off` verifies the fixed-header synchronized-photometry switch; the
worker and worker-en scenarios render the separate production photometry dock
template with synthetic data. Master/worker pairing, English role isolation, running,
pending pause, narrow layout and the help page are also covered. Tests verify that
the other role's actions and collapsed raw details are not visible. These scenarios
never construct the real photometry host or connect devices.
