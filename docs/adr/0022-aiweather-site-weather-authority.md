# ADR-0022: AIWeather site weather authority without a forecast adapter

**Status:** Accepted; narrowly supersedes ADR-0010's mandatory independent Weather adapter and ADR-0021's corresponding requirement.  
**Date:** 2026-10-10  
**Authority:** Owner explicitly requested removal of the Open-Meteo weather veto, selected no Weather source, and requested continuation with AIWeather roof safety.

## Evidence and problem

At 01:38, the native sequence stopped before roof opening: Open-Meteo reported
67% cloud against the independent 40% gate, while the selected AIWeather monitor
reported Safe. This establishes disagreement, not proof that a particular sky
classification is correct. At 01:40, selecting No Device then failed the static
four-adapter requirement. A network forecast is not an on-site rain/cloud sensor.

## Decision

Add an explicit `AiWeatherSafetyStack` preparation choice. The immutable,
SHA-256-bound commissioning package and Profile must both declare
`RequireWeatherData=false`; `RequireSafetyMonitor`, roof and cover remain true,
and weak supervision remains false. Runtime selections must be exactly
`AIWeatherSafetyMonitor` plus no Weather device. No missing-adapter automatic
fallback is permitted. Old packages still require their original weather source.

AIWeather remains the sole owner of its combined site verdict, including source
freshness, replica health and any configured external sensor veto. OpenAstroSpec
reads its native N.I.N.A. mediator, never duplicates its network protocol or
fabricates rain/wind readings. Identity, Connected and IsSafe are checked before
actions and through the existing night safety subscription. Unsafe, unknown or
disconnected state retains stop/checked closure. No cloud threshold in AIWeather
is changed. A sky camera is not claimed to measure wind or replace a rain sensor;
the owner-selected policy accepts that coverage limitation.

Independent Weather mode remains the default, with its existing rain/cloud/wind
checks. Enabling this new mode while retaining any Weather selection is a
configuration error, not permission to ignore a sensor. Run-time selection drift
blocks; adoption is only at the idle boundary via a new immutable run/package.

Roof authority, controller interlocks, dawn/deadline, all-device ownership,
motion accounting and science-quality checks are unchanged. Test standby keeps
its live safety subscription and still closes on unsafe/deadline/dawn.

## Verification and rollback

Test explicit policy and exact selection, old-package rejection, missing monitor,
roof/cover and unsafe/disconnected paths. Install the exact tested artifact and
inspect the actual preparation/sequence panels before restarting via the native
sequence. Software tests do not certify rain/wind coverage or a successful night.
Rollback restores the previous immutable package and independent Weather choice
only outside an active run; it never edits observation evidence.
