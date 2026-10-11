# ADR-0025: Reusable observing-night clock deadlines

Status: Accepted; source/offline verification is separate from installation and real-sky acceptance.

Date: 2026-10-10

Owner authorization: change the previously discussed design and update the advanced sequences and templates to omit mandatory dates.

## Scope and decision

Extends ADR-0019 / baseline section 7 only in saved schedule representation and
deadline resolution. No device ownership, weather authority, roof policy, science
acceptance, motion budget or recovery authority changes.

- Keep the start instruction as N.I.N.A.'s native `WaitForTime`; do not add a
  second start scheduler to the spectroscopy container. Its native rollover
  behavior stays native. An observing sequence is not an OS scheduled task.
- The night editor accepts optional local `HH:mm` or `HH:mm:ss` cutoff text.
  The observatory PC's time zone supplies the offset. Reusable templates use
  `05:00`; the owner's early/late plans use `00:30` / `05:00`.
- Resolve the cutoff **once when the night container executes**, before reserving
  equipment or creating target runners. The observing date is the local date
  at/after noon, or the previous date before noon. Times before 12:00 belong to
  the following calendar day. This fixed noon boundary deliberately differs
  from the native start instruction's sunrise rollover: a post-dawn retry of
  `05:00` must remain expired until the next observing night, not grant another day.
- An expired cutoff remains expired; never find the next future occurrence.
  Target transitions, clouds, re-selection, calibration, meridian continuation,
  UI edits and standby reuse the same locked UTC instant. Only explicitly
  starting a new night resolves a new schedule. This does not reset finished
  native items or authorize automatic resume after a restart.
- Preserve the requested text, resolution UTC timestamp, time zone, observing
  date and resolved UTC cutoff in the night journal. Runtime values are not
  serialized into reusable templates or copied to another night.
- Reject DST-invalid or ambiguous cutoff times before equipment activity. The
  operator may supply an explicit offset-bearing timestamp for that exceptional
  night. Existing offset-bearing dates remain absolute, including expired dates;
  no silent migration to a daily recurring cutoff. Blank retains no-deadline
  behavior, but remains invalid with hold-open standby.
- Retain the serialized `EndAtIso8601` property name for compatibility, despite
  its historical name. Values may now contain clock text. An older plugin
  rejects this text through its existing validation rather than silently
  ignoring a new property and losing the cutoff. Clock-only sequences require
  0.4.0.261 or later. Do not erase the field to make an old version accept them.

## Unchanged boundaries

Deadline and morning stops occur at the existing stage/saved-frame boundaries;
no new truncation of an already executing normal exposure. AIWeather Unsafe
retains immediate stop and commissioned closeout authority. Target failures,
explicit cancellation, hold-open behavior and checked Home/Park/roof ordering
are unchanged. Two separate early/late files do not automatically hand off a
single safety subscription or promise to keep the roof open between them.

Removing calendar dates does not make the target coordinates suitable for all
seasons. Native observability/horizon and morning checks still apply. Local
station bindings and raw products remain outside Git.

## Verification

Required: evening/midnight/morning and elapsed cutoffs, explicit next-night reuse,
month/year/leap-day rollover, offsets/DST, blank and malformed text, native JSON
round trips and clone isolation, immutable in-run cutoff, journal provenance,
and retained safety/cancellation/standby tests. Update both distributed templates
and the two owner-selected plans without altering target data or hardware state.
