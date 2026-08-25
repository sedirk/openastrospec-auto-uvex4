# ADR-0007: N.I.N.A.-native target identity and image provenance

**Status:** Accepted
**Date:** 2026-08-25
**Decision owners:** Observatory owner and OpenAstroSpec Auto project

## Context

OpenAstroSpec Auto originally stored a target draft in parallel scalar fields:
target name, catalogue ID, J2000 right ascension and J2000 declination. It could
copy those values from N.I.N.A.'s framing assistant or configured planetarium,
then construct an immutable `ObservationPlan`. The custom target-observation
container nevertheless remained an ordinary `SequenceContainer`; it did not
publish an `InputTarget` through N.I.N.A.'s `IDeepSkyObjectContainer` contract.

ATR585M captures still used N.I.N.A.'s camera and `ImageSaveMediator`, and the
runner assigned target metadata immediately before enqueueing an image. This was
enough to produce an `OBJECT` value in some paths, but it did not make the
OpenAstroSpec container a N.I.N.A. target for native sequence items, conditions,
triggers, image-history semantics or other plugins. Commissioning helpers also
put run and frame-role labels into `OBJECT`, for example a value shaped like
`algol-<run>-science-3`, instead of retaining the stable scientific object name.

The active ATR585M N.I.N.A. profile compounded the issue. Its image-file pattern
contained date, image type, time, filter, sensor temperature, exposure and frame
number, but no `$$TARGETNAME$$`. Spectral observations were therefore stored in
one date/type directory with names that could not identify the requested target.
An empty spectrograph filter name also produced an unexplained double underscore.

The frozen automation baseline already requires requested target identity,
coordinates, common run ID and complete FITS provenance. It also makes N.I.N.A.
the sole ATR585M owner and the sequence orchestration surface. This decision
clarifies how those existing requirements are represented; it does not change
device ownership or authorize equipment operation.

## Decision

### N.I.N.A. target contract

The `OpenAstroSpec · UVEX4 target observation` container is a N.I.N.A. target
container. It exposes one `NINA.Astrometry.InputTarget` through
`IDeepSkyObjectContainer` and supplies N.I.N.A. nighttime data.

- The `InputTarget` is the live editable planning object used by N.I.N.A. and by
  the OpenAstroSpec user interface.
- The automatic-observation dock also holds its editable draft as an
  `InputTarget`; the plugin settings are its persisted defaults and audit
  fields, not a competing live coordinate object. A newly created advanced
  sequence container copies those defaults into its own native target snapshot.
- Existing user-visible name/RA/Dec properties may remain as compatibility
  proxies, but they must read and write that same `InputTarget`; they are not a
  second target store.
- Framing-assistant, planetarium, persisted-default and manual entry paths all
  update the dock's same target draft. As before, importing a new draft does not
  silently rewrite advanced sequence containers that the operator already made.
- At run start, the coordinator copies the target into an immutable J2000
  `ObservationPlan`. Later user-interface edits cannot alter an active run.
- The separate catalogue ID remains OpenAstroSpec provenance because N.I.N.A.
  3.2 planning sources do not always provide a stable independent catalogue ID.

### Stable scientific identity

`OBJECT` and `$$TARGETNAME$$` contain the stable, sanitized scientific target
name, such as `Algol`, `3C 273` or `NGC 6543`. They never contain a run ID, frame
number, probe/science suffix, retry number or quality decision.

Per-run and per-frame facts remain separate:

- `OBSRUNID`: immutable OpenAstroSpec observation run identifier;
- `UVEXSTG`: acquisition role such as `PROBE` or `SCIENCE`;
- `UVEXCID`: per-capture correlation identifier;
- `NIGHTSET`: locked Night Setup identifier;
- `CATALOG`: requested catalogue identifier when available;
- `IMAGETYP`: N.I.N.A. image type (`SNAPSHOT` for exposure probes and `LIGHT`
  for accepted-candidate science images);
- N.I.N.A. sequence title: root sequence identity where available.

The run manifest remains the authoritative cross-camera relationship and quality
record. FITS metadata makes each raw frame independently identifiable; it does
not replace the manifest.

### N.I.N.A. image-file pattern

The recommended ATR585M profile pattern is:

```text
$$DATEMINUS12$$\$$TARGETNAME$$\$$IMAGETYPE$$\$$DATETIME$$_$$TARGETNAME$$_$$EXPOSURETIME$$s_G$$GAIN$$_O$$OFFSET$$_$$FRAMENR$$
```

This uses N.I.N.A.'s native pattern expansion and produces target-separated
`SNAPSHOT` and `LIGHT` directories. The pattern deliberately omits `$$FILTER$$`
because the current UVEX spectral path has no N.I.N.A. filter-wheel role.

OpenAstroSpec validates the active profile before a real run:

- a missing `$$TARGETNAME$$` is a visible provenance problem;
- a missing target/type directory structure is reported as a recommendation;
- the plugin never silently rewrites a N.I.N.A. profile;
- an operator may invoke an explicit, reversible command to apply the recommended
  pattern after reviewing the before/after values;
- simulator execution may report the issue without blocking equipment-free UI
  work; real science acquisition fails closed if the target name could not be
  propagated to the image-save metadata.

### Save-time verification

Every retained ATR FITS is reopened read-only after N.I.N.A. reports it saved.
The runner verifies at least:

- the file exists at the absolute path returned by N.I.N.A.;
- `OBJECT` equals the canonical target name;
- `OBSRUNID`, `UVEXSTG`, `UVEXCID` and `NIGHTSET` equal the locked capture
  context;
- the N.I.N.A. image type agrees with probe/science role.

A metadata mismatch retains the immutable raw file and publishes failed evidence;
it does not rename, rewrite, move or delete the FITS. No subsequent frame is
claimed as accepted science until the mismatch is resolved.

## Consequences

### Positive

- Files can be found by target and role without opening every FITS.
- N.I.N.A. can display and consume the OpenAstroSpec target through its normal
  sequencer contracts.
- `OBJECT` becomes scientifically stable across all frames for one target.
- Run correlation and frame role remain machine-readable without polluting the
  target name.
- Other installations can reproduce the behavior using an ordinary N.I.N.A.
  profile pattern rather than a machine-specific post-save renamer.

### Costs and risks

- Existing sequence JSON must remain deserializable while scalar target fields
  migrate to `InputTarget` proxies.
- N.I.N.A. target objects use site-aware and epoch-aware types; conversion to the
  locked J2000 plan must remain explicit and tested.
- A profile may intentionally use a different file layout. The plugin therefore
  reports and offers an explicit repair instead of mutating it at startup.
- Existing files are not retroactively reorganized. Their run manifests are the
  preferred index; otherwise `OBJECT`/coordinate FITS headers must be inspected.

## Rejected alternatives

### Continue using only parallel OpenAstroSpec text fields

Rejected because it duplicates N.I.N.A. target state and prevents native target
conditions, triggers and exposure items from discovering the observation target.

### Rename or move raw FITS after saving

Rejected because raw observations are immutable and N.I.N.A.'s returned save
path, image history and manifest would diverge. N.I.N.A. must generate the final
path once through its official file-pattern mechanism.

### Encode the run and role in `OBJECT`

Rejected because `OBJECT` is scientific identity. Run ID, stage and correlation
ID have independent provenance fields.

### Rewrite every N.I.N.A. profile automatically

Rejected because profiles are user-owned, may serve other instruments and can be
active in another N.I.N.A. process. Changes require an explicit operator action
and a displayed before/after value.

## Relationship to earlier decisions

This ADR conforms to the frozen automation baseline and ADR-0001: N.I.N.A.
remains the sole ATR585M owner and `ImageSaveMediator` remains the only final
image-save route. It does not supersede any earlier ADR and does not alter the
hardware authorization or staged-commissioning rules.
