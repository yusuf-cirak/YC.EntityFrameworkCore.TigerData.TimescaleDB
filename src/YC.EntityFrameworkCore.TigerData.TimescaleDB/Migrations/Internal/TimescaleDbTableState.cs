using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Migrations.Internal;

/// <summary>
///     A policy's parameters, read from the table annotations. <paramref name="CreatedBefore" /> discriminates
///     the creation-time form (<c>drop_created_before</c> / <c>created_before</c>) from the value form
///     (<c>drop_after</c> / <c>after</c>); in both cases <paramref name="Main" /> carries the interval value.
/// </summary>
public sealed record PolicyState(
    string? Main, string? Schedule, string? InitialStart, string? Timezone, bool CreatedBefore = false)
{
    public bool Exists => Main is not null;
}

/// <summary>A hash (space) partition dimension.</summary>
public sealed record SpaceDimension(string Column, int Partitions);

/// <summary>
///     A range (secondary) partition dimension. <paramref name="Interval" /> is a PostgreSQL interval
///     literal (e.g. <c>7 days</c>) for time columns, or a plain bigint for integer-time hypertables.
/// </summary>
public sealed record RangeDimension(string Column, string Interval);

/// <summary>
///     The TimescaleDB state of a table, read from the annotations EF attaches to a
///     <c>CreateTableOperation</c> / <c>AlterTableOperation</c> (and its <c>OldTable</c>).
///     The generator diffs an old state against a new state to produce forward/reverse SQL.
/// </summary>
public sealed record TimescaleDbTableState(
    bool IsHypertable,
    string? PartitionColumn,
    string? ChunkInterval,
    bool CreateDefaultIndexes,
    IReadOnlyList<SpaceDimension> SpaceDimensions,
    IReadOnlyList<RangeDimension> RangeDimensions,
    IReadOnlyList<string> Tablespaces,
    string? IntegerNowFunction,
    string[] ChunkSkipping,
    bool Columnstore,
    string? SegmentBy,
    string? OrderBy,
    string? MergeInterval,
    string? SparseIndex,
    PolicyState Retention,
    PolicyState ColumnstorePolicy,
    string? ReorderIndex,
    bool MigrateData,
    bool RebuildData,
    bool AutoDecompress)
{
    /// <summary>
    ///     Reads the table state. The migration-behavior toggles fall back to the supplied model-wide
    ///     defaults (from <c>UseTimescaleDb(...)</c>) when the entity does not set them explicitly.
    /// </summary>
    public static TimescaleDbTableState Read(
        IReadOnlyAnnotatable source,
        bool migrateDataDefault = true,
        bool rebuildDataDefault = true,
        bool autoDecompressDefault = true)
    {
        string? S(string name) => source.FindAnnotation(name)?.Value as string;
        bool B(string name, bool fallback) => source.FindAnnotation(name)?.Value as bool? ?? fallback;

        return new TimescaleDbTableState(
            IsHypertable: source.FindAnnotation(TimescaleDbAnnotationNames.IsHypertable)?.Value is true,
            PartitionColumn: S(TimescaleDbAnnotationNames.PartitionColumn),
            ChunkInterval: S(TimescaleDbAnnotationNames.ChunkInterval),
            CreateDefaultIndexes:
                source.FindAnnotation(TimescaleDbAnnotationNames.CreateDefaultIndexes)?.Value as bool? ?? true,
            SpaceDimensions: ParseDimensions(S(TimescaleDbAnnotationNames.SpaceDimensions)),
            RangeDimensions: ParseRangeDimensions(S(TimescaleDbAnnotationNames.RangeDimensions)),
            Tablespaces: S(TimescaleDbAnnotationNames.Tablespaces)
                    ?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                ?? [],
            IntegerNowFunction: S(TimescaleDbAnnotationNames.IntegerNowFunction),
            ChunkSkipping: S(TimescaleDbAnnotationNames.ChunkSkippingColumns)
                    ?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                ?? [],
            Columnstore: source.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreEnabled)?.Value is true,
            SegmentBy: S(TimescaleDbAnnotationNames.ColumnstoreSegmentBy),
            OrderBy: S(TimescaleDbAnnotationNames.ColumnstoreOrderBy),
            MergeInterval: S(TimescaleDbAnnotationNames.ColumnstoreChunkMergeInterval),
            SparseIndex: S(TimescaleDbAnnotationNames.ColumnstoreSparseIndex),
            Retention: ReadPolicy(
                S(TimescaleDbAnnotationNames.RetentionPolicyDropAfter),
                S(TimescaleDbAnnotationNames.RetentionPolicyDropCreatedBefore),
                S(TimescaleDbAnnotationNames.RetentionPolicyScheduleInterval),
                S(TimescaleDbAnnotationNames.RetentionPolicyInitialStart),
                S(TimescaleDbAnnotationNames.RetentionPolicyTimezone)),
            ColumnstorePolicy: ReadPolicy(
                S(TimescaleDbAnnotationNames.ColumnstorePolicyAfter),
                S(TimescaleDbAnnotationNames.ColumnstorePolicyCreatedBefore),
                S(TimescaleDbAnnotationNames.ColumnstorePolicyScheduleInterval),
                S(TimescaleDbAnnotationNames.ColumnstorePolicyInitialStart),
                S(TimescaleDbAnnotationNames.ColumnstorePolicyTimezone)),
            ReorderIndex: S(TimescaleDbAnnotationNames.ReorderPolicyIndex),
            MigrateData: B(TimescaleDbAnnotationNames.MigrateData, migrateDataDefault),
            RebuildData: B(TimescaleDbAnnotationNames.RebuildData, rebuildDataDefault),
            AutoDecompress: B(TimescaleDbAnnotationNames.AutoDecompress, autoDecompressDefault));
    }

    /// <summary>Columnstore layout changed (segmentby / orderby / merge interval) while staying enabled.</summary>
    public bool ColumnstoreLayoutDiffers(TimescaleDbTableState other)
        => SegmentBy != other.SegmentBy || OrderBy != other.OrderBy || MergeInterval != other.MergeInterval
            || SparseIndex != other.SparseIndex;

    /// <summary>
    ///     Builds a <see cref="PolicyState" /> from the value-form and creation-time annotations. When the
    ///     creation-time annotation is set it wins (the two are mutually exclusive, enforced at model build).
    /// </summary>
    private static PolicyState ReadPolicy(
        string? afterValue, string? createdBefore, string? schedule, string? initialStart, string? timezone)
        => createdBefore is not null
            ? new PolicyState(createdBefore, schedule, initialStart, timezone, CreatedBefore: true)
            : new PolicyState(afterValue, schedule, initialStart, timezone);

    private static IReadOnlyList<SpaceDimension> ParseDimensions(string? value)
    {
        if (value is null)
        {
            return [];
        }

        var result = new List<SpaceDimension>();
        foreach (var item in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split(':', 2);
            result.Add(new SpaceDimension(
                parts[0].Trim(),
                int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
        }

        return result;
    }

    private static IReadOnlyList<RangeDimension> ParseRangeDimensions(string? value)
    {
        if (value is null)
        {
            return [];
        }

        var result = new List<RangeDimension>();
        foreach (var item in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // Split on the first ':' only — an interval literal may itself contain ':' (e.g. "01:30:00").
            var parts = item.Split(':', 2);
            result.Add(new RangeDimension(parts[0].Trim(), parts[1].Trim()));
        }

        return result;
    }
}
