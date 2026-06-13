# YC.EntityFrameworkCore.TigerData.TimescaleDB

First-class [TimescaleDB](https://www.tigerdata.com/) support for Entity Framework Core, layered on top of the
[Npgsql EF Core provider](https://www.npgsql.org/efcore/).

Configure hypertables, columnstore compression, continuous aggregates, retention/reorder policies and background
jobs with the Fluent API **or** attributes — and have everything flow through standard EF Core migrations.
Query with TimescaleDB hyperfunctions (`time_bucket`, `first`/`last`, gapfilling, histograms, UUIDv7) directly
from LINQ. Reverse-engineer existing TimescaleDB schemas with `dotnet ef dbcontext scaffold`.

> Requires EF Core 10, `Npgsql.EntityFrameworkCore.PostgreSQL` 10 and TimescaleDB 2.23+ (the modern
> declarative `CREATE TABLE ... WITH (timescaledb.hypertable)` interface is used; no legacy fallback).

## Package

One package — design-time support (migration code generation, scaffolding for `dotnet ef`) is included:

```
dotnet add package YC.EntityFrameworkCore.TigerData.TimescaleDB
```

## Quick start

```csharp
services.AddDbContext<MetricsContext>(options => options
    .UseNpgsql(connectionString)
    .UseTimescaleDb());
```

### Fluent API — type-safe column references and intervals

Columns are selected with expressions and emitted with whatever store name EF produced
(`HasColumnName`, naming-convention plugins, …); intervals are `TimeSpan`s, with raw-string
overloads for calendar units (`"1 month"`) a `TimeSpan` cannot express.

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<Reading>(e =>
    {
        e.HasNoKey();                       // or include the partition column in the key
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

### Attributes (equivalent) — property-attached, no name strings

```csharp
[Hypertable(ChunkIntervalDays = 1)]
[Columnstore]
[ColumnstorePolicy(AfterDays = 7)]
[RetentionPolicy(DropAfterDays = 90)]
public class Reading
{
    [HypertablePartition]
    [ColumnstoreOrderBy(Descending = true)]
    public DateTimeOffset Time { get; set; }

    [SpacePartition(4)]
    [ColumnstoreSegmentBy]
    public string DeviceId { get; set; } = null!;

    public double Value { get; set; }
}
```

### Continuous aggregates

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
    e.HasRefreshPolicy(startOffset: "3 days", endOffset: "1 hour", scheduleInterval: "1 hour");
});
```

The entity maps to the materialized view for querying and the view is created, recreated and dropped
through migrations.

### Background jobs

```csharp
modelBuilder.HasTimescaleDbJob(
    "nightly_cleanup",
    "public.cleanup",          // the procedure must already exist in the database
    scheduleInterval: "1 day",
    config: """{"drop_after":"30 days"}""");
```

### Hyperfunctions in LINQ

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

Available: `TimeBucket`, `TimeBucketGapfill`, `Locf`, `Interpolate`, `First`, `Last`, `Histogram`,
`GenerateUuid7`, `ToUuid7`, `UuidTimestamp`.

### Migrations — snapshot-driven, no special commands

`dotnet ef migrations add` records the TimescaleDB configuration purely as **annotations** on the table,
plus `migrationBuilder.Sql(...)` for the objects that aren't tables (the extension, continuous aggregates,
jobs). There are **no imperative commands** like `ConvertToHypertable()` or `DisableColumnstore()`:

```csharp
migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS timescaledb;");

migrationBuilder.CreateTable(/* ... */)
    .Annotation("TimescaleDb:IsHypertable", true)
    .Annotation("TimescaleDb:Hypertable:PartitionColumn", "time")
    .Annotation("TimescaleDb:Hypertable:ChunkInterval", "1 day")
    .Annotation("TimescaleDb:Columnstore:Enabled", true)
    .Annotation("TimescaleDb:RetentionPolicy:DropAfter", "90 days");

migrationBuilder.Sql("CREATE MATERIALIZED VIEW \"hourly_averages\" WITH (timescaledb.continuous) AS …;");
```

The provider's SQL generator turns those annotation deltas into the real DDL when the migration runs
(`SELECT create_hypertable(...)`, `ALTER TABLE … SET (...)`, `add_retention_policy`, …). Because the
annotations carry **both old and new** values, every change is **bidirectional automatically** — the `Down`
migration emits the exact reverse with no extra code, and re-running `migrations add` on an unchanged model
produces an **empty** migration.

## Transition matrix

Every table-state transition is derived from the snapshot↔model annotation delta. Changes TimescaleDB cannot
apply in place **rebuild the table** (create a target-shaped table, copy every row, drop the old, rename) so
the **data is preserved** — nothing is rejected:

| Change in the model | Behavior |
|---|---|
| New table as hypertable | `CREATE TABLE …` then `SELECT create_hypertable(...)` |
| **Existing table → hypertable** | `create_hypertable(..., migrate_data => true)` — migrates existing rows into chunks |
| **Hypertable → plain table** | **rebuild** → plain table, data copied back |
| **Partition column changed** | **rebuild** → hypertable with the new partition column |
| Chunk interval changed | `set_chunk_time_interval(...)` (applies to future chunks) |
| Space partition added | `add_dimension(by_hash(...))` |
| **Space partition removed / changed** | **rebuild** |
| Columnstore enabled | `ALTER TABLE … SET (timescaledb.enable_columnstore, …)` |
| **Columnstore disabled** | every compressed chunk decompressed, then disabled |
| segmentby / orderby changed | `ALTER TABLE … SET (...)` (future chunks) |
| Retention / columnstore / reorder policy ± | `add_*_policy` / `remove_*_policy` |
| Chunk skipping ± | `enable_chunk_skipping` / `disable_chunk_skipping` |
| Cagg query changed | drop + recreate (+ policies re-added) |

## More features

- **Integer-time hypertables**: `IsHypertable(x => x.UnixMicros, chunkInterval: 86_400_000_000,
  integerNowFunction: "micros_now")` — the integer-now function is required (and validated) for policies.
- **Chunk skipping**: `HasChunkSkipping(x => x.Value)` / `[ChunkSkipping]`.
- **Policy scheduling**: `initialStart` / `timezone` on retention, columnstore and refresh policies.
- **Columnstore chunk merging**: `cs.MergeChunksUpTo(TimeSpan.FromDays(7))`.
- **Columnstore on continuous aggregates**: `HasColumnstore(...)` on a cagg entity emits
  `ALTER MATERIALIZED VIEW ... SET (timescaledb.enable_columnstore, ...)`.
- **Reorder policy by EF index**: `HasReorderPolicy(x => new { x.DeviceId, x.Time })` resolves the
  index's database name.

## Notes and limitations

- **Primary keys** on hypertables must include the partition column (TimescaleDB rule, validated at model
  build time); keyless entities are the natural fit for raw time-series tables.
- **Rebuilds are data-heavy and not free of caveats.** Un-converting a hypertable, repartitioning, or
  changing a space dimension copies every row under lock via `CREATE TABLE (LIKE … INCLUDING ALL)`. PostgreSQL
  `LIKE` does **not** copy FOREIGN KEY constraints (inbound or outbound) and recreates indexes under generated
  names; the migration includes a warning comment. Re-create FKs manually if the table participates in any.
- **Chunk interval changes apply to future chunks only** (`set_chunk_time_interval`); existing chunks keep
  their interval. Re-chunking historical data is intentionally not performed.
- Converting an existing table to a hypertable and disabling the columnstore are **data-heavy**
  (row migration / chunk decompression); review the generated migration before running it in production.
- **Continuous aggregates** are not reverse-engineered by scaffolding (materialized views are outside the
  Npgsql scaffolding surface); hypertables, dimensions, columnstore settings, policies and custom jobs are.
- Enabling the columnstore auto-creates a default conversion policy; `HasColumnstorePolicy` replaces it
  deterministically.

## License

MIT
