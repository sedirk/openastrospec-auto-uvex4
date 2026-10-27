# ADR-0049: Native sequence skip scopes

- Status: Accepted by the owner request, 2026-10-27.
- Supersedes: ADR-0036's treatment of every target-area interruption as ordinary
  whole-sequence cancellation; all physical-stop and safety requirements remain.

N.I.N.A. distinguishes skipping a leaf, interrupting an instruction set, skipping
the target area to End, and cancelling the root. The spectroscopy adapter must
not promote a local interruption into cancellation of the entire night.

1. Reuse native sequence commands; do not add a parallel plugin skip button or
   replace the native menu. Native target-container Interrupt/Skip must cancel and
   join only that logical target, retain its raw frames and budgets, independently
   confirm stopped acquisition/motion and terminal ledger retirement, then allow
   the next native target. Record `SkippedByOperator`, not success or a quality
   failure. A simultaneous parent cancellation, unsafe event, deadline, unknown
   stop or cleanup failure overrides local skip. No recovery/flip is started.
2. A native current-instruction skip inside the immutable spectroscopy workflow
   ends this target through that same boundary. Its internal stages are dependent
   prerequisites, not independent user-authored cards; skipping acquisition must
   never pretend it succeeded and proceed to exposure.
   Other native cards retain their native behavior. A target's local conditions
   ending its dependent stages likewise end only that target after checked stop.
3. Native target-area interruption first joins the current target and transfers
   monitoring to the existing independent standby. Only actual execution of this
   run's native root End area, with its uncancelled token, may consume a transient
   End receipt. If the locked closeout choice is on, ask that exact standby owner
   to close once, await verified closure, then allow the protected native End
   instructions once. If keep-open is selected, retain standby and skip conflicting
   equipment End actions. Never take over another night's standby.
4. Ordinary root cancellation does not execute End or issue mechanical commands.
   Unsafe, dawn/deadline, failed stop/retirement/closure and unrelated faults do
   not grant optional End authority. Reset/clone/restart invalidates receipts.
   Native End order and current saved choices are retained; no active sequence
   file is rewritten. Explicit close-night remains distinct from skip-target.

Verify using the installed native root engine: leaf Skip, target Interrupt,
target-area Interrupt, root Interrupt, local condition completion, repeated
requests, and late unsafe/cancel/stop failure. Assert the next target actually
executes, or End executes exactly once, as appropriate. Hardware-free tests and
UI acceptance do not authorize observations or constitute real-sky validation.
