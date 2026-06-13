# YC.EntityFrameworkCore.TigerData.TimescaleDB

[![Tests](https://img.shields.io/github/actions/workflow/status/yusufcirak/YC.EntityFrameworkCore.TigerData.TimescaleDB/ci.yml?branch=main&label=tests&logo=github)](https://github.com/yusufcirak/YC.EntityFrameworkCore.TigerData.TimescaleDB/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/YC.EntityFrameworkCore.TigerData.TimescaleDB?logo=nuget&label=nuget)](https://www.nuget.org/packages/YC.EntityFrameworkCore.TigerData.TimescaleDB)
[![Downloads](https://img.shields.io/nuget/dt/YC.EntityFrameworkCore.TigerData.TimescaleDB?logo=nuget&label=downloads)](https://www.nuget.org/packages/YC.EntityFrameworkCore.TigerData.TimescaleDB)
[![codecov](https://img.shields.io/codecov/c/github/yusufcirak/YC.EntityFrameworkCore.TigerData.TimescaleDB?logo=codecov)](https://codecov.io/gh/yusufcirak/YC.EntityFrameworkCore.TigerData.TimescaleDB)
[![Release](https://img.shields.io/github/v/release/yusufcirak/YC.EntityFrameworkCore.TigerData.TimescaleDB?sort=date&display_name=release&logo=github&label=release)](https://github.com/yusufcirak/YC.EntityFrameworkCore.TigerData.TimescaleDB/releases/latest)
[![Issues](https://img.shields.io/github/issues/yusufcirak/YC.EntityFrameworkCore.TigerData.TimescaleDB?logo=github)](https://github.com/yusufcirak/YC.EntityFrameworkCore.TigerData.TimescaleDB/issues)
[![License: MIT](https://img.shields.io/github/license/yusufcirak/YC.EntityFrameworkCore.TigerData.TimescaleDB)](#license)

First-class [TimescaleDB](https://www.tigerdata.com/) support for Entity Framework Core, layered on top of the
[Npgsql EF Core provider](https://www.npgsql.org/efcore/).

Configure hypertables, columnstore compression, continuous aggregates, retention/reorder policies and background
jobs with the **Fluent API or attributes** — everything flows through standard EF Core migrations (snapshot-diff,
fully bidirectional, no imperative commands). Query with TimescaleDB hyperfunctions (`time_bucket`, `first`/`last`,
gapfilling, histograms, UUIDv7) directly from LINQ, and reverse-engineer existing schemas with
`dotnet ef dbcontext scaffold`.

> **Requirements:** EF Core 10, `Npgsql.EntityFrameworkCore.PostgreSQL` 10, TimescaleDB **2.23+**. The modern
> declarative interface is used (`create_hypertable`, `ALTER TABLE … SET (timescaledb.…)`); there is no legacy
> fallback.

## Install

One package — design-time support (migration code generation, scaffolding for `dotnet ef`) is included:

```
dotnet add package YC.EntityFrameworkCore.TigerData.TimescaleDB
```

## Setup

Call `UseTimescaleDb()` **after** `UseNpgsql(...)`:

```csharp
services.AddDbContext<MetricsContext>(options => options
    .UseNpgsql(connectionString)
    .UseTimescaleDb());
```

| Option | Default | Effect |
|---|---|---|
| `o.CreateExtension(false)` | **true** | When true, the first migration emits `CREATE EXTENSION IF NOT EXISTS timescaledb;`. Disable when the extension is provisioned out of band (e.g. managed cloud). |
| `o.MigrateData(false)` | **true** | Model-wide default for [migration data operations](FEATURES.md#migration-options). Per-entity `WithMigrateData(...)` overrides it. |
| `o.RebuildData(false)` | **true** | Model-wide default — forbid data-copying rebuilds across all entities. Per-entity `WithRebuildData(...)` overrides it. |
| `o.AutoDecompress(false)` | **true** | Model-wide default for chunk decompression on columnstore disable. Per-entity `WithAutoDecompress(...)` overrides it. |

```csharp
options.UseNpgsql(cs).UseTimescaleDb(o => o.RebuildData(false));   // disable risky rebuilds everywhere
```

## Intervals

Every duration is expressed type-safely — **never a raw string**:

| Form | Use it for | Example | Becomes |
|---|---|---|---|
| `TimeSpan` | Fixed durations (days, hours, minutes, seconds) | `TimeSpan.FromDays(7)` | `INTERVAL '7 days'` |
| `(int value, Every unit)` | Calendar units a `TimeSpan` can't express | `(1, Every.Month)` | `INTERVAL '1 month'` |
| `long` (integer-time hypertables only) | Raw size in the column's own unit | `86_400` | `86400` |

`Every` values: `Second`, `Minute`, `Hour`, `Day`, `Week`, `Month`, `Year`. Use `(int, Every)` (or its attribute
form) whenever you need `Week`, `Month` or `Year` — those are calendar intervals with no fixed `TimeSpan`.

## Quick start

The same hypertable, configured two ways.

**Fluent API:**

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<Reading>(e =>
    {
        e.HasNoKey();                                   // raw time-series → keyless
        e.ToTable("readings");
        e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
        e.HasSpacePartition(x => x.DeviceId, partitions: 4);
        e.HasColumnstore(cs => cs
            .SegmentBy(x => x.DeviceId)
            .OrderByDescending(x => x.Time));
        e.HasColumnstorePolicy(after: TimeSpan.FromDays(7));
        e.HasRetentionPolicy(dropAfter: TimeSpan.FromDays(90));
    });
}
```

**Attributes (equivalent):**

```csharp
[Columnstore(CompressAfter = 7, CompressAfterUnit = Every.Day)]
[Retention(90, Every.Day)]
public class Reading
{
    [PartitionColumn(1, Every.Day)]      // declares the hypertable + chunk interval
    [OrderBy(0, Sort.Descending)]        // columnstore ordering
    public DateTimeOffset Time { get; set; }

    [SpacePartition(4)]
    [SegmentBy]                          // columnstore segment column
    public string DeviceId { get; set; } = null!;

    public double Value { get; set; }
}
```

There is **no `[Hypertable]` attribute** — the `[PartitionColumn]` marker *is* the hypertable declaration, so
compression / retention / dimensions cannot be written without one.

## Features

Every feature is documented in **[FEATURES.md](FEATURES.md)** with its Fluent/attribute syntax, defaults and
allowable values. Click through:

| Feature | What it does |
|---|---|
| [Hypertable](FEATURES.md#hypertable) | Partition a table by time into chunks — the core TimescaleDB primitive. |
| [Integer-time hypertable](FEATURES.md#integer-time-hypertable) | Partition by an integer column (epoch/sequence) with an integer-now function. |
| [Space dimensions](FEATURES.md#space-dimensions) | Hash sub-partitioning on one or more extra columns. |
| [Tablespaces](FEATURES.md#tablespaces) | Spread chunks across disks/tablespaces, round-robin. |
| [Chunk skipping](FEATURES.md#chunk-skipping) | Min/max range tracking to prune chunks at query time. |
| [Columnstore](FEATURES.md#columnstore) | Native compression with segment-by / order-by layout. |
| [Columnstore policy](FEATURES.md#columnstore-policy) | Auto-compress chunks older than an age. |
| [Retention policy](FEATURES.md#retention-policy) | Auto-drop chunks older than an age. |
| [Reorder policy](FEATURES.md#reorder-policy) | Periodically reorder chunks by an index. |
| [Continuous aggregates](FEATURES.md#continuous-aggregates) | Incrementally-materialized rollups over a hypertable. |
| [Refresh policy](FEATURES.md#refresh-policy) | Schedule continuous-aggregate refreshes. |
| [Hierarchical caggs](FEATURES.md#hierarchical-continuous-aggregates) | A cagg built on another cagg, ordered automatically. |
| [Background jobs](FEATURES.md#background-jobs) | Schedule custom stored procedures via `add_job`. |
| [Migration options](FEATURES.md#migration-options) | Toggle the automatic data ops (migrate / rebuild / decompress). |
| [Hyperfunctions in LINQ](FEATURES.md#hyperfunctions-in-linq) | `time_bucket`, gapfill, `first`/`last`, histogram, UUIDv7. |

## Migrations — snapshot-driven, no special commands

`dotnet ef migrations add` records the TimescaleDB configuration as **annotations** on the table, plus
`migrationBuilder.Sql(...)` for the objects that aren't tables (the extension, continuous aggregates, jobs,
reorder policies). There are **no imperative commands** like `ConvertToHypertable()` or `DisableColumnstore()`.

The provider's SQL generator turns the annotation **delta** into real DDL when the migration runs. Because the
annotations carry **both old and new** values, every change is **bidirectional automatically** — the `Down`
migration emits the exact reverse with no extra code, and re-running `migrations add` on an unchanged model
produces an **empty** migration.

### Transition matrix

Changes TimescaleDB cannot apply in place **rebuild the table** (`CREATE TABLE (LIKE … INCLUDING ALL)`, copy
every row, drop, rename) so **data is preserved** — nothing is rejected:

| Change in the model | Behavior |
|---|---|
| New table as hypertable | `CREATE TABLE …` then `create_hypertable(...)` |
| **Existing table → hypertable** | `create_hypertable(..., migrate_data => true)` |
| **Hypertable → plain table** | **rebuild** → plain table, data copied back |
| **Partition column changed** | **rebuild** → hypertable with the new column |
| Chunk interval changed | `set_chunk_time_interval(...)` (future chunks) |
| Space dimension added | `add_dimension(by_hash(...))` |
| **Space dimension removed / changed** | **rebuild** |
| Columnstore enabled | `ALTER TABLE … SET (timescaledb.enable_columnstore, …)` |
| **Columnstore disabled** | every compressed chunk decompressed, then disabled |
| segmentby / orderby changed | `ALTER TABLE … SET (...)` (future chunks) |
| Retention / columnstore / reorder policy ± | `add_*_policy` / `remove_*_policy` |
| Reorder policy ± | emitted after `CREATE INDEX` (add) / before drop (remove) |
| Chunk skipping ± | `SET timescaledb.enable_chunk_skipping = on; enable/disable_chunk_skipping` |
| Cagg query changed | drop + recreate (+ policies re-added, dependents cascaded) |

The automatic, data-heavy steps (`migrate_data`, the rebuild row-copy, columnstore decompression) can be toggled
per entity or model-wide — see [Migration options](FEATURES.md#migration-options).

## Scaffolding

`dotnet ef dbcontext scaffold` reverse-engineers hypertables, space dimensions, columnstore settings, retention/
columnstore/reorder policies, chunk skipping, tablespaces and custom jobs. **Continuous aggregates are not
reverse-engineered** (materialized views are outside the Npgsql scaffolding surface).

## Notes and limitations

- **Primary keys** on hypertables must include the partition column (TimescaleDB requires every unique constraint
  to cover the partitioning columns; validated at model-build time).
- **Rebuilds are data-heavy and not free of caveats.** Un-converting a hypertable, repartitioning, or changing a
  space dimension copies every row under lock via `CREATE TABLE (LIKE … INCLUDING ALL)`. PostgreSQL `LIKE` does
  **not** copy FOREIGN KEY constraints (inbound or outbound) and recreates indexes under generated names; the
  migration includes a warning comment. Re-create FKs and any reorder policy manually afterward.
- **Chunk interval changes apply to future chunks only** (`set_chunk_time_interval`); existing chunks keep their
  interval. Re-chunking historical data is intentionally not performed.
- **Chunk skipping** requires an ordered scalar column (integer/time, not floating point); the migration enables
  the `timescaledb.enable_chunk_skipping` GUC.
- Converting an existing table to a hypertable and disabling the columnstore are **data-heavy** (row migration /
  chunk decompression); review the generated migration before running it in production.
- Enabling the columnstore auto-creates a default conversion policy; `HasColumnstorePolicy` replaces it.
- **Direct Compress (Direct-to-Columnstore)** is a session-level ingestion GUC
  (`SET timescaledb.enable_direct_compress_insert/copy = on`), not a table option — enable it in the
  session doing the bulk `INSERT`/`COPY`. It has no declarative DDL form, so it is outside this package's
  migration scope (and is a TimescaleDB tech preview).

## License

MIT
