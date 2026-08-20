# Third-party software and data boundary

The repository's original OpenAstroSpec source (historically identified by the
`UVEX-ADV` compatibility name) is licensed under `GPL-3.0-only`. That license
does not replace the terms of independent software, hardware drivers, SDKs, catalogs,
templates, fonts/icons, or observation data used alongside it.

## External runtime applications

OpenAstroSpec Auto — UVEX4 integrates with independently installed applications and drivers,
including N.I.N.A., PHD2, ASCOM components, camera drivers, and device firmware. They are
not authored or relicensed by this repository. Obtain them from their upstream projects
or vendors and follow their licenses and hardware warranties.

## Package dependencies

The C# projects use .NET/NuGet dependencies declared in project files. Spectral Studio
uses the pinned Python packages declared in `reduction/pyproject.toml` and
`reduction/requirements-lock.txt`. Each dependency retains its own license. Before a
binary release, generate an inventory from the exact lock/asset files and include the
required notices; this document is a boundary statement, not that generated inventory.

## Vendor native SDKs

Native camera SDKs and driver binaries are intentionally ignored or copied only into
local build/install output. They are not covered by this project's GPL grant. Do not attach
them to a public release until their redistribution terms have been reviewed and
recorded. A source release should require users to install supported vendor software
separately when redistribution is uncertain.

## Scientific data and templates

Raw FITS observations, calibration libraries, generated products, local ISIS/stellar
templates, catalogs, and observer metadata are outside the source repository by default.
They retain the rights and attribution of their observers/providers. Test fixtures may
be committed only with documented provenance, permission/license, checksum, and any
necessary privacy-sensitive header removal.

## Trademarks

Product, project, and vendor names are used for interoperability and identification.
They remain the property of their respective owners; no endorsement is implied.
