# ADR-0057 — Explicit mount Home before the first eligible target

Accepted 2026-11-21 at the owner's request. Supersedes only the absence of an
opening Home in the native night of ADR-0019/0021. ADR-0038's prohibition on
adding a Home *as a retry after a failed catalogue GOTO* remains in force.

HD 25627 stopped on .361 before imaging: the single tracking request was
acknowledged but tracking remained off; the one permitted GOTO did not restore
stable tracking. The operator traced this to residual tracking after ordinary
cancel reaching the controller's meridian limit and recovered by native Home.
That diagnosis does not make every tracking failure a proven limit hit.

- The saved, default-off `HomeMountAtNightStart` option is locked for an
  explicitly started native spectroscopy night. New distributed simulation
  examples show it enabled; simulation never operates physical equipment.
  Missing fields in old saved files, standalone target starts, program launch,
  reconnection, cancellation, new targets and flip/clock/focus continuations
  do not grant new Home commands. Closeout Home/Park is a separate option.
- Run at the first *eligible* target's initial slew after Night Setup, not the
  root Start area: a preflight horizon skip cannot cause movement. At this
  point the master holds the exclusive lease, the bound roof is fully open,
  the cover is open and any required native unpark has been verified. Recheck
  full weather/monitor, original deadline/dawn, horizon, immutable owner/clock,
  actual ATR, global photometry idle, stopped PHD2, focus and outstanding motion
  obligations. Do not stop guiding or commandeer somebody else's operation.
- Persist one exclusive intent in the unique night's start-home directory
  before changing residual tracking or issuing native `FindHome`. A still
  tracking but otherwise idle mount may be asked once to stop tracking before
  Home; two stationary samples are required. Already home requires no Home
  command. Keep controller limits; no manual zero-setting, Park-as-Home, forced
  flip, coordinate Sync or raw hardware command. Neither motion nor verification
  extends the original target/night deadline or subsequent acquisition budgets.
- While awaiting native completion, check live safety and cancellation; a
  timed-out wait is not a joined driver operation. Two stopped, tracking-off
  `AtHome` readbacks, a final idle-owner check and a durable result are needed
  before entering the normal tracking/GOTO and optical verification chain.
  Unknown completion never authorizes another attempt under the same night
  identity; a reconstructed runner cannot replay the intent. Never use this
  Home as proof of returning an old vector or of optical target position.
- Ordinary cancel continues to stop acquisition without authorizing Home/Park/
  closure by itself. If tracking cannot be restored after a catalogue slew,
  stop and preserve the independent safety policy; do not skip or repeatedly
  Home into an unconfirmed mechanical state. Software verification is not
  real-sky commissioning.
