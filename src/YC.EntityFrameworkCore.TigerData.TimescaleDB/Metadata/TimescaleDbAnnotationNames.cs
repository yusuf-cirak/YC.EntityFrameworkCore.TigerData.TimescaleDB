namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;

/// <summary>
///     Names of the semantic TimescaleDB annotations stored on the EF model.
///     Column-name values are canonicalized (property names are resolved to store
///     column names) by the model finalizing convention.
/// </summary>
public static class TimescaleDbAnnotationNames
{
    public const string Prefix = "TimescaleDb:";

    // Hypertable (entity type)
    public const string IsHypertable = Prefix + "IsHypertable";
    public const string PartitionColumn = Prefix + "Hypertable:PartitionColumn";
    public const string ChunkInterval = Prefix + "Hypertable:ChunkInterval";
    public const string CreateDefaultIndexes = Prefix + "Hypertable:CreateDefaultIndexes";

    /// <summary>Ordered <c>column:partitions</c> list of hash (space) dimensions, e.g. <c>"device_id:4, region:8"</c>.</summary>
    public const string SpaceDimensions = Prefix + "Hypertable:SpaceDimensions";
    public const string IntegerNowFunction = Prefix + "Hypertable:IntegerNowFunction";
    public const string ChunkSkippingColumns = Prefix + "Hypertable:ChunkSkippingColumns";

    /// <summary>Comma-joined tablespace names attached to the hypertable.</summary>
    public const string Tablespaces = Prefix + "Hypertable:Tablespaces";

    // Columnstore / compression (entity type)
    public const string ColumnstoreEnabled = Prefix + "Columnstore:Enabled";
    public const string ColumnstoreSegmentBy = Prefix + "Columnstore:SegmentBy";
    public const string ColumnstoreOrderBy = Prefix + "Columnstore:OrderBy";
    public const string ColumnstoreChunkMergeInterval = Prefix + "Columnstore:ChunkMergeInterval";

    // Policies (entity type)
    public const string ColumnstorePolicyAfter = Prefix + "ColumnstorePolicy:After";
    public const string ColumnstorePolicyScheduleInterval = Prefix + "ColumnstorePolicy:ScheduleInterval";
    public const string ColumnstorePolicyInitialStart = Prefix + "ColumnstorePolicy:InitialStart";
    public const string ColumnstorePolicyTimezone = Prefix + "ColumnstorePolicy:Timezone";
    public const string RetentionPolicyDropAfter = Prefix + "RetentionPolicy:DropAfter";
    public const string RetentionPolicyScheduleInterval = Prefix + "RetentionPolicy:ScheduleInterval";
    public const string RetentionPolicyInitialStart = Prefix + "RetentionPolicy:InitialStart";
    public const string RetentionPolicyTimezone = Prefix + "RetentionPolicy:Timezone";
    public const string ReorderPolicyIndex = Prefix + "ReorderPolicy:Index";
    public const string ReorderPolicyIndexProperties = Prefix + "ReorderPolicy:IndexProperties";

    // Continuous aggregate (entity type, mapped to a materialized view)
    public const string IsContinuousAggregate = Prefix + "IsContinuousAggregate";
    public const string ContinuousAggregateQuery = Prefix + "ContinuousAggregate:Query";
    public const string ContinuousAggregateMaterializedOnly = Prefix + "ContinuousAggregate:MaterializedOnly";
    public const string ContinuousAggregateWithNoData = Prefix + "ContinuousAggregate:WithNoData";
    public const string ContinuousAggregateChunkInterval = Prefix + "ContinuousAggregate:ChunkInterval";
    public const string RefreshPolicyStartOffset = Prefix + "RefreshPolicy:StartOffset";
    public const string RefreshPolicyEndOffset = Prefix + "RefreshPolicy:EndOffset";
    public const string RefreshPolicyScheduleInterval = Prefix + "RefreshPolicy:ScheduleInterval";
    public const string RefreshPolicyInitialStart = Prefix + "RefreshPolicy:InitialStart";
    public const string RefreshPolicyTimezone = Prefix + "RefreshPolicy:Timezone";

    // Jobs (model level; JSON-serialized array of job definitions)
    public const string Jobs = Prefix + "Jobs";

    // Migration behavior toggles (entity type; default true). Govern the automatic, data-heavy
    // operations the engine injects during transitions.
    public const string MigrateData = Prefix + "Migration:MigrateData";
    public const string RebuildData = Prefix + "Migration:RebuildData";
    public const string AutoDecompress = Prefix + "Migration:AutoDecompress";

    /// <summary>
    ///     The hypertable/table-attached annotations the relational annotation provider projects
    ///     onto the table, so EF emits them on <c>CreateTableOperation</c>/<c>AlterTableOperation</c>
    ///     and the SQL generator can interpret the snapshot↔model delta.
    /// </summary>
    public static readonly string[] TableAttached =
    [
        IsHypertable,
        PartitionColumn,
        ChunkInterval,
        CreateDefaultIndexes,
        SpaceDimensions,
        IntegerNowFunction,
        ChunkSkippingColumns,
        Tablespaces,
        ColumnstoreEnabled,
        ColumnstoreSegmentBy,
        ColumnstoreOrderBy,
        ColumnstoreChunkMergeInterval,
        ColumnstorePolicyAfter,
        ColumnstorePolicyScheduleInterval,
        ColumnstorePolicyInitialStart,
        ColumnstorePolicyTimezone,
        RetentionPolicyDropAfter,
        RetentionPolicyScheduleInterval,
        RetentionPolicyInitialStart,
        RetentionPolicyTimezone,
        // ReorderPolicyIndex is intentionally NOT projected onto the table: the reorder policy must be
        // added after EF's CreateIndexOperation (which runs after the table), so it is emitted as a
        // SqlOperation by the model differ instead. See TimescaleDbMigrationsModelDiffer.DiffReorderPolicy.
    ];

    /// <summary>
    ///     Migration behavior toggles, projected onto the table for **any** mapped entity (not only
    ///     hypertables) — the generator must see, e.g., <see cref="RebuildData" /> on a table that is
    ///     becoming a plain table (hypertable→plain). Default true when absent.
    /// </summary>
    public static readonly string[] MigrationToggles =
    [
        MigrateData,
        RebuildData,
        AutoDecompress,
    ];
}
