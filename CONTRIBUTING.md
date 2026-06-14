# Contributing

Thanks for your interest in improving **YC.EntityFrameworkCore.TigerData.TimescaleDB**. This document
explains how to build, test, and submit changes. Be respectful and constructive in all interactions.

## Prerequisites

- **.NET 10 SDK** — the exact version is pinned in [`global.json`](global.json).
- **Docker** — the functional tests start a real TimescaleDB container via Testcontainers.
- A POSIX shell or PowerShell; Git.

## Getting started

```bash
git clone https://github.com/yusuf-cirak/YC.EntityFrameworkCore.TigerData.TimescaleDB.git
cd YC.EntityFrameworkCore.TigerData.TimescaleDB

dotnet restore YC.EntityFrameworkCore.TigerData.TimescaleDB.slnx
dotnet build   YC.EntityFrameworkCore.TigerData.TimescaleDB.slnx -c Release
```

### Running the tests

```bash
# Fast: SQL-generation, model-building and migration-diff tests (no Docker)
dotnet test test/YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests

# End-to-end against a real TimescaleDB (Docker must be running)
dotnet test test/YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests
```

Both suites must be green before a PR is merged. New behavior needs tests — most changes belong in the
unit suite (assert on generated SQL); behavior that only a real database can prove (data preservation,
policy creation) belongs in the functional suite.

## Project layout

| Path | What |
|---|---|
| `src/…` | The provider extension (Fluent API, attributes, conventions, migrations SQL generator, differ, scaffolding). |
| `test/…UnitTests` | SQL-generation + model-building tests via `MigrationSqlHelper` / `TimescaleDbModelBuilder`. |
| `test/…FunctionalTests` | Testcontainers (TimescaleDB pg17) end-to-end tests. |
| `Directory.Packages.props` | **Central** package versions (Central Package Management) — add/bump versions here, not in csproj. |
| `Directory.Build.props` | Shared build settings, analyzers, package metadata. |
| `FEATURES.md` | Per-feature reference; update it when you add or change a feature. |

## Coding standards

- Nullable reference types are on; the build runs the .NET analyzers and **treats warnings as errors** —
  keep it clean (`dotnet build` must be warning-free).
- Match the surrounding style (naming, XML doc density). Public API gets XML docs (they ship in the
  package and drive IntelliSense).
- No magic-string intervals in the public surface — use `TimeSpan` / `(int, Every)` (see the existing
  Fluent API and attributes).

## Commit & PR conventions

This repo uses **[Conventional Commits](https://www.conventionalcommits.org/)** — they drive automatic
versioning and the changelog via [Release Please](https://github.com/googleapis/release-please).

- Use a typed, imperative **PR title** (squash-merge uses it as the commit): `feat: …`, `fix: …`,
  `docs: …`, `refactor: …`, `perf: …`, `test: …`, `ci: …`, `build: …`, `chore: …`.
- Breaking changes: add `!` (`feat!: …`) or a `BREAKING CHANGE:` footer.
- Open a PR against `master`, fill in the template, and link the issue with `Closes #NNN`.
- `feat` → minor bump, `fix` → patch, breaking → major (0.x: breaking → minor).

## How releases work

You don't bump versions or write the changelog by hand:

1. Merged commits on `master` are read by Release Please, which maintains a **release PR**
   (`chore(master): release X.Y.Z`) with the computed version + `CHANGELOG.md`.
2. A maintainer merges that PR → a `vX.Y.Z` tag + GitHub Release are created, and the package is built
   and **published to NuGet via OIDC Trusted Publishing** (no long-lived API key).

## Good first contributions

- A package icon (`PackageIcon`) is still a TODO — a clean SVG/PNG logo would be a welcome first PR.
- Improving `FEATURES.md` examples, adding tests for edge cases, or tightening XML docs.
