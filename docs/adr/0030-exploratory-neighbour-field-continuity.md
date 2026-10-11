# ADR-0030 — Exploratory neighbour-field continuity before repeated solving

- Status: Accepted
- Date: 2026-10-11
- Authorization: the owner requests fewer observational-quality blockers, use of
  neighbouring-field WCS and pixels when the target field cannot solve, local
  dual-camera mapping, and continuation to the next target after recoverable
  acquisition failure.
- Supersedes: ADR-0008's requirement for another formal target-field WCS at every
  coarse-to-fine handoff, and ADR-0004's unconditional pause after search
  exhaustion, only within the measured/predicted continuation described here.
  ADR-0001 ownership and independent hardware-safety boundaries are unchanged.

## Context

The 01:08 run of HD 18552 obtained several neighbouring-field solutions and one
same-pointing QHY/G3 pair. After its final approach, the first target frame held
a bright candidate about 40 pixels from the slit. The old 20-pixel *proposal*
filter prevented the existing wider short-frame confirmation from running.
Repeated 5/10/15-second solve tiers then consumed the original return-time
reserve. Independently, the verified-return failure code was omitted from the
native night's skip classification, so all later targets were not executed.

The candidate's appearance does not prove its catalogue identity or slit arrival.
It does, however, justify using fresh pixel evidence before spending the remaining
budget on the same failed whole-field operation. Experimental continuity is the
goal; absolute catalogue certainty and optimal precision are not prerequisites
for attempting that confirmation.

## Decision

1. A formal neighbouring G3 solution plus the recorded, measured mount arrival
   may predict the target in the new frame. The prediction is a search/association
   seed, not a new formal WCS or a measured centroid. Keep source hashes, geometry,
   uncertainty and arrival provenance. An intermediate field still outside the
   target's usable detector area does not become a target handoff by fiat.
2. At the final approach, a fresh stellar candidate in the configured recognition
   area and remaining fine-motion envelope may enter the existing independent
   short-frame confirmation. Do not first require it to lie within the historical
   20-pixel coarse window around both the prediction and the slit. The downstream
   confirmation decides whether fresh consistent measurements support association
   and fine placement; the proposal filter does not claim success.
3. Try this bounded short-frame route early, before needlessly completing another
   full exposure/solve ladder. Retain original failed solves and record which
   evidence actually supported continuation. Source acquisition, confirmation and
   fine movement consume existing deadlines/reservations; no unbounded retry or
   fresh budget is created. If the route cannot establish a useful result, continue
   the available fallback or end this target with its original reason.
4. Same-run QHY/G3 paired solutions may supply a local **search hint** for a fresh,
   unchanged-pointing QHY solution, with the pair's optical offset and explicit
   provenance. A single sample is not a global flexure model, is not promoted to
   an Active transfer, and grants no extra mount or fine-motion authority. A
   mismatched/expired pair is skipped without blocking ordinary G3 acquisition.
   Wide-field solving and G3 pixel-to-mount control remain separate models.
5. After checked stop and independent terminal reconciliation, the explicit
   `G3_WCS_CENTERING_BUDGET_EXHAUSTED_RETURNED` and verified-return nonconvergence
   outcomes are target quality failures under the saved skip policy. The next
   native target must actually execute. An unknown code containing `RETURNED`,
   unsuccessful stop or unresolved return is not made successful by string match.

## Boundaries and evidence

Warnings and inference are first-class results, not fabricated measurements.
Prediction remains labelled `PREDICTED / 未实测`; short-frame association records
measured pixels and its provenance, and science keeps the existing target/slit
and guide checks. This change does not force scientific-quality qualification.
Raw FITS, failed attempts and motion history stay immutable.

Weather Unsafe, mount electronic/mechanical/horizon limits, changed device owner,
operator cancel and unconfirmed stop remain separate physical constraints. A
later Unsafe may still close the observatory even after a quality failure selected
keep-open standby. Ordinary target failure must not be mislabeled as that event.

## Verification

Regression tests must cover a candidate beyond 20 pixels reaching short-frame
confirmation, outside-envelope/invalid candidates not gaining movement authority,
the ordering of confirmation before repeated full solving, hint-only pair use and
fallback, and native two-target execution after verified-return exhaustion.
Include failed cleanup, unknown return code and explicit EndNight policy cases.
Build/replay/installation are not claims of successful on-sky acquisition.
