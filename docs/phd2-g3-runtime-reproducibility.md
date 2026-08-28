# PHD2 / G3M2210M runtime reproducibility

## What is and is not customized

The observatory does **not** use a private or patched `phd2.exe`. The label
`c11+ccdt67+slit+2210` is a PHD2 equipment profile name, not a software build.
No OpenAstroSpec change has been made to PHD2 star selection, centroiding,
multi-star guiding, calibration, guide algorithms, event protocol, or JSON-RPC
implementation.

The only installed binary substitution is the camera vendor's x86 ToupTek SDK
library loaded by the otherwise unchanged PHD2 executable:

| File | Version / role | SHA-256 |
| --- | --- | --- |
| `C:\Program Files (x86)\PHDGuiding2\phd2.exe` | PHD2 2.6.14 executable | `2A509227FA529865E2B046468564B28362F04ED8FDF05CF6D53AD52448C3C18B` |
| `C:\Program Files (x86)\PHDGuiding2\toupcam.dll` | active ToupTek x86 SDK `59.30701.20260128` | `FEAD71A73A682DA0FC929B77DC72A885B57731A989C6A18EF47037484F278EE5` |
| `C:\Program Files (x86)\PHDGuiding2\toupcam.dll.59.29465.20250907.bak` | original rollback SDK `59.29465.20250907` | `EED731C634BF1EFB51659AFB9E8F8089D1D31ECFAF7271D9A62CD861AEE86375` |

On 2026-08-28 the formal executable, the earlier per-user G3 installation,
and the SDK-update staging copy were compared byte-for-byte. All three were
9,129,472 bytes, had the same timestamp, and had the executable hash in the
table. This is why project documentation should say **official PHD2 2.6.14
with ToupTek SDK 59.30701.20260128**, not “custom PHD2”.

The repository does not redistribute either vendor DLL. A reproducible machine
setup must obtain the SDK from the camera vendor, verify the version and hash,
stop PHD2 normally, back up the installed `toupcam.dll`, place the x86 library
beside `phd2.exe`, and run a fresh immutable OFF/ON/OFF camera test. To roll
back, stop PHD2 normally, restore the versioned backup as `toupcam.dll`, verify
its hash, and repeat the same test. Never replace the DLL while PHD2 is running.

## Owner-native frame capture

OpenAstroSpec uses PHD2's existing `capture_single_frame` event-server command
and waits for `SingleFrameComplete`. Both are present in the upstream PHD2
source; they are not private RPC additions. The slit sequence supplies exposure,
binning, and gain atomically and requires PHD2 to attest that the requested
parameters were applied. Ordinary PHD2 guiding-profile gain is not rewritten.

PHD2 remains the sole G3M2210M owner. OpenAstroSpec never opens the camera SDK
directly and never replaces PHD2's native star selection or guiding functions.

## Spectrograph slit overlay

PHD2's official slit overlay is profile data stored under
`/overlay/slit/center.x`, `center.y`, `width`, `height`, and `angle`. PHD2 2.6.14
does not expose a JSON-RPC method to update those coordinates in a running
instance; it reads them when the profile starts. OpenAstroSpec therefore must
not add a private PHD2 binary patch or fragile UI injection merely to obtain a
live overlay. During an observation, the N.I.N.A. panel and immutable evidence
remain the authoritative fresh geometry display.

After the no-motion 2026-08-28 slit test, profile 2 was synchronized through
PHD2's own profile storage and PHD2 was normally restarted while no observation
was active. The official `View -> Spectrograph Slit` radio item was then enabled:

- center `(817, 425)` pixels;
- width `410` pixels and height `4` pixels (the slit is nearly horizontal);
- PHD2 angle `+2°`, corresponding to OpenAstroSpec line angle `-2°` because the
  two implementations use opposite rotation signs.

The previous `(937, 440), 1000 x 3, +3°` overlay was stale and was never used as
motion authority. A future upstream PHD2 overlay API could make same-session
publication safe; until then an automatic run may queue the verified profile
values for the next PHD2 start but must not restart a guiding session merely to
refresh a cosmetic overlay.
