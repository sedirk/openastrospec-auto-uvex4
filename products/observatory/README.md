# OpenAstroSpec Auto — UVEX4

OpenAstroSpec Auto — UVEX4 is the **acquisition and observatory-control product**. One
installation consists of several cooperating processes because each physical device
must have exactly one owner:

- the N.I.N.A. plugin owns the operator workflow and requests ATR585M exposures
  through N.I.N.A.;
- `UvexAdv.Service` exclusively owns UVEX4 COM5;
- `UvexAdv.Qhy.Service` exclusively owns QHYminiCam8M;
- PHD2 owns G3M2210M, while the plugin communicates through PHD2's event API;
- `UvexAdv.Phd2.Watchdog` provides the independent guiding safety lease;
- `UvexAdv.Admin` is the standalone UVEX manager.

## Human entry points

1. Open **OpenAstroSpec Auto — UVEX4 Manager** for UVEX4 status and bounded manual service operations.
2. In N.I.N.A., open **OpenAstroSpec 自动观测** for complete target observations.
3. At the top of that panel explicitly choose either:
   - **模拟演练（不连接任何真实设备）**, or
   - **真实设备控制（必须通过全部安全硬门）**.
4. The operation panel always shows the latest failed gate, its metrics, a plain-language
   recommendation, the relevant QHY/G3/ATR preview, and the evidence directory.

Selecting real mode alone performs no device operation. Physical work begins only
after the operator presses the start button and every immutable and live interlock
passes.

## Source boundary

The product is implemented by the C# projects under `src/` except
`UvexAdv.Reduction.Launcher`, with matching projects under `tests/`, plus the
equipment-facing scripts and configuration examples. The normative workflow is in
[`docs/observatory-automation-sop.md`](../../docs/observatory-automation-sop.md).

Build and publish from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

Publishing does not install or restart any component. Hardware installation remains an
explicit, elevated, separate operation.

## Release contents

A future GitHub release for this product should contain source plus separately named
artifacts for the N.I.N.A. plugin, Manager, UVEX service, QHY service, PHD2 watchdog,
and commissioning tool. Do not publish a vendor camera SDK until its redistribution
terms have been reviewed and recorded.
