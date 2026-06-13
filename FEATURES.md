# Feature reference

Every feature below is configurable through the **Fluent API** and, where the value is a compile-time
constant, through **attributes**. Intervals are `TimeSpan` or `(int, Every)` — see
[Intervals](README.md#intervals). Back to the [README](README.md).

## Contents

- [Hypertable](#hypertable)
- [Integer-time hypertable](#integer-time-hypertable)
- [Space dimensions](#space-dimensions)
- [Tablespaces](#tablespaces)
- [Chunk skipping](#chunk-skipping)
- [Columnstore](#columnstore)
- [Columnstore policy](#columnstore-policy)
- [Retention policy](#retention-policy)
- [Reorder policy](#reorder-policy)
- [Continuous aggregates](#continuous-aggregates)
- [Refresh policy](#refresh-policy)
- [Hierarchical continuous aggregates](#hierarchical-continuous-aggregates)
- [Background jobs](#background-jobs)
- [Migration options](#migration-options)
- [Hyperfunctions in LINQ](#hyperfunctions-in-linq)

---

## Hypertable

**What it is:** the core TimescaleDB primitive — a regular table automatically partitioned into *chunks*
by a time (range) column, so inserts and time-range queries stay fast as the table grows. Marking the
partition column **is** the hypertable declaration; there is no `[Hypertable]` attribute.

```csharp
e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
e.IsHypertable(x => x.Time, chunkInterval: 1, chunkUnit: Every.Week);   // calendar unit
```
```csharp
[PartitionColumn(1, Every.Day)]
public DateTimeOffset Time { get; set; }
```

| Parameter | Default | Allowable |
|---|---|---|
| `chunkInterval` | TimescaleDB default **7 days** (omit / `0`) | any positive `TimeSpan`, or `(int, Every)` |
| `createDefaultIndexes` | **true** | `true` / `false` (TimescaleDB's automatic time index) |

The primary key (if any) **must include the partition column** — keyless entities are the natural fit.

## Integer-time hypertable

**What it is:** a hypertable partitioned by an **integer** column (epoch microseconds, a sequence, …)
instead of a timestamp. Time-based policies need an *integer-now* function so TimescaleDB knows "now" in
the column's own unit.

```csharp
e.IsHypertableByInteger(x => x.UnixMicros, chunkInterval: 86_400_000_000,
    integerNowFunction: "micros_now");
// policy ages then use the long overloads:
e.HasRetentionPolicy(7_776_000_000_000L);
```

The integer-now function is **required and validated** once any policy is configured.

## Space dimensions

**What it is:** optional **hash sub-partitioning** on one or more non-time columns (e.g. device, region),
so a single time range is spread across several chunks for parallelism. One time + N space dimensions.

```csharp
e.HasSpacePartition(x => x.DeviceId, partitions: 4);
e.HasSpacePartition(x => x.Region, partitions: 2);
```
```csharp
[SpacePartition(4)] public string DeviceId { get; set; } = null!;
[SpacePartition(2)] public string Region   { get; set; } = null!;
```

`partitions` must be **> 0**. Adding a dimension applies in place; **removing or changing** one rebuilds
the table (data preserved).

## Tablespaces

**What it is:** placement of a hypertable's chunks across one or more pre-existing PostgreSQL
**tablespaces** (on-disk locations), round-robin — used to spread I/O or capacity over multiple disks.

```csharp
public static readonly Tablespace FastSsd = new("fast_ssd");

e.HasTablespace(FastSsd);          // repeatable
```
```csharp
[Tablespace("fast_ssd")]           // repeatable
public class Reading { … }
```

The tablespace itself is a cluster-level object you create out of band (`CREATE TABLESPACE …`).
Attach/detach is in place and reversible.

## Chunk skipping

**What it is:** per-column **min/max range tracking** so the planner can skip whole chunks that can't
match a `WHERE` on that column — cheap pruning for monotonic or clustered columns.

```csharp
e.HasChunkSkipping(x => x.DeviceId);
```
```csharp
[ChunkSkipping] public long DeviceId { get; set; }
```

The column must be an **ordered scalar type** (integer or time — `double precision` is rejected by
TimescaleDB). The migration turns on the required `timescaledb.enable_chunk_skipping` GUC for you.

## Columnstore

**What it is:** TimescaleDB's native **compression** — chunks are stored column-oriented, grouped by
`segmentby` columns and sorted by `orderby`, typically 90 %+ smaller and faster for analytical scans.

```csharp
e.HasColumnstore(cs => cs
    .SegmentBy(x => x.DeviceId)
    .OrderByDescending(x => x.Time)
    .ThenBy(x => x.Value, Nulls.Last)
    .MergeChunksUpTo(TimeSpan.FromDays(7)));
```
```csharp
[Columnstore]
public class Reading
{
    [SegmentBy]                              public string DeviceId { get; set; } = null!;
    [OrderBy(0, Sort.Descending)]            public DateTimeOffset Time { get; set; }
}
```

Builder methods: `SegmentBy`, `OrderBy` / `OrderByDescending`, `ThenBy` / `ThenByDescending`,
`MergeChunksUpTo(TimeSpan | int, Every)`. Any `[SegmentBy]` or `[OrderBy]` attribute implicitly enables
the columnstore.

| Option | Default | Allowable |
|---|---|---|
| `OrderBy(..., nulls)` | `Nulls.Default` (PG default: NULLS LAST asc / NULLS FIRST desc) | `Nulls.Default` / `First` / `Last` |
| `[OrderBy(order, direction, nulls)]` | `order` 0, `Sort.Ascending`, `Nulls.Default` | `Sort.Ascending` / `Descending` |

## Columnstore policy

**What it is:** a background job that **automatically converts chunks to the columnstore** once they are
older than a given age (`add_columnstore_policy`).

```csharp
e.HasColumnstorePolicy(after: TimeSpan.FromDays(7));
e.HasColumnstorePolicy(after: 7, unit: Every.Day);
```
```csharp
[Columnstore(CompressAfter = 7, CompressAfterUnit = Every.Day,
             ScheduleInterval = 1, ScheduleIntervalUnit = Every.Hour)]
```

| Parameter | Default | Allowable |
|---|---|---|
| `after` / `CompressAfter` | none — required to add a policy (`0` = no policy) | `TimeSpan`, `(int, Every)`, or `long` (integer-time) |
| `scheduleInterval` | TimescaleDB default | `TimeSpan` / `(int, Every)` |
| `initialStart` | none | `DateTimeOffset` |
| `timezone` | none | IANA tz id (e.g. `"Europe/Istanbul"`) |

Enabling the columnstore auto-creates a default conversion policy; `HasColumnstorePolicy` replaces it
deterministically.

## Retention policy

**What it is:** a background job that **drops chunks older than a given age** (`add_retention_policy`) —
automatic time-based data expiry.

```csharp
e.HasRetentionPolicy(dropAfter: TimeSpan.FromDays(90));
e.HasRetentionPolicy(dropAfter: 90, unit: Every.Day);
```
```csharp
[Retention(90, Every.Day)]
```

| Parameter | Default | Allowable |
|---|---|---|
| `dropAfter` / `DropAfter` | required, **> 0** | `TimeSpan`, `(int, Every)`, or `long` (integer-time) |
| `scheduleInterval` / `ScheduleInterval` | **1 day** | `TimeSpan` / `(int, Every)` |
| `initialStart`, `timezone` | none | `DateTimeOffset`, IANA tz id |

## Reorder policy

**What it is:** a background job that periodically **reorders each chunk by an index**
(`add_reorder_policy`), keeping data physically clustered for faster range scans. **Fluent only** — it
references a database index by name.

```csharp
e.HasIndex(x => new { x.DeviceId, x.Time });           // the index must exist
e.HasReorderPolicy(x => new { x.DeviceId, x.Time });   // resolves its DB name
// or by explicit name:
e.HasReorderPolicy("readings_device_id_time_idx");
```

The policy is sequenced **after** the index is created, so it works on the very first migration. Like all
index-name-dependent features, it is not preserved across a table rebuild (see
[limitations](README.md#notes-and-limitations)).

## Continuous aggregates

**What it is:** an **incrementally materialized view** over a hypertable (a rollup such as hourly
averages) that TimescaleDB keeps up to date — query it like a table without re-scanning raw data. The
entity maps to the materialized view and is created / recreated / dropped through migrations.

```csharp
modelBuilder.Entity<HourlyAverage>(e =>
{
    e.HasNoKey();
    e.IsContinuousAggregate(
        "hourly_averages",
        """
        SELECT time_bucket(INTERVAL '1 hour', time) AS bucket,
               device_id,
               avg(value) AS average
        FROM readings
        GROUP BY 1, 2
        """);
    e.HasRefreshPolicy(startOffset: TimeSpan.FromDays(3), endOffset: TimeSpan.FromHours(1),
        scheduleInterval: TimeSpan.FromHours(1));
});
```

| `IsContinuousAggregate` parameter | Default | Notes |
|---|---|---|
| `materializedOnly` | **true** | `false` → real-time aggregation (union of materialized + raw) |
| `withNoData` | **true** | `false` → materialize on creation |
| `chunkInterval` | TimescaleDB default | `TimeSpan` |
| `schema` | none (default schema) | target schema for the view |

The query **must** aggregate by `time_bucket(...)` (validated). A continuous aggregate may also take
`HasColumnstore(...)` and `HasRetentionPolicy(...)`.

## Refresh policy

**What it is:** the schedule that **refreshes a continuous aggregate** over a sliding window
(`add_continuous_aggregate_policy`).

```csharp
e.HasRefreshPolicy(startOffset: TimeSpan.FromDays(3), endOffset: TimeSpan.FromHours(1),
    scheduleInterval: TimeSpan.FromHours(1));
```

Both offsets are **required together**; `scheduleInterval` defaults to **24 hours**. An `(int, int, Every)`
overload covers calendar units.

## Hierarchical continuous aggregates

**What it is:** a continuous aggregate whose query reads **another continuous aggregate's** view (e.g.
daily built on hourly). Migrations order create/drop by dependency automatically, and recreating a source
cascades to its dependents.

```csharp
e.IsContinuousAggregate("daily_averages",
    "SELECT time_bucket(INTERVAL '1 day', bucket) AS bucket, avg(average) AS average "
    + "FROM hourly_averages GROUP BY 1");
```

## Background jobs

**What it is:** a **user-defined scheduled job** (`add_job`) that runs a stored procedure on a cadence —
custom maintenance alongside the built-in policies. Created, altered and dropped through migrations,
keyed by name.

```csharp
modelBuilder.HasTimescaleDbJob(
    name: "nightly_cleanup",
    procedure: "public.cleanup",          // must already exist in the database
    scheduleInterval: TimeSpan.FromDays(1),
    config: """{"drop_after":"30 days"}""",
    maxRuntime: TimeSpan.FromMinutes(5),
    maxRetries: 3,
    retryPeriod: TimeSpan.FromMinutes(10));
```

| Parameter | Default (TimescaleDB) | Allowable |
|---|---|---|
| `scheduleInterval` | **24 hours** | `TimeSpan` |
| `config` | none | JSONB string |
| `fixedSchedule` | **true** | `true` / `false` (sliding interval) |
| `initialStart` | none | `DateTimeOffset` |
| `timezone` | **UTC** | IANA tz id |
| `maxRuntime` | none | `TimeSpan` |
| `maxRetries` | none | `int` |
| `retryPeriod` | none | `TimeSpan` |

## Migration options

**What it is:** per-entity (and model-wide) toggles for the three automatic, data-heavy operations the
migration engine injects. All default **on** (current behavior); a per-entity setting wins over the
`UseTimescaleDb` model-wide default.

```csharp
e.WithMigrateData(false);      // don't migrate existing rows on plain → hypertable
e.WithRebuildData(false);      // forbid data-copying rebuilds (throw instead)
e.WithAutoDecompress(false);   // don't decompress chunks when disabling the columnstore
```
```csharp
[MigrationOptions(MigrateData = false, RebuildData = false, AutoDecompress = false)]
public class Reading { … }
```
```csharp
// Model-wide default for every entity (override per entity with the With* methods above):
options.UseTimescaleDb(o => o.MigrateData(false).RebuildData(false).AutoDecompress(false));
```

| Toggle | Default | Off behavior |
|---|---|---|
| `WithMigrateData` | **true** | Plain→hypertable emits `migrate_data => false` — TimescaleDB rejects a non-empty table, so use only for empty tables or manual migration. |
| `WithRebuildData` | **true** | A change needing a data-copying rebuild (hypertable→plain, partition column change, space dimension drop/repartition) **throws at migration generation** instead of rebuilding. |
| `WithAutoDecompress` | **true** | Disabling the columnstore skips the per-chunk decompression — TimescaleDB errors if any chunk is still compressed. |

## Hyperfunctions in LINQ

**What it is:** TimescaleDB's analytical SQL functions exposed on `EF.Functions`, translated to SQL (no
in-memory execution) so they compose in LINQ queries.

| Method | Signature | SQL |
|---|---|---|
| `TimeBucket` | `(TimeSpan \| string width, DateTime \| DateTimeOffset \| DateOnly)` | `time_bucket(width, ts)` |
| `TimeBucketGapfill` | `(TimeSpan \| string width, DateTime \| DateTimeOffset)` | `time_bucket_gapfill(width, ts)` |
| `Locf<T>` | `(T value)` | `locf(value)` — last observation carried forward |
| `Interpolate<T>` | `(T value)` | `interpolate(value)` — linear interpolation |
| `First` | `(IEnumerable<(TValue, TTime)>)` | `first(value, time)` |
| `Last` | `(IEnumerable<(TValue, TTime)>)` | `last(value, time)` |
| `Histogram` | `(IEnumerable<double> values, double min, double max, int buckets)` | `histogram(value, min, max, nbuckets)` |
| `GenerateUuid7` | `()` | `generate_uuidv7()` |
| `ToUuid7` | `(DateTimeOffset)` | `to_uuidv7(ts)` |
| `UuidTimestamp` | `(Guid)` | `uuid_timestamp(uuid)` |

```csharp
var buckets = await db.Readings
    .GroupBy(r => EF.Functions.TimeBucket(TimeSpan.FromMinutes(15), r.Time))
    .Select(g => new
    {
        Bucket = g.Key,
        Avg = g.Average(x => x.Value),
        First = EF.Functions.First(g.Select(x => ValueTuple.Create(x.Value, x.Time))),
        Last = EF.Functions.Last(g.Select(x => ValueTuple.Create(x.Value, x.Time))),
    })
    .ToListAsync();
```
