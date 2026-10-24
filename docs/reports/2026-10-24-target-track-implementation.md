# Measured target continuity implementation (ADR-0044)

This component note describes source and offline regression, not new on-sky
success or installation. The release/deployment receipt is owned by the main
implementation report.

The ordinary stellar guide-selection and fresh-guiding-residual entry points
now share one last-trusted target reference. Complete independent SEP field
parents, with the same frozen extractor hashes as coarse acquisition, feed the
existing `G3OverlapRegistration` (three spread fit stars and at least two held-out
stars). The target recognition region, slit and detector-fixed matches are
excluded. Accepted registration supplies only a search prediction. The original
identifier must measure the target on the new image; native-primary continuity,
component morphology and the original budgets still apply. Same-exposure,
same-saturation-state normalized patches help reject an unrelated small peak or
bright wing. A rejected candidate never replaces the trusted reference. No new
camera owner, exposure, pulse, solver ladder or motion credit is introduced.

Catalogue-only sampling also consumes this independent similarity registrar,
not the older three-star self-validated translation implementation. A geometric
sampling point remains explicitly different from a measured stellar centroid.
Registration exhaustion is a typed target-quality result, not an untyped
whole-night exception; stop/terminal reconciliation remains independent.

Short-position confirmation deliberately retains the original long-frame WCS
field. `G3MeasuredTargetSource` carries the actual short image, immutable hash,
binding, target measurement and its own capture timing alongside that field.
The short frame's timestamp is not assigned to the older long frame. Full HDR
median composites cannot seed a raw-image track unless their pixels actually
equal the hash-bound source; otherwise the next accepted raw measurement starts
the track.

Guide selection and residual images now enter the shared raw/SEP store. The
residual interface exposes fresh frame/event identity and same-frame primary
pixels but no authenticated shutter-start time. Its capture start is therefore
explicitly unknown, not inferred by subtracting the exposure or wait timeout
from save completion. Such raw frames retain independent geometric **search**
and component-continuity usefulness, but cannot become a temporal graph edge,
current WCS, drift/extrapolation or motion-position authority. Actual new target
pixels and the original native-primary checks are still required. Native single
captures with a real PC request/completion bracket retain the 45/60/120-second
temporal admission limits. Exact-time coverage of native rolling frames remains
limited by the owner API; this is not silently represented as zero latency.

If a fresh stellar identification failed, an available continuous-wide-field
pose plus authenticated local boresight prior may propose a second search on the
**same G3 pixels**. Both the hint and eventual measured candidate must stay inside
the original recognition circle. Unknown wide/narrow uncertainty is recorded;
the model point cannot become a measured target, replace native-primary
continuity or authorize motion. Unavailable data leave the original bounded
route intact. Existing multi-lobe rejection and resolved-component identity are
not relaxed, and no automatic dark-point-source mode switch is made.

## Offline regression

`G3TargetTrackProductionTests` checks independent holdouts/target exclusion,
duplicate/reversed/stale samples, patch/component rejection, raw-versus-HDR
identity, original-window-only wide search and no-signal controls. Compiled async
entry-point IL checks verify that **both actual production call sites** consume,
reject/retain, commit, and feed the common store; the test is not merely checking
that a standalone helper exists.

Set `UVEX_REPLAY_TARGET_TRACK_REPORT` to
`tmp/audit-20261024-tracking-final/report.json`, and
`UVEX_REPLAY_OBSERVATIONS` to the immutable observations root, to run the explicit
historical cases. The retained report supplies complete field-star measurements
and SHA references; the test reads and rehashes all 17 original FITS and runs the
production registrar, current identifier and component policy.

Seven of the nine star-bearing tested edges met the production mask/holdout
contract and obtained current measured centres. The two historical wrong points
(about 56.8 and 17.1 pixels away from the persistent body) were rejected. Two
October 23 edges did not meet the independently held-out registration contract
at the rolling reference; they are honest unavailable-route controls, not
manufactured successful recoveries. HD 218393's weak 10 ms pair has no adequate
independent field stars; HD 224559's multiple lobes are not accepted by treating
persistent signal as a unique star. The separate recorded native-primary tests
cover the existing route and actual next-target/failed-cleanup boundaries.

The stellar-body continuity experiment does not independently prove that the
October 23 bright body is catalogue HD 219937. A reused on-sky identity chain,
observing quality and a new successful complete observation are separate claims.

Final component verification rebuilt the current Debug source (including unknown
rolling-exposure timing and both wide-hint call sites) and passed **177/177**
focused/recorded/source-boundary tests, zero failures and zero skips. Both history
environment variables above were set. The local receipt is
`tmp/target-track-implementation-tests/target-track.trx`. Full release checks and
exact-artifact deployment are reported separately; this result does not install
anything or authorize observation.
