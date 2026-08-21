# OpenAstroSpec brand and compatibility naming

**Status:** Accepted public-brand policy  
**Decision date:** 2026-08-20  
**Scope:** Repository presentation, documentation, application titles, N.I.N.A. labels,
installer-facing descriptions, and future public release names  
**Does not change:** Observatory architecture, device ownership, motion authority,
commissioning contracts, persistent schemas, or historical evidence

## 1. Canonical public name

The canonical public name of this project is:

> **OpenAstroSpec Auto — UVEX4**

The name is intentionally layered:

- **OpenAstroSpec** is the open-source astronomical-spectroscopy project family;
- **Auto** is the automated observing and observatory-orchestration product line;
- **UVEX4** is the first supported spectrograph implementation, not the permanent
  boundary of the project family.

The preferred future GitHub repository slug is:

```text
openastrospec-auto-uvex4
```

The short identifier for prose, release artifacts, and issue labels is
`OASA-UVEX4`. It is a convenience abbreviation, not a replacement for the full
name on first use.

The canonical one-line descriptions are:

> Open-source automation for astronomical spectroscopy, with UVEX4 as the first
> fully supported spectrograph implementation.

> 面向天文光谱的开源自动观测系统，UVEX4 是首个完整支持的光谱仪实现。

## 2. Product-family names

This repository currently ships two user-facing products:

| Product | Canonical display name | Purpose |
|---|---|---|
| Acquisition and control | **OpenAstroSpec Auto — UVEX4** | N.I.N.A. orchestration, equipment services, commissioning, slit acquisition, guiding, spectral acquisition, recovery, and immutable evidence |
| Offline reduction | **OpenAstroSpec Spectral Studio — UVEX4** | Offline FITS inspection, reduction, calibration, visualization, and delivery |

Short UI surfaces may use `OpenAstroSpec Auto`, `OpenAstroSpec 自动观测`, and
`OpenAstroSpec 校准库` when the full name would crowd a tab. ATR spectral previews
and one-frame checks live inside the automatic-observation surface rather than in a
separate product-named dock. Hardware-action labels continue to say `UVEX4` or `UVEX` where they identify
the physical spectrograph rather than the software brand.

Future instrument implementations should keep the family and product-line names
and replace only the instrument qualifier, for example:

```text
OpenAstroSpec Auto — ALPY
OpenAstroSpec Auto — LHIRES III
```

This naming pattern does not promise that those implementations exist today.

## 3. Identity and affiliation statement

The preferred public identity statement is:

> OpenAstroSpec Auto is an independent community open-source project for
> astronomical spectroscopy. It is not an official release of the UVEX4 project
> or N.I.N.A.

UVEX4, the ground-based spectrograph supported here, is unrelated to NASA's UVEX
(`UltraViolet EXplorer`) space mission. This clarification belongs in naming/FAQ
material when useful; it need not dominate the main product screen.

Always use the full compound brand on first reference. Avoid publishing the
software under the bare names `UVEX`, `UVEX Automation`, or `AstroSpec`, because
those names are ambiguous outside the repository.

## 4. Current migration phase: display names only

The current migration changes only names that people use to recognize the project:

- repository and product README titles;
- N.I.N.A. plugin title, options-page title, dock titles, sequencer category,
  sequence-item progress source, and the target-observation container name;
- Manager and Spectral Studio window titles and headings;
- product-manifest display names and human entry-point descriptions;
- new shortcut labels and installer-facing descriptions where changing them does
  not alter an operating-system identity;
- public release, contribution, security, and operator documentation.

The migration must not alter behavior, authorization, device selection, schema
validation, motion limits, or raw-data provenance.

## 5. Compatibility identifiers retained as `UVEX-ADV` / `UvexAdv`

The following identifiers remain unchanged until a separately designed,
versioned, backward-compatible migration is approved:

- C# namespaces, project names, assembly file names, executable names, and the
  solution file, including `UvexAdv.*` and `UVEX-ADV.sln`;
- the N.I.N.A. plugin GUID and all persisted N.I.N.A. profile keys;
- Windows service **names** such as `UVEX-ADV`, `UVEX-ADV-QHY`, and
  `UVEX-ADV-PHD2-WATCHDOG`;
- installation and data paths such as `%ProgramData%\UVEX-ADV`,
  `%LOCALAPPDATA%\UVEX-ADV`, and existing N.I.N.A. plugin directories;
- configuration property names, environment variables, JSON schema fields,
  type discriminators, database names, API service identifiers, and loopback
  endpoint contracts;
- FITS keywords/history, pipeline identifiers, run manifests, checksums, audit
  events, calibration IDs, and all historical evidence;
- frozen design records and ADR text that records the historical project name;
- existing command/script filenames whose rename would break operator bookmarks,
  automation, or published instructions.

These strings may remain visible in a path, log, service console, FITS header, or
compatibility diagnostic. They identify an installed/persisted technical object;
they are not the current public brand. User documentation should label them as
legacy compatibility identifiers instead of pretending they have already migrated.

## 6. Deferred internal migration

A future breaking release may introduce namespaces such as
`OpenAstroSpec.Auto.*` and an instrument adapter such as
`OpenAstroSpec.Auto.Uvex4`, but only after it defines and tests:

1. side-by-side plugin detection and GUID continuity;
2. N.I.N.A. profile-key migration;
3. Windows service and recovery behavior;
4. ProgramData/LocalAppData compatibility reads;
5. evidence, FITS, database, and commissioning-schema compatibility;
6. upgrade, rollback, and uninstall behavior;
7. links from the old GitHub repository and release assets.

Until that work exists, a global search-and-replace of `UVEX-ADV` or `UvexAdv` is
explicitly prohibited.

## 7. Release and UI acceptance

Every public release must check that:

- the first README heading uses **OpenAstroSpec Auto — UVEX4**;
- the N.I.N.A. plugin and its primary automatic-observation surface use the new
  public brand while preserving the plugin GUID;
- narrow N.I.N.A. tabs use the approved short labels without clipping;
- Manager and Spectral Studio title bars use their canonical display names;
- paths and service names retained for compatibility are documented accurately;
- frozen-design hash verification still passes;
- the complete build/test suite passes;
- no rename rewrites immutable observations or historical evidence.

The public rename is complete for a surface only after the real rendered UI or
published document has been inspected; a source-string replacement alone is not
visual acceptance.
