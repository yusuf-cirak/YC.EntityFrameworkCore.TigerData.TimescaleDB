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
        DiffReorderPolicy(sourceModel, targetModel, pre, post);

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
        var source = IndexByView(sourceModel);
        var target = IndexByView(targetModel);

        // Dependency graph over the union (target entity preferred): A → B when A's query reads B's view.
        // Built over the union so drops of source-only caggs are ordered too.
        var all = new Dictionary<(string? Schema, string Name), IEntityType>(source);
        foreach (var (key, entity) in target)
        {
            all[key] = entity;
        }

        var deps = BuildCaggDependencies(all);

        // A cagg is recreated if its query/chunk changed, OR (cascade) any cagg it depends on is recreated.
        var recreated = new HashSet<(string?, string)>();
        foreach (var (key, entity) in target)
        {
            if (source.TryGetValue(key, out var src)
                && (A(src, TimescaleDbAnnotationNames.ContinuousAggregateQuery) != Query(entity)
                    || A(src, TimescaleDbAnnotationNames.ContinuousAggregateChunkInterval)
                        != A(entity, TimescaleDbAnnotationNames.ContinuousAggregateChunkInterval)))
            {
                recreated.Add(key);
            }
        }

        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (key, dependsOn) in deps)
            {
                if (source.ContainsKey(key) && !recreated.Contains(key) && dependsOn.Any(recreated.Contains))
                {
                    recreated.Add(key);
                    changed = true;
                }
            }
        }

        var createKeys = target.Keys.Where(k => !source.ContainsKey(k) || recreated.Contains(k)).ToHashSet();
        var dropKeys = source.Keys.Where(k => !target.ContainsKey(k) || recreated.Contains(k)).ToList();

        // Drop dependents before their sources; create sources before their dependents.
        var dropOrdered = TopoSortCaggs(dropKeys, deps);
        dropOrdered.Reverse();
        foreach (var (schema, view) in dropOrdered)
        {
            pre.Add(Sql(TimescaleDbSql.DropContinuousAggregate(view, schema)));
        }

        foreach (var (schema, view) in TopoSortCaggs(createKeys.ToList(), deps))
        {
            var entity = target[(schema, view)];
            post.Add(Sql(TimescaleDbSql.CreateContinuousAggregate(
                view, schema, Query(entity),
                Bool(entity, TimescaleDbAnnotationNames.ContinuousAggregateMaterializedOnly, true),
                Bool(entity, TimescaleDbAnnotationNames.ContinuousAggregateWithNoData, true),
                A(entity, TimescaleDbAnnotationNames.ContinuousAggregateChunkInterval))));
        }

        // Per-cagg settings: run after every create so the relation exists.
        foreach (var (key, entity) in target)
        {
            var (schema, view) = key;
            var isCreate = createKeys.Contains(key);
            source.TryGetValue(key, out var sourceEntity);

            if (!isCreate
                && Bool(sourceEntity, TimescaleDbAnnotationNames.ContinuousAggregateMaterializedOnly, true)
                    != Bool(entity, TimescaleDbAnnotationNames.ContinuousAggregateMaterializedOnly, true))
            {
                post.Add(Sql(TimescaleDbSql.AlterContinuousAggregateMaterializedOnly(
                    view, schema, Bool(entity, TimescaleDbAnnotationNames.ContinuousAggregateMaterializedOnly, true))));
            }

            var effectiveSource = isCreate ? null : sourceEntity;
            DiffCaggColumnstore(effectiveSource, entity, view, schema, post);
            DiffRefreshPolicy(effectiveSource, entity, view, schema, post);
            DiffCaggPolicies(effectiveSource, entity, view, schema, post);
        }
    }

    private static string Query(IEntityType entity)
        => (string)entity.FindAnnotation(TimescaleDbAnnotationNames.ContinuousAggregateQuery)!.Value!;

    private static Dictionary<(string? Schema, string View), HashSet<(string?, string)>> BuildCaggDependencies(
        Dictionary<(string? Schema, string Name), IEntityType> target)
    {
        var deps = new Dictionary<(string?, string), HashSet<(string?, string)>>();
        foreach (var (key, entity) in target)
        {
            var query = Query(entity);
            var references = new HashSet<(string?, string)>();
            foreach (var (other, _) in target)
            {
                if (!other.Equals(key) && ReferencesView(query, other.Name))
                {
                    references.Add(other);
                }
            }

            deps[key] = references;
        }

        return deps;
    }

    private static bool ReferencesView(string query, string view)
        => System.Text.RegularExpressions.Regex.IsMatch(
            query, $@"(?<![\w""]){System.Text.RegularExpressions.Regex.Escape(view)}(?![\w""])");

    private static List<(string?, string)> TopoSortCaggs(
        List<(string?, string)> keys,
        Dictionary<(string? Schema, string View), HashSet<(string?, string)>> deps)
    {
        var included = keys.ToHashSet();
        var state = new Dictionary<(string?, string), int>(); // 0 unvisited, 1 visiting, 2 done
        var result = new List<(string?, string)>();

        void Visit((string?, string) key)
        {
            if (!included.Contains(key) || state.GetValueOrDefault(key) == 2)
            {
                return;
            }

            if (state.GetValueOrDefault(key) == 1)
            {
                throw new InvalidOperationException(
                    $"Continuous aggregates have a circular dependency involving '{key.Item2}'.");
            }

            state[key] = 1;
            if (deps.TryGetValue(key, out var dependsOn))
            {
                foreach (var dependency in dependsOn)
                {
                    Visit(dependency);
                }
            }

            state[key] = 2;
            result.Add(key);
        }

        foreach (var key in keys)
        {
            Visit(key);
        }

        return result; // dependencies before dependents
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
                    job.FixedSchedule, job.InitialStart, job.Timezone,
                    job.MaxRuntime, job.MaxRetries, job.RetryPeriod)));
            }
        }
    }

    // ---------------------------------------------------------------- reorder policy

    /// <summary>
    ///     Reorder policies reference an index by database name. The index is created by EF's
    ///     <c>CreateIndexOperation</c>, which runs after the table — so <c>add_reorder_policy</c> is
    ///     emitted as a post-base <see cref="SqlOperation" /> (after the index exists) and the matching
    ///     <c>remove_reorder_policy</c> as a pre-base op (while the hypertable still exists). Symmetric,
    ///     so Down migrations reverse automatically.
    /// </summary>
    private static void DiffReorderPolicy(
        IModel? sourceModel,
        IModel? targetModel,
        List<MigrationOperation> pre,
        List<MigrationOperation> post)
    {
        var source = ReorderIndexes(sourceModel);
        var target = ReorderIndexes(targetModel);

        foreach (var (key, index) in source)
        {
            if (!target.TryGetValue(key, out var targetIndex) || targetIndex != index)
            {
                pre.Add(Sql(TimescaleDbSql.RemoveReorderPolicy(key.Table, key.Schema)));
            }
        }

        foreach (var (key, index) in target)
        {
            if (!source.TryGetValue(key, out var sourceIndex) || sourceIndex != index)
            {
                post.Add(Sql(TimescaleDbSql.AddReorderPolicy(key.Table, key.Schema, index)));
            }
        }
    }

    private static Dictionary<(string? Schema, string Table), string> ReorderIndexes(IModel? model)
    {
        var result = new Dictionary<(string? Schema, string Table), string>();
        foreach (var entity in model?.GetEntityTypes() ?? [])
        {
            if (entity.FindAnnotation(TimescaleDbAnnotationNames.IsHypertable)?.Value is true
                && entity.FindAnnotation(TimescaleDbAnnotationNames.ReorderPolicyIndex)?.Value is string index
                && entity.GetTableName() is { } table)
            {
                result[(entity.GetSchema(), table)] = index;
            }
        }

        return result;
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
