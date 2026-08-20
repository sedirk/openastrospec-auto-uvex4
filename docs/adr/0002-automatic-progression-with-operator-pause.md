# ADR-0002: Automatic progression with operator pause and intervention

**Status:** Accepted; supersedes the confirmation-gate portions of the acquisition baseline  
**Date:** 2026-08-16  
**Decision owners:** Observatory owner and UVEX-ADV project

## Context

The original commissioning baseline required an operator confirmation after slit placement and proposed removing selected confirmations only after a later qualification phase. The observatory owner clarified that this contradicts the intended automation model: a healthy run should progress without repeated clicks. The operator must still be able to see the evidence in real time and pause, resume, cancel, or take over at any point.

This change does not relax device ownership, horizon, motion, solve-confidence, guiding, camera-quality, or provenance requirements.

## Decision

The canonical workflow uses automatic progression:

- Every transition is guarded by a machine-evaluable quality and safety gate.
- A passing gate advances immediately; there is no routine per-stage confirmation.
- A failed or indeterminate gate stops new actions and enters `PausedNeedsAttention`, retaining evidence and an explicit reason.
- Operator controls are `Pause`, `Resume`, `Cancel`, and `Take over`. Pause is cooperative: no new motion or exposure starts, in-flight work follows its declared cancellation/retention policy, and state remains inspectable.
- `Resume` revalidates all stale gates before performing another physical action.
- Automatic recovery is bounded by configured per-step, cumulative, attempt, and elapsed-time limits. Exhaustion pauses the run instead of guessing or continuing.
- Starting an observation is the operator's authorization for that bounded plan; normal execution does not ask for repeated confirmation.

If no trustworthy roof/safety-monitor input exists, the run records the capability as unavailable and applies the configured startup policy. It must never infer roof or weather safety from a camera frame or CCTV image.

## Consequences

### Positive

- The workflow matches N.I.N.A.-style unattended sequencing instead of becoming a checklist of modal prompts.
- Operator attention is directed to exceptional states, while visual evidence remains available continuously.
- Pause and recovery semantics are testable without weakening movement bounds or evidence requirements.

### Costs and risks

- Slit-centering confidence, target identity, solver sanity, horizon prediction, guiding stability, and device-state validation must be sufficiently explicit to be executable gates.
- Missing commissioning measurements will cause `PausedNeedsAttention`; they cannot be bypassed by silently substituting guessed constants.
- Real-hardware commissioning still proceeds by simulator, read-only, bounded action, shadow analysis, and closed loop, even though each qualified stage advances automatically.

## Unchanged decisions

ADR-0001 remains authoritative for sole device ownership. Emergency stop, cancellation, raw-data immutability, bounded motion, evidence capture, and staged hardware commissioning are unchanged.
