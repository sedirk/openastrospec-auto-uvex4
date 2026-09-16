# ADR-0018: Run-local main-telescope focus, independent of UVEX calibration

Status: Accepted, 2026-09-16, explicitly requested by the observatory owner.
Supersedes only the cross-run C11 position-equality interpretation of ADR-0003
and baseline section 4; device ownership and all other focus roles are unchanged.

Main telescope focusing changes the incident stellar image, not the setting of
the spectrograph's internal M2, grating, slit or spectral camera. An intentional
between-run C11 adjustment must not require recreating UVEX installation or
spectral calibration records merely because a historical C11 step number changed.
This does not assert invariant slit illumination, throughput, centroids or PSF.

Each new production runner captures the stopped, connected, identity-checked C11
owner's actual position within the declared travel range. That run-local position
is immutable for this runner, including Resume and repeated setup checks. It is
recorded beside the historical Night Setup position, with no inherited focus
quality claim. Downstream checks use the run lock; live physical identity and USB
topology are still checked against commissioning. Changed position during a run
stops continuation; only a new run can establish another position.

The immutable Night Setup file, hash, raw data and internal UVEX calibration are
not rewritten. Existing historical C11 focus measurements remain tied to their
original position; an exceptional bright-wing/ghost route requiring independent
focus evidence must not promote the new position to measured optical quality.
New target/WCS, slit and guide evidence are acquired through the normal route.
No automatic focus movement, return to a historical number, or resume is added.

Tests must cover changed between-run positions, same-run movement and Resume,
missing/wrong/busy owners, travel limits, unchanged UVEX/GS350 gates and raw setup
immutability. Installation checks do not establish real-sky focus success.
