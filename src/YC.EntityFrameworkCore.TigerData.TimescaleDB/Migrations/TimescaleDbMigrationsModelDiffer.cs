using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Infrastructure.Internal;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Metadata;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Migrations.Internal;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Migrations;

/// <summary>
///     Adds the non-table TimescaleDB objects to the migration diff — the extension, continuous
///     aggregates and user jobs — as <c>SqlOperation</c>s (rendered as
///     <c>migrationBuilder.Sql(...)</c>). Table-attached features (hypertables, columnstore,
///     dimensions, policies, …) are handled entirely by the base differ via the table annotations
///     and the SQL generator. Because the diff is symmetric (source vs target), Down migrations
///     are produced automatically.
/// </summary>
public class TimescaleDbMigrationsModelDiffer : MigrationsModelDiffer
{
    private readonly bool _createExtension;

    public TimescaleDbMigrationsModelDiffer(
        IRelationalTypeMappingSource typeMappingSource,
        IMigrationsAnnotationProvider migrationsAnnotationProvider,
        IRelationalAnnotationProvider relationalAnnotationProvider,
        IRowIdentityMapFactory rowIdentityMapFactory,
        CommandBatchPreparerDependencies commandBatchPreparerDependencies,
        IDbContextOptions contextOptions)
        : base(
            typeMappingSource,
            migrationsAnnotationProvider,
            relationalAnnotationProvider,
            rowIdentityMapFactory,
            commandBatchPreparerDependencies)
        => _createExtension =
            contextOptions.FindExtension<TimescaleDbOptionsExtension>()?.CreateExtension ?? true;

    public override IReadOnlyList<MigrationOperation> GetDifferences(
        IRelationalModel? source,
        IRelationalModel? target)
    {
        var baseOperations = base.GetDifferences(source, target);

        var sourceModel = source?.Model;
        var targetModel = target?.Model;

        // Before the base operations: cagg drops precede drops of their source hypertables.
        var pre = new List<MigrationOperation>();

        // After the base operations: caggs and jobs need their source tables to already exist.
        var post = new List<MigrationOperation>();

        DiffContinuousAggregates(sourceModel, targetModel, pre, post);
        DiffJobs(sourceModel, targetModel, post);

        if (_createExtension && UsesTimescaleDb(targetModel) && !UsesTimescaleDb(sourceModel))
        {
            pre.Insert(0, Sql(TimescaleDbSql.CreateExtension()));
        }

        return pre.Count == 0 && post.Count == 0
            ? baseOperations
            : [.. pre, .. baseOperations, .. post];
    }

    // ---------------------------------------------------------------- continuous aggregates

    private static void DiffContinuousAggregates(
        IModel? sourceModel,
        IModel? targetModel,
        List<MigrationOperation> pre,
        List<MigrationOperation> post)
    {
        var sourceAggregates = IndexByView(sourceModel);
        var targetAggregates = IndexByView(targetModel);

        foreach (var ((schema, view), _) in sourceAggregates)
        {
            if (!targetAggregates.ContainsKey((schema, view)))
            {
                pre.Add(Sql(TimescaleDbSql.DropContinuousAggregate(view, schema)));
            }
        }

        foreach (var ((schema, view), entity) in targetAggregates)
        {
            sourceAggregates.TryGetValue((schema, view), out var sourceEntity);

            var query = (string)entity.FindAnnotation(TimescaleDbAnnotationNames.ContinuousAggregateQuery)!.Value!;
            var materializedOnly = Bool(entity, TimescaleDbAnnotationNames.ContinuousAggregateMaterializedOnly, true);
            var withNoData = Bool(entity, TimescaleDbAnnotationNames.ContinuousAggregateWithNoData, true);
            var chunkInterval = A(entity, TimescaleDbAnnotationNames.ContinuousAggregateChunkInterval);

            var sourceQuery = A(sourceEntity, TimescaleDbAnnotationNames.ContinuousAggregateQuery);
            var sourceMaterializedOnly = Bool(sourceEntity, TimescaleDbAnnotationNames.ContinuousAggregateMaterializedOnly, true);
            var sourceChunkInterval = A(sourceEntity, TimescaleDbAnnotationNames.ContinuousAggregateChunkInterval);

            var recreated = sourceEntity is not null
                && (sourceQuery != query || sourceChunkInterval != chunkInterval);

            if (sourceEntity is null || recreated)
            {
                if (recreated)
                {
                    pre.Add(Sql(TimescaleDbSql.DropContinuousAggregate(view, schema)));
                }

                post.Add(Sql(TimescaleDbSql.CreateContinuousAggregate(
                    view, schema, query, materializedOnly, withNoData, chunkInterval)));
            }
            else if (sourceMaterializedOnly != materializedOnly)
            {
                post.Add(Sql(TimescaleDbSql.AlterContinuousAggregateMaterializedOnly(view, schema, materializedOnly)));
            }

            var effectiveSource = recreated ? null : sourceEntity;

            DiffCaggColumnstore(effectiveSource, entity, view, schema, post);
            DiffRefreshPolicy(effectiveSource, entity, view, schema, post);
            DiffCaggPolicies(effectiveSource, entity, view, schema, post);
        }
    }

    private static void DiffCaggColumnstore(
        IEntityType? source,
        IEntityType target,
        string view,
        string? schema,
        List<MigrationOperation> post)
    {
        var sourceEnabled = source?.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreEnabled)?.Value is true;
        var targetEnabled = target.FindAnnotation(TimescaleDbAnnotationNames.ColumnstoreEnabled)?.Value is true;
        var sourceSegment = A(source, TimescaleDbAnnotationNames.ColumnstoreSegmentBy);
        var sourceOrder = A(source, TimescaleDbAnnotationNames.ColumnstoreOrderBy);
        var targetSegment = A(target, TimescaleDbAnnotationNames.ColumnstoreSegmentBy);
        var targetOrder = A(target, TimescaleDbAnnotationNames.ColumnstoreOrderBy);

        if (sourceEnabled == targetEnabled && sourceSegment == targetSegment && sourceOrder == targetOrder)
        {
            return;
        }

        if (!sourceEnabled && !targetEnabled)
        {
            return;
        }

        post.Add(Sql(TimescaleDbSql.AlterContinuousAggregateColumnstore(
            view, schema, targetEnabled, targetSegment, targetOrder)));
    }

    private static void DiffRefreshPolicy(
        IEntityType? source,
        IEntityType target,
        string view,
        string? schema,
        List<MigrationOperation> post)
    {
        string?[] Read(IEntityType? e) =>
        [
            A(e, TimescaleDbAnnotationNames.RefreshPolicyStartOffset),
            A(e, TimescaleDbAnnotationNames.RefreshPolicyEndOffset),
            A(e, TimescaleDbAnnotationNames.RefreshPolicyScheduleInterval),
            A(e, TimescaleDbAnnotationNames.RefreshPolicyInitialStart),
            A(e, TimescaleDbAnnotationNames.RefreshPolicyTimezone),
        ];

        var s = Read(source);
        var t = Read(target);
        if (s.SequenceEqual(t))
        {
            return;
        }

        if (s[0] is not null)
        {
            post.Add(Sql(TimescaleDbSql.RemoveRefreshPolicy(view, schema)));
        }

        if (t[0] is not null)
        {
            post.Add(Sql(TimescaleDbSql.AddRefreshPolicy(view, schema, t[0]!, t[1]!, t[2], t[3], t[4])));
        }
    }

    private static void DiffCaggPolicies(
        IEntityType? source,
        IEntityType target,
        string view,
        string? schema,
        List<MigrationOperation> post)
    {
        DiffPolicy(
            source, target,
            [
                TimescaleDbAnnotationNames.RetentionPolicyDropAfter,
                TimescaleDbAnnotationNames.RetentionPolicyScheduleInterval,
                TimescaleDbAnnotationNames.RetentionPolicyInitialStart,
                TimescaleDbAnnotationNames.RetentionPolicyTimezone,
            ],
            add: v => TimescaleDbSql.AddRetentionPolicy(view, schema, v[0]!, v[1], v[2], v[3]),
            remove: () => TimescaleDbSql.RemoveRetentionPolicy(view, schema),
            post);

        DiffPolicy(
            source, target,
            [
                TimescaleDbAnnotationNames.ColumnstorePolicyAfter,
                TimescaleDbAnnotationNames.ColumnstorePolicyScheduleInterval,
                TimescaleDbAnnotationNames.ColumnstorePolicyInitialStart,
                TimescaleDbAnnotationNames.ColumnstorePolicyTimezone,
            ],
            add: v => TimescaleDbSql.AddColumnstorePolicy(view, schema, v[0]!, v[1], v[2], v[3]),
            remove: () => TimescaleDbSql.RemoveColumnstorePolicy(view, schema),
            post);
    }

    private static void DiffPolicy(
        IEntityType? source,
        IEntityType target,
        string[] annotations,
        Func<string?[], string> add,
        Func<string> remove,
        List<MigrationOperation> post)
    {
        var s = annotations.Select(a => A(source, a)).ToArray();
        var t = annotations.Select(a => A(target, a)).ToArray();
        if (s.SequenceEqual(t))
        {
            return;
        }

        if (s[0] is not null)
        {
            post.Add(Sql(remove()));
        }

        if (t[0] is not null)
        {
            post.Add(Sql(add(t)));
        }
    }

    // ---------------------------------------------------------------- jobs

    private static void DiffJobs(IModel? sourceModel, IModel? targetModel, List<MigrationOperation> post)
    {
        var sourceJobs = TimescaleDbJob
            .Deserialize(sourceModel?.FindAnnotation(TimescaleDbAnnotationNames.Jobs)?.Value as string)
            .ToDictionary(j => j.Name, StringComparer.Ordinal);
        var targetJobs = TimescaleDbJob
            .Deserialize(targetModel?.FindAnnotation(TimescaleDbAnnotationNames.Jobs)?.Value as string)
            .ToDictionary(j => j.Name, StringComparer.Ordinal);

        foreach (var (name, job) in sourceJobs)
        {
            if (!targetJobs.TryGetValue(name, out var unchanged) || unchanged != job)
            {
                post.Add(Sql(TimescaleDbSql.DeleteJob(name)));
            }
        }

        foreach (var (name, job) in targetJobs)
        {
            if (!sourceJobs.TryGetValue(name, out var existing) || existing != job)
            {
                post.Add(Sql(TimescaleDbSql.AddJob(
                    job.Name, job.Procedure, job.ScheduleInterval, job.Config,
                    job.FixedSchedule, job.InitialStart, job.Timezone)));
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static SqlOperation Sql(string sql, bool suppressTransaction = false)
        => new() { Sql = sql, SuppressTransaction = suppressTransaction };

    private static string? A(IEntityType? entity, string name)
        => entity?.FindAnnotation(name)?.Value as string;

    private static bool Bool(IEntityType? entity, string name, bool fallback)
        => entity?.FindAnnotation(name)?.Value as bool? ?? fallback;

    private static Dictionary<(string? Schema, string Name), IEntityType> IndexByView(IModel? model)
    {
        var index = new Dictionary<(string?, string), IEntityType>();
        foreach (var entity in model?.GetEntityTypes() ?? [])
        {
            if (entity.FindAnnotation(TimescaleDbAnnotationNames.IsContinuousAggregate)?.Value is true
                && entity.GetViewName() is { } view)
            {
                index[(entity.GetViewSchema(), view)] = entity;
            }
        }

        return index;
    }

    private static bool UsesTimescaleDb(IModel? model)
        => model is not null
            && (model.FindAnnotation(TimescaleDbAnnotationNames.Jobs) is not null
                || model.GetEntityTypes().Any(e =>
                    e.FindAnnotation(TimescaleDbAnnotationNames.IsHypertable)?.Value is true
                    || e.FindAnnotation(TimescaleDbAnnotationNames.IsContinuousAggregate)?.Value is true));
}
