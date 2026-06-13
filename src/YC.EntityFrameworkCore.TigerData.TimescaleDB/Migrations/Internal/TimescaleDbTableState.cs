using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Migrations.Internal;

/// <summary>A policy's parameters, read from the table annotations.</summary>
public sealed record PolicyState(string? Main, string? Schedule, string? InitialStart, string? Timezone)
{
    public bool Exists => Main is not null;
}

/// <summary>A hash (space) partition dimension.</summary>
public sealed record SpaceDimension(string Column, int Partitions);

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
    IReadOnlyList<string> Tablespaces,
    string? IntegerNowFunction,
    string[] ChunkSkipping,
    bool Columnstore,
    string? SegmentBy,
    string? OrderBy,
    string? MergeInterval,
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
            ReorderIndex: S(TimescaleDbAnnotationNames.ReorderPolicyIndex),
            MigrateData: B(TimescaleDbAnnotationNames.MigrateData, migrateDataDefault),
            RebuildData: B(TimescaleDbAnnotationNames.RebuildData, rebuildDataDefault),
            AutoDecompress: B(TimescaleDbAnnotationNames.AutoDecompress, autoDecompressDefault));
    }

    /// <summary>Columnstore layout changed (segmentby / orderby / merge interval) while staying enabled.</summary>
    public bool ColumnstoreLayoutDiffers(TimescaleDbTableState other)
        => SegmentBy != other.SegmentBy || OrderBy != other.OrderBy || MergeInterval != other.MergeInterval;

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
}
