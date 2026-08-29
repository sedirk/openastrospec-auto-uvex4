# QHYminiCam8M deterministic acquisition-time USB disconnect

**Status:** OPEN ROOT CAUSE — reproduced twice; recovered only by a complete cold power interruption  
**Affected owner:** `UVEX-ADV-QHY` / `UvexAdv.Qhy.Service`  
**Physical device:** QHYminiCam8M on GS350  
**Local incident times:** 2026-08-27 04:09:01 CST and 2026-08-29 04:42:19 CST

## Summary

The 2026-08-27 and 2026-08-29 QHY failures are the same incident family, not two
independent reports of a missing cable. In both cases the dedicated QHY service began
the same full-frame acquisition, the call remained in progress for about 24 seconds,
the QHY USB interface disappeared about 23.5 seconds after job start, and the service
then retained an all-zero FITS before reporting a gain-control failure.

The repeat interval from QHY job start to USB removal differs by only about 0.054 s.
That deterministic alignment is much stronger than the coincident mount-slew timing.
The current evidence therefore points first to the QHY SDK/driver/device interaction
during control setup or readout. It does **not** yet prove a purely software defect:
a marginal camera USB controller, cable, upstream hub/controller or independent camera
power path may reset only when the SDK starts sustained traffic. The camera is separately
powered and directly connected to the computer rather than to the observatory power box,
so a shared observatory power-box load is not a supported explanation.

## Correlated timeline

All times below are local China Standard Time (`+08:00`). Run directory names contain
UTC and therefore show the previous UTC calendar date.

| Event | 2026-08-27 occurrence | 2026-08-29 occurrence |
| --- | ---: | ---: |
| N.I.N.A. startup/device enumeration | 04:07:05.824 | 04:41:20.248 |
| First concurrent mount slew began | 04:08:31.060 | 04:41:46.357 |
| QHY acquisition job started | 04:08:37.630 | 04:41:56.133 |
| `QHY5IIISeries_IO` removed from USB | 04:09:01.153 | 04:42:19.601 |
| QHY acquisition call returned | 04:09:01.842 | 04:42:20.367 |
| Service emitted the gain failure | 04:09:02.102 | 04:42:20.696 |
| Job start to USB removal | 23.523 s | 23.469 s |
| USB removal to capture return | 0.689 s | 0.766 s |

The mount was moving during both incidents, but QHY acquisition began 6.57 s after the
first slew and 9.78 s after the second. Despite that difference, the USB removal remained
locked to QHY job start within 0.054 s. Mount motion is therefore a possible concurrent
electrical or cable-load factor, not the primary timing authority shown by the evidence.

## Identical acquisition state

Both jobs used:

- exposure `0.5 s`;
- gain `20`, offset `20`;
- binning `1 x 1` and full-frame ROI `3856 x 2180`;
- readout mode `1`, `16 bit`, USB traffic `0`;
- filter `R` and target temperature `-10 C`;
- the exact stable camera identity recorded in the machine-local incident bundle
  and deliberately omitted from this public document.

Both retained frames have minimum, maximum, mean and median ADU equal to zero,
`zeroFraction=1`, `detectedStars=0` and `ZERO_CLIPPING`. The first service version
reported `QHY camera does not report required control 'gain'`; the later version reported
`Failed to set QHY gain; QHY SDK code 0xFFFFFFFF`. The wording changed, but the USB loss,
zero frame and acquisition timing did not.

## USB topology evidence

N.I.N.A. recorded the removed interface as:

```text
QHY5IIISeries_IO
VID_1618&PID_0588
Manufacturer: QHYCCD
Service: QHYCCD_2ND
```

The older occurrence used downstream instance suffix `6&19880B47&0&5`; the current
occurrence uses `6&19880B47&0&4`. The camera was therefore moved from downstream port 5
to port 4, while retaining the same upstream USB controller/hub lineage. After the
current removal, port 4 enumerated as `VID_0000&PID_0002`, Windows problem code 43,
`USB device descriptor request failed`.

The port change makes a single bad downstream receptacle less likely. It does not clear
the camera, its USB cable, its independent supply or the common upstream controller.

No QHY USB removal was recorded by N.I.N.A. on the local calendar date 2026-08-28.
Additional QHY and CH340 removals around 2026-08-27 16:26-16:30 coincide with deliberate
cable relocation and are not classified as automatic reproductions.

## Immediate recovery policy

Recovery must preserve the single-owner invariant and must not open the camera from
N.I.N.A. or PHD2.

1. Stop `UVEX-ADV-QHY`, the sole QHY camera owner.
2. With administrator rights, restart only the failed Port 4 PnP node. If necessary,
   disable/enable that node and finally remove only the failed descriptor node followed
   by a PnP rescan.
3. Do not restart the common USB root hub while ATR585M, G3M2210M, AAF or serial devices
   are active. A root-hub reset is a separate, disruptive recovery step.
4. When `VID_1618&PID_0588` returns with status `OK`, restart `UVEX-ADV-QHY` and verify
   stable identity and service health through the owner-service API without taking an
   exposure.
5. A Windows restart and Device Manager restart were both later confirmed insufficient
   for this occurrence. If selective recovery cannot re-enumerate the device, perform a
   controlled cold power interruption which actually removes power from the camera and
   computer/USB controller. If that also fails, inspect the camera USB cable, independent
   camera supply and camera controller physically.

The repository-local helper `tmp/recover-qhy-usb.ps1` implements steps 1-4 for the
current failed instance. It deliberately does not reset the root hub and requires an
explicit Windows elevation confirmation.

From an administrator PowerShell in the interactive Windows session, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ".\tmp\recover-qhy-usb.ps1"
```

## 2026-08-29 remote recovery result

At 05:05 CST the failed acquisition job
`233c5387-36c4-40ec-8838-6b9710263058` was explicitly taken over through the loopback
QHY owner-service API. The takeover completed successfully, cancelled no active
exposure, closed the native QHY handle and changed the service camera state from the
stale `connected=true` report to `connected=false`.

Releasing the SDK handle did **not** make Windows enumerate the camera again. Four
seconds after the takeover, the only present device on the affected path remained:

```text
USB\VID_0000&PID_0002\6&19880B47&0&4
Unknown USB Device (Device Descriptor Request Failed)
CM_PROB_FAILED_POST_START / problem code 43
```

This separates two recovery layers. Owner-service takeover is sufficient to remove a
stale application/SDK claim, but it is not sufficient once the USB device node itself
has failed during enumeration. The next non-physical recovery is therefore an elevated,
selective Port 4 PnP restart/rescan. No UAC prompt appeared during the Codex attempt:
the execution host rejected elevation before Windows could create a prompt, and all
non-elevated `pnputil` and service-control attempts were denied without changing device
state. An administrator must launch the helper explicitly from the interactive Windows
session. A controlled Windows restart remains the next remote option if selective node
recovery fails.

The subsequently supplied desktop elevation launcher did not recover the camera and
produced neither the expected recovery-result JSON nor the transcript log. There is
therefore no evidence that its elevated `pnputil` sequence actually ran; this must not be
recorded as a proven selective-PnP recovery failure. The launcher was removed at the
operator's request. The operator elected to perform a controlled Windows restart as the
next non-physical recovery step.

## Confirmed final recovery outcome

The operator subsequently confirmed that both a normal Windows restart and restarting
the device through Device Manager failed to restore the camera. Recovery occurred only
after interrupting power to the complete observatory system. Because the QHY camera has
its own supply and connects directly to the computer, that action cold-cycled both the
camera's independent power domain and the computer/USB-controller domain; it must not be
interpreted as evidence for a load transient on the observatory power box.

After power was restored, a read-only check found the same downstream Port 4 enumerated
normally again:

```text
QHY5IIISeries_IO
USB\VID_1618&PID_0588\6&19880B47&0&4
Status: OK
Problem: CM_PROB_NONE
```

`UVEX-ADV-QHY` was running and its loopback health endpoint returned `status=ok`. No
camera connect or exposure was used for that verification.

This outcome rules out a merely stale N.I.N.A./QHY-service handle and makes a durable
fault state below the ordinary Windows restart boundary substantially more likely. The
state may reside in the camera USB controller/firmware, the independently powered camera
electronics, the cable electrical path, or an upstream USB controller that was not fully
power-cycled by a warm Windows restart. It still does not identify which component is the
root cause. The deterministic 23.5-second acquisition correlation continues to make the
SDK/driver/device transaction the trigger, even if the latched failure itself is held in
hardware or firmware.

## Required software follow-up

- Treat `0xFFFFFFFF`, a missing gain control, an all-zero frame after a long SDK call,
  and disappearance of the stable USB identity as one correlated device-loss event.
- Record separate timestamps for control discovery, gain/offset application, exposure
  start, readout start, SDK return and USB removal so the exact failing call can be
  identified.
- Do not loop additional captures after descriptor loss. Close the QHY handle, mark the
  QHY/photometry branch unavailable, retain evidence, and allow the canonical policy to
  decide whether spectroscopy continues without simultaneous photometry.
- Add a bounded owner-service recovery path which never resets a shared root hub or opens
  QHY from the N.I.N.A. process.
- Detect the persistent descriptor-loss state explicitly and stop recommending repeated
  application, service, Device Manager or warm-Windows restarts after they have failed.
  Escalate to a controlled cold power interruption with a clear warning that simultaneous
  observatory services and devices will be interrupted.
- Reproduce in daylight with mount stationary before attributing causality to a slew.
  Compare the present full-frame mode against one-variable-at-a-time changes to gain
  application, readout mode, bit depth, ROI and USB traffic. Each test must retain its
  manifest and Windows PnP evidence.

## Immutable evidence

- N.I.N.A. log, first occurrence:
  `%LOCALAPPDATA%/NINA/Logs/<first-occurrence-log>.log`
- QHY manifest, first occurrence:
  `%ProgramData%/UVEX-ADV/qhy/data/runs/<first-run>/<first-job>/manifest.json`
- N.I.N.A. log, second occurrence:
  `%LOCALAPPDATA%/NINA/Logs/<second-occurrence-log>.log`
- QHY manifest, second occurrence:
  `%ProgramData%/UVEX-ADV/qhy/data/runs/<second-run>/<second-job>/manifest.json`

These machine-local files remain outside Git and immutable. This incident record only
references them; it does not move, rename or rewrite any acquired frame.
