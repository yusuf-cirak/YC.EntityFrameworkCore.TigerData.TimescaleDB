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
    public const string SpacePartitionColumn = Prefix + "Hypertable:SpacePartitionColumn";
    public const string SpacePartitions = Prefix + "Hypertable:SpacePartitions";
    public const string IntegerNowFunction = Prefix + "Hypertable:IntegerNowFunction";
    public const string ChunkSkippingColumns = Prefix + "Hypertable:ChunkSkippingColumns";

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
        SpacePartitionColumn,
        SpacePartitions,
        IntegerNowFunction,
        ChunkSkippingColumns,
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
        ReorderPolicyIndex,
    ];
}
