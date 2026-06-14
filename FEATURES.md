# Feature reference

Every feature is configurable through the **Fluent API** and, where the value is a compile-time
constant, through **attributes**. Intervals are `TimeSpan` or `(int, Every)` — see
[Intervals](README.md#intervals). Back to the [README](README.md).

All examples below build on the **one shared model** in the next section, so you can see exactly where
each call goes and what it operates on.

## Contents

- [The example model](#the-example-model)
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

## The example model

A single time-series entity and the `DbContext` that maps it. Every feature section below adds to the
`modelBuilder.Entity<Reading>(e => { ... })` block or annotates the `Reading` class — nothing else
changes.

```csharp
using Microsoft.EntityFrameworkCore;

// One row of raw time-series data (e.g. a sensor reading).
// Keyless: time-series rows have no natural primary key, and a hypertable's
// primary key must include the partition column anyway — so HasNoKey() is the
// natural fit. Map a key only if you genuinely need one.
public class Reading
{
    public DateTimeOffset Time { get; set; }       // the TIME (range) partition column
    public string DeviceId { get; set; } = null!;  // which device produced the reading
    public string Region { get; set; } = null!;    // where the device is located
    public double Value { get; set; }              // the measured value
}

public class MetricsDbContext(DbContextOptions<MetricsDbContext> options) : DbContext(options)
{
    public DbSet<Reading> Readings => Set<Reading>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Reading>(e =>
        {
            e.HasNoKey();              // raw time-series → no primary key
            e.ToTable("readings");

            // Turn the table into a hypertable, partitioned by Time into 1-day chunks.
            // This single call is what makes it a hypertable — everything else below
            // is layered on top of it.
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
        });
    }
}
```

Registration (e.g. in `Program.cs`) — `UseTimescaleDb()` must come **after** `UseNpgsql()`:

```csharp
builder.Services.AddDbContext<MetricsDbContext>(options => options
    .UseNpgsql(connectionString)
    .UseTimescaleDb());
```

The same model expressed entirely with **attributes** (no `OnModelCreating` needed for these):

```csharp
// The [PartitionColumn] marker IS the hypertable declaration — there is no [Hypertable].
public class Reading
{
    [PartitionColumn(1, Every.Day)]                 // hypertable, 1-day chunks, partitioned by Time
    public DateTimeOffset Time { get; set; }

    public string DeviceId { get; set; } = null!;
    public string Region { get; set; } = null!;
    public double Value { get; set; }
}
```

---

## Hypertable

**What it is:** the core TimescaleDB primitive — a regular table automatically partitioned into *chunks*
by a time (range) column, so inserts and time-range queries stay fast as the table grows. Marking the
partition column **is** the hypertable declaration; there is no `[Hypertable]` attribute.

```csharp
modelBuilder.Entity<Reading>(e =>
{
    e.HasNoKey();
    e.ToTable("readings");

    // Partition by the Time column. chunkInterval sizes each chunk; aim for the
    // most-recent chunk to fit comfortably in memory.
    e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));

    // Calendar units a fixed TimeSpan can't express use the (value, unit) overload:
    // e.IsHypertable(x => x.Time, chunkInterval: 1, chunkUnit: Every.Week);
});
```

Attribute form (on the partition property):

```csharp
[PartitionColumn(1, Every.Day)]            // value + calendar unit → 1-day chunks
public DateTimeOffset Time { get; set; }
```

| Parameter | Default | Allowable |
|---|---|---|
| `chunkInterval` | TimescaleDB default **7 days** (omit / `0`) | any positive `TimeSpan`, or `(int, Every)` |
| `createDefaultIndexes` | **true** | `true` / `false` (TimescaleDB's automatic time index) |

The primary key (if any) **must include the partition column** — keyless entities are the natural fit.

> **Automatic chunk exclusion:** once a table is a hypertable, queries that filter on the **partition
> columns** — the time column and any [space dimensions](#space-dimensions) — automatically skip chunks
> that can't match. No configuration. [Chunk skipping](#chunk-skipping) is the opt-in extension of this
> to a *non-partition* column.

## Integer-time hypertable

**What it is:** a hypertable partitioned by an **integer** column (epoch microseconds, a sequence, …)
instead of a timestamp. Time-based policies need an *integer-now* function so TimescaleDB knows "now" in
the column's own unit.

```csharp
// A reading whose time is stored as Unix microseconds (bigint), not a timestamp.
public class IntReading
{
    public long UnixMicros { get; set; }   // integer "time" column
    public double Value { get; set; }
}

modelBuilder.Entity<IntReading>(e =>
{
    e.HasNoKey();
    e.ToTable("int_readings");

    // chunkInterval is a RAW number in the column's own unit (here: 1 day = 86_400_000_000 µs).
    // integerNowFunction is a SQL function returning "now" in that same unit — required once
    // you add any policy (retention/columnstore), and validated at model build.
    e.IsHypertableByInteger(x => x.UnixMicros,
        chunkInterval: 86_400_000_000,
        integerNowFunction: "micros_now");

    // Policy ages are also raw numbers for integer hypertables — use the long overloads:
    e.HasRetentionPolicy(7_776_000_000_000L);   // drop after 90 days, in microseconds
});
```

## Space dimensions

**What it is:** optional **hash sub-partitioning** on one or more non-time columns (e.g. device, region),
so a single time range is spread across several chunks for parallelism. One time + N space dimensions.

```csharp
modelBuilder.Entity<Reading>(e =>
{
    e.HasNoKey();
    e.ToTable("readings");
    e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));

    // Add hash (space) sub-partitions. Call once per column; each column must
    // differ from the time column. Good candidates: columns you filter/group by.
    e.HasSpacePartition(x => x.DeviceId, partitions: 4);   // 4 hash buckets on DeviceId
    e.HasSpacePartition(x => x.Region, partitions: 2);     // + 2 hash buckets on Region
});
```

Attribute form (one attribute per column):

```csharp
[SpacePartition(4)] public string DeviceId { get; set; } = null!;
[SpacePartition(2)] public string Region { get; set; } = null!;
```

`partitions` must be **> 0**. Adding a dimension applies in place; **removing or changing** one rebuilds
the table (data preserved). Beyond write parallelism, equality filters on a space dimension also benefit
from [automatic chunk exclusion](#hypertable) — querying `WHERE device_id = 'd1'` prunes chunks.

## Tablespaces

**What it is:** placement of a hypertable's chunks across one or more pre-existing PostgreSQL
**tablespaces** (on-disk locations), round-robin — used to spread I/O or capacity over multiple disks.

```csharp
// Declare your tablespaces once as strongly-typed values (refactor-safe, no magic strings).
// The tablespaces must already exist: CREATE TABLESPACE fast_ssd LOCATION '/mnt/nvme';
public static class Tablespaces
{
    public static readonly Tablespace FastSsd = new("fast_ssd");
    public static readonly Tablespace ArchiveHdd = new("archive_hdd");
}

modelBuilder.Entity<Reading>(e =>
{
    e.HasNoKey();
    e.ToTable("readings");
    e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));

    // New chunks are distributed round-robin across the attached tablespaces.
    e.HasTablespace(Tablespaces.FastSsd);       // repeatable
    e.HasTablespace(Tablespaces.ArchiveHdd);
});
```

Attribute form (repeatable, on the class):

```csharp
[Tablespace("fast_ssd")]
[Tablespace("archive_hdd")]
public class Reading { /* ... */ }
```

The tablespace itself is a cluster-level object you create out of band (`CREATE TABLESPACE …`).
Attach/detach is in place and reversible.

## Chunk skipping

**What it is:** an **opt-in** extension of [automatic chunk exclusion](#hypertable) to a
**non-partition** column. TimescaleDB tracks each chunk's min/max for the column and skips chunks that
can't match a range filter on it.

> Filtering on the time column or a [space dimension](#space-dimensions) is **already** pruned
> automatically — reach for chunk skipping only for a *secondary* column you also filter on, ideally one
> **correlated with time** (e.g. a monotonically-growing id). It is pointless on a partition column.

**It only speeds up compressed chunks** — the min/max is gathered when a chunk is converted to the
[columnstore](#columnstore). Enable the columnstore too, or chunk skipping has no query effect (the call
still succeeds).

```csharp
// A reading with a monotonically-increasing sequence id (bigint), correlated with time.
public class SeqReading
{
    public DateTimeOffset Time { get; set; }
    public long Sequence { get; set; }
    public double Value { get; set; }
}

modelBuilder.Entity<SeqReading>(e =>
{
    e.HasNoKey();
    e.ToTable("seq_readings");
    e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));

    // Chunk skipping only helps COMPRESSED chunks, so enable the columnstore:
    e.HasColumnstore(cs => cs.OrderByDescending(x => x.Time));

    // Track per-chunk min/max of Sequence so range filters on it skip chunks.
    e.HasChunkSkipping(x => x.Sequence);
});
```

Attribute form (pair with `[Columnstore]`):

```csharp
[Columnstore]
public class SeqReading
{
    [PartitionColumn(1, Every.Day)] public DateTimeOffset Time { get; set; }
    [ChunkSkipping]                 public long Sequence { get; set; }   // non-partition, time-correlated
    public double Value { get; set; }
}
```

Supported column types: `smallint`, `int`, `bigint`, `serial`, `bigserial`, `date`, `timestamp`,
`timestamptz` (no floating-point or text). The migration turns on the required
`timescaledb.enable_chunk_skipping` GUC for you.

## Columnstore

**What it is:** TimescaleDB's native **compression** — chunks are stored column-oriented, grouped by
`segmentby` columns and sorted by `orderby`, typically 90 %+ smaller and faster for analytical scans.

```csharp
modelBuilder.Entity<Reading>(e =>
{
    e.HasNoKey();
    e.ToTable("readings");
    e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));

    e.HasColumnstore(cs => cs
        // segmentby: columns you filter by EQUALITY (low-cardinality grouping keys).
        .SegmentBy(x => x.DeviceId)
        // orderby: how rows are sorted inside a compressed chunk — usually time descending
        // so the newest rows decompress first.
        .OrderByDescending(x => x.Time)
        // additional ordering column; control NULL placement explicitly if needed.
        .ThenBy(x => x.Value, Nulls.Last)
        // optionally merge small chunks into larger ones when compressing.
        .MergeChunksUpTo(TimeSpan.FromDays(7)));
});
```

Attribute form — any `[SegmentBy]` or `[OrderBy]` implicitly enables the columnstore:

```csharp
[Columnstore]                               // optional here; the markers below already enable it
public class Reading
{
    [OrderBy(0, Sort.Descending)]            // orderby = Time DESC
    public DateTimeOffset Time { get; set; }

    [SegmentBy]                              // segmentby = DeviceId
    public string DeviceId { get; set; } = null!;

    public double Value { get; set; }
}
```

A column must not be in both `segmentby` and `orderby`.

| Option | Default | Allowable |
|---|---|---|
| `OrderBy(..., nulls)` | `Nulls.Default` (PG default: NULLS LAST asc / NULLS FIRST desc) | `Nulls.Default` / `First` / `Last` |
| `[OrderBy(order, direction, nulls)]` | `order` 0, `Sort.Ascending`, `Nulls.Default` | `Sort.Ascending` / `Descending` |

## Columnstore policy

**What it is:** a background job that **automatically converts chunks to the columnstore** once they are
older than a given age (`add_columnstore_policy`).

```csharp
modelBuilder.Entity<Reading>(e =>
{
    e.HasNoKey();
    e.ToTable("readings");
    e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
    e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId).OrderByDescending(x => x.Time));

    // Compress chunks once their data is older than 7 days. Two equivalent forms:
    e.HasColumnstorePolicy(after: TimeSpan.FromDays(7));   // fixed duration
    // e.HasColumnstorePolicy(after: 1, unit: Every.Month); // calendar unit
});
```

Attribute form (on the class, alongside the columnstore markers):

```csharp
[Columnstore(CompressAfter = 7, CompressAfterUnit = Every.Day,   // compress after 7 days
             ScheduleInterval = 1, ScheduleIntervalUnit = Every.Hour)]  // job runs hourly
public class Reading { /* ... [SegmentBy] / [OrderBy] ... */ }
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
modelBuilder.Entity<Reading>(e =>
{
    e.HasNoKey();
    e.ToTable("readings");
    e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));

    // Automatically delete whole chunks once their data is older than 90 days.
    // This is fast and irreversible — pick an age safely beyond anything you still query.
    e.HasRetentionPolicy(dropAfter: TimeSpan.FromDays(90));
    // e.HasRetentionPolicy(dropAfter: 90, unit: Every.Day);  // equivalent (calendar) form
});
```

Attribute form:

```csharp
[Retention(90, Every.Day)]
public class Reading { /* ... */ }
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
modelBuilder.Entity<Reading>(e =>
{
    e.HasNoKey();
    e.ToTable("readings");
    e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));

    // The index must exist — declare it first with the normal EF Core API.
    e.HasIndex(x => new { x.DeviceId, x.Time });

    // Reorder each chunk by that index. Resolve it by the same property set...
    e.HasReorderPolicy(x => new { x.DeviceId, x.Time });
    // ...or by its explicit database name:
    // e.HasReorderPolicy("ix_readings_device_id_time");
});
```

The policy is sequenced **after** the index is created, so it works on the very first migration. Like all
index-name-dependent features, it is not preserved across a table rebuild (see
[limitations](README.md#notes-and-limitations)).

## Continuous aggregates

**What it is:** an **incrementally materialized view** over a hypertable (a rollup such as hourly
averages) that TimescaleDB keeps up to date — query it like a table without re-scanning raw data. The
entity maps to the materialized view and is created / recreated / dropped through migrations.

```csharp
// A read model that maps to the materialized view (one row per bucket per device).
public class HourlyAverage
{
    public DateTimeOffset Bucket { get; set; }   // the time_bucket
    public string DeviceId { get; set; } = null!;
    public double Average { get; set; }
}

modelBuilder.Entity<HourlyAverage>(e =>
{
    e.HasNoKey();

    // Map the entity to a continuous aggregate. The query MUST aggregate by time_bucket(...).
    e.IsContinuousAggregate(
        viewName: "hourly_averages",
        query: """
        SELECT time_bucket(INTERVAL '1 hour', time) AS bucket,
               device_id,
               avg(value) AS average
        FROM readings
        GROUP BY 1, 2
        """);

    // Keep it refreshed (see "Refresh policy" below).
    e.HasRefreshPolicy(startOffset: TimeSpan.FromDays(3),
                       endOffset: TimeSpan.FromHours(1),
                       scheduleInterval: TimeSpan.FromHours(1));
});

// Query it like any keyless entity:
// var rows = await db.Set<HourlyAverage>().Where(x => x.DeviceId == "d1").ToListAsync();
```

| `IsContinuousAggregate` parameter | Default | Notes |
|---|---|---|
| `materializedOnly` | **true** | `false` → real-time aggregation (union of materialized + raw) |
| `withNoData` | **true** | `false` → materialize on creation |
| `chunkInterval` | TimescaleDB default | `TimeSpan` |
| `schema` | none (default schema) | target schema for the view |

A continuous aggregate may also take `HasColumnstore(...)` and `HasRetentionPolicy(...)`.

## Refresh policy

**What it is:** the schedule that **refreshes a continuous aggregate** over a sliding window
(`add_continuous_aggregate_policy`).

```csharp
// On a continuous-aggregate entity: refresh the window from 3 days ago up to 1 hour ago,
// every hour. Both offsets are REQUIRED together.
e.HasRefreshPolicy(startOffset: TimeSpan.FromDays(3),
                   endOffset: TimeSpan.FromHours(1),
                   scheduleInterval: TimeSpan.FromHours(1));   // defaults to 24h if omitted
```

An `(int, int, Every)` overload covers calendar units.

## Hierarchical continuous aggregates

**What it is:** a continuous aggregate whose query reads **another continuous aggregate's** view (e.g.
daily built on hourly). Migrations order create/drop by dependency automatically, and recreating a source
cascades to its dependents.

```csharp
public class DailyAverage
{
    public DateTimeOffset Bucket { get; set; }
    public double Average { get; set; }
}

modelBuilder.Entity<DailyAverage>(e =>
{
    e.HasNoKey();

    // This cagg reads FROM hourly_averages (another cagg), not the raw table.
    // The engine creates hourly_averages first and drops it last, automatically.
    e.IsContinuousAggregate("daily_averages", """
        SELECT time_bucket(INTERVAL '1 day', bucket) AS bucket,
               avg(average) AS average
        FROM hourly_averages
        GROUP BY 1
        """);
});
```

## Background jobs

**What it is:** a **user-defined scheduled job** (`add_job`) that runs a stored procedure on a cadence —
custom maintenance alongside the built-in policies. Created, altered and dropped through migrations,
keyed by name. Defined at the **model** level (not on an entity).

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    // ... entity configuration ...

    // The procedure must already exist in the database, e.g.:
    //   CREATE PROCEDURE public.cleanup(job_id int, config jsonb) LANGUAGE plpgsql AS $$ ... $$;
    modelBuilder.HasTimescaleDbJob(
        name: "nightly_cleanup",              // unique name (also the job's application_name)
        procedure: "public.cleanup",
        scheduleInterval: TimeSpan.FromDays(1),
        config: """{"drop_after":"30 days"}""",  // JSONB passed to the procedure
        maxRuntime: TimeSpan.FromMinutes(5),     // stop the job if it runs longer
        maxRetries: 3,                            // retries on failure
        retryPeriod: TimeSpan.FromMinutes(10));   // wait between retries
}
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
modelBuilder.Entity<Reading>(e =>
{
    e.HasNoKey();
    e.ToTable("readings");
    e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));

    // Opt out of specific automatic data operations for THIS entity:
    e.WithMigrateData(false);      // plain→hypertable: don't migrate existing rows (table must be empty)
    e.WithRebuildData(false);      // forbid data-copying rebuilds → throw at migration generation instead
    e.WithAutoDecompress(false);   // disabling the columnstore: don't decompress chunks first
});
```

Attribute form:

```csharp
[MigrationOptions(MigrateData = false, RebuildData = false, AutoDecompress = false)]
public class Reading { /* ... */ }
```

Model-wide default for every entity (a per-entity `With*` call still overrides it):

```csharp
options.UseNpgsql(connectionString)
       .UseTimescaleDb(o => o.MigrateData(false).RebuildData(false).AutoDecompress(false));
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
// 15-minute buckets per device, with the first/last value in each bucket.
var buckets = await db.Readings
    // Group rows into 15-minute time buckets (translated to time_bucket(...)).
    .GroupBy(r => new
    {
        Bucket = EF.Functions.TimeBucket(TimeSpan.FromMinutes(15), r.Time),
        r.DeviceId,
    })
    .Select(g => new
    {
        g.Key.Bucket,
        g.Key.DeviceId,
        Avg = g.Average(x => x.Value),
        // first()/last() take the value ordered by a companion time column:
        First = EF.Functions.First(g.Select(x => ValueTuple.Create(x.Value, x.Time))),
        Last = EF.Functions.Last(g.Select(x => ValueTuple.Create(x.Value, x.Time))),
    })
    .ToListAsync();
```
