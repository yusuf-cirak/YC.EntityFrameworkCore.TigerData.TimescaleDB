using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Migrations.Internal;

/// <summary>A policy's parameters, read from the table annotations.</summary>
public sealed record PolicyState(string? Main, string? Schedule, string? InitialStart, string? Timezone)
{
    public bool Exists => Main is not null;
}

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
    string? SpaceColumn,
    int? SpacePartitions,
    string? IntegerNowFunction,
    string[] ChunkSkipping,
    bool Columnstore,
    string? SegmentBy,
    string? OrderBy,
    string? MergeInterval,
    PolicyState Retention,
    PolicyState ColumnstorePolicy,
    string? ReorderIndex)
{
    public static TimescaleDbTableState Read(IReadOnlyAnnotatable source)
    {
        string? S(string name) => source.FindAnnotation(name)?.Value as string;

        return new TimescaleDbTableState(
            IsHypertable: source.FindAnnotation(TimescaleDbAnnotationNames.IsHypertable)?.Value is true,
            PartitionColumn: S(TimescaleDbAnnotationNames.PartitionColumn),
            ChunkInterval: S(TimescaleDbAnnotationNames.ChunkInterval),
            CreateDefaultIndexes:
                source.FindAnnotation(TimescaleDbAnnotationNames.CreateDefaultIndexes)?.Value as bool? ?? true,
            SpaceColumn: S(TimescaleDbAnnotationNames.SpacePartitionColumn),
            SpacePartitions: source.FindAnnotation(TimescaleDbAnnotationNames.SpacePartitions)?.Value as int?,
            IntegerNowFunction: S(TimescaleDbAnnotationNames.IntegerNowFunction),
            ChunkSkipping: S(TimescaleDbAnnotationNames.ChunkSkippingColumns)
                    ?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                ?? [],
            Columnstore: source.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreEnabled)?.Value is true,
            SegmentBy: S(TimescaleDbAnnotationNames.ColumnstoreSegmentBy),
            OrderBy: S(TimescaleDbAnnotationNames.ColumnstoreOrderBy),
            MergeInterval: S(TimescaleDbAnnotationNames.ColumnstoreChunkMergeInterval),
            Retention: new PolicyState(
                S(TimescaleDbAnnotationNames.RetentionPolicyDropAfter),
                S(TimescaleDbAnnotationNames.RetentionPolicyScheduleInterval),
                S(TimescaleDbAnnotationNames.RetentionPolicyInitialStart),
                S(TimescaleDbAnnotationNames.RetentionPolicyTimezone)),
            ColumnstorePolicy: new PolicyState(
                S(TimescaleDbAnnotationNames.ColumnstorePolicyAfter),
                S(TimescaleDbAnnotationNames.ColumnstorePolicyScheduleInterval),
                S(TimescaleDbAnnotationNames.ColumnstorePolicyInitialStart),
                S(TimescaleDbAnnotationNames.ColumnstorePolicyTimezone)),
            ReorderIndex: S(TimescaleDbAnnotationNames.ReorderPolicyIndex));
    }

    /// <summary>Columnstore layout changed (segmentby / orderby / merge interval) while staying enabled.</summary>
    public bool ColumnstoreLayoutDiffers(TimescaleDbTableState other)
        => SegmentBy != other.SegmentBy || OrderBy != other.OrderBy || MergeInterval != other.MergeInterval;
}
