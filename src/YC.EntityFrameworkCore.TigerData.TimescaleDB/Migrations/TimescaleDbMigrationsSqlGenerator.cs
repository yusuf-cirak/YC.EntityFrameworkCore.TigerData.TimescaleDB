using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Infrastructure.Internal;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.Migrations.Internal;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Migrations;

/// <summary>
///     Interprets the TimescaleDB table annotations that EF attaches to
///     <c>CreateTableOperation</c> / <c>AlterTableOperation</c> and emits the matching DDL.
///     Because the annotations carry both old and new values, the generator produces forward,
///     reverse and (where TimescaleDB cannot change in place) rebuild SQL symmetrically — Down
///     migrations work with no extra code. Continuous aggregates and jobs are emitted as
///     <c>SqlOperation</c> by the model differ.
/// </summary>
public class TimescaleDbMigrationsSqlGenerator : NpgsqlMigrationsSqlGenerator
{
    private readonly bool _migrateDataDefault;
    private readonly bool _rebuildDataDefault;
    private readonly bool _autoDecompressDefault;

    public TimescaleDbMigrationsSqlGenerator(
        MigrationsSqlGeneratorDependencies dependencies,
        INpgsqlSingletonOptions npgsqlSingletonOptions,
        IDbContextOptions contextOptions)
        : base(dependencies, npgsqlSingletonOptions)
    {
        var extension = contextOptions.FindExtension<TimescaleDbOptionsExtension>();
        _migrateDataDefault = extension?.MigrateData ?? true;
        _rebuildDataDefault = extension?.RebuildData ?? true;
        _autoDecompressDefault = extension?.AutoDecompress ?? true;
    }

    private TimescaleDbTableState ReadState(IReadOnlyAnnotatable source)
        => TimescaleDbTableState.Read(source, _migrateDataDefault, _rebuildDataDefault, _autoDecompressDefault);

    protected override void Generate(
        CreateTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        base.Generate(operation, model, builder, terminate);

        if (!terminate)
        {
            return;
        }

        var state = ReadState(operation);
        if (state.IsHypertable)
        {
            EmitHypertableSetup(operation.Name, operation.Schema, state, builder, migrateData: false);
        }
    }

    protected override void Generate(AlterTableOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        base.Generate(operation, model, builder);

        var old = ReadState(operation.OldTable);
        var @new = ReadState(operation);

        if (RequiresRebuild(old, @new))
        {
            if (!@new.RebuildData)
            {
                throw new InvalidOperationException(
                    $"The change to table '{operation.Name}' requires a data-copying table rebuild "
                    + "(hypertable→plain, partition column change, or space dimension drop/repartition), "
                    + "but rebuilds are disabled for this entity (WithRebuildData(false) / "
                    + "[MigrationOptions(RebuildData = false)]). Re-enable it, or perform the change manually.");
            }

            EmitRebuild(operation.Name, operation.Schema, @new, builder);
            return;
        }

        if (!old.IsHypertable && @new.IsHypertable)
        {
            // Existing (possibly populated) plain table becomes a hypertable.
            EmitHypertableSetup(operation.Name, operation.Schema, @new, builder, migrateData: @new.MigrateData);
            return;
        }

        if (!@new.IsHypertable)
        {
            // Both plain (hypertable→plain is a rebuild, handled above).
            return;
        }

        EmitInPlaceDiff(operation.Name, operation.Schema, old, @new, builder);
    }

    // ---------------------------------------------------------------- create / setup

    private void EmitHypertableSetup(
        string table,
        string? schema,
        TimescaleDbTableState s,
        MigrationCommandListBuilder builder,
        bool migrateData)
    {
        Statement(builder, TimescaleDbSql.CreateHypertable(
            table, schema, s.PartitionColumn!, s.ChunkInterval, s.CreateDefaultIndexes, migrateData));

        foreach (var dimension in s.SpaceDimensions)
        {
            Statement(builder, TimescaleDbSql.AddDimension(table, schema, dimension.Column, dimension.Partitions));
        }

        if (s.IntegerNowFunction is not null)
        {
            Statement(builder, TimescaleDbSql.SetIntegerNowFunction(table, schema, s.IntegerNowFunction));
        }

        foreach (var tablespace in s.Tablespaces)
        {
            Statement(builder, TimescaleDbSql.AttachTablespace(table, schema, tablespace));
        }

        if (s.Columnstore)
        {
            Statement(builder, TimescaleDbSql.SetColumnstore(table, schema, s.SegmentBy, s.OrderBy, s.MergeInterval));
        }

        foreach (var column in s.ChunkSkipping)
        {
            Statement(builder, TimescaleDbSql.EnableChunkSkipping(table, schema, column));
        }

        EmitAddPolicies(table, schema, s, builder);
    }

    private void EmitAddPolicies(
        string table,
        string? schema,
        TimescaleDbTableState s,
        MigrationCommandListBuilder builder)
    {
        if (s.Retention.Exists)
        {
            Statement(builder, TimescaleDbSql.AddRetentionPolicy(
                table, schema, s.Retention.Main!, s.Retention.Schedule, s.Retention.InitialStart, s.Retention.Timezone));
        }

        if (s.ColumnstorePolicy.Exists)
        {
            Statement(builder, TimescaleDbSql.AddColumnstorePolicy(
                table, schema, s.ColumnstorePolicy.Main!, s.ColumnstorePolicy.Schedule,
                s.ColumnstorePolicy.InitialStart, s.ColumnstorePolicy.Timezone));
        }

        // The reorder policy is emitted by the model differ (after the index is created), not here.
    }

    // ---------------------------------------------------------------- in-place diff

    private void EmitInPlaceDiff(
        string table,
        string? schema,
        TimescaleDbTableState old,
        TimescaleDbTableState @new,
        MigrationCommandListBuilder builder)
    {
        if (@new.ChunkInterval is not null && @new.ChunkInterval != old.ChunkInterval)
        {
            Statement(builder, TimescaleDbSql.SetChunkInterval(table, schema, @new.ChunkInterval));
        }

        // Space dimensions added (removal / partition-count change forces a rebuild — see RequiresRebuild).
        var oldColumns = old.SpaceDimensions.Select(d => d.Column).ToHashSet(StringComparer.Ordinal);
        foreach (var dimension in @new.SpaceDimensions.Where(d => !oldColumns.Contains(d.Column)))
        {
            Statement(builder, TimescaleDbSql.AddDimension(table, schema, dimension.Column, dimension.Partitions));
        }

        if (@new.IntegerNowFunction is not null && @new.IntegerNowFunction != old.IntegerNowFunction)
        {
            Statement(builder, TimescaleDbSql.SetIntegerNowFunction(table, schema, @new.IntegerNowFunction));
        }

        // Tablespaces are reversible in place: attach the new, detach the gone.
        foreach (var tablespace in @new.Tablespaces.Except(old.Tablespaces, StringComparer.Ordinal))
        {
            Statement(builder, TimescaleDbSql.AttachTablespace(table, schema, tablespace));
        }

        foreach (var tablespace in old.Tablespaces.Except(@new.Tablespaces, StringComparer.Ordinal))
        {
            Statement(builder, TimescaleDbSql.DetachTablespace(table, schema, tablespace));
        }

        foreach (var column in @new.ChunkSkipping.Except(old.ChunkSkipping, StringComparer.Ordinal))
        {
            Statement(builder, TimescaleDbSql.EnableChunkSkipping(table, schema, column));
        }

        foreach (var column in old.ChunkSkipping.Except(@new.ChunkSkipping, StringComparer.Ordinal))
        {
            Statement(builder, TimescaleDbSql.DisableChunkSkipping(table, schema, column));
        }

        EmitColumnstoreDiff(table, schema, old, @new, builder);

        EmitPolicyDiff(
            old.Retention, @new.Retention, builder,
            add: p => TimescaleDbSql.AddRetentionPolicy(table, schema, p.Main!, p.Schedule, p.InitialStart, p.Timezone),
            remove: () => TimescaleDbSql.RemoveRetentionPolicy(table, schema));

        EmitPolicyDiff(
            old.ColumnstorePolicy, @new.ColumnstorePolicy, builder,
            add: p => TimescaleDbSql.AddColumnstorePolicy(table, schema, p.Main!, p.Schedule, p.InitialStart, p.Timezone),
            remove: () => TimescaleDbSql.RemoveColumnstorePolicy(table, schema));

        // The reorder policy is emitted by the model differ (it must run after the index exists).
    }

    private void EmitColumnstoreDiff(
        string table,
        string? schema,
        TimescaleDbTableState old,
        TimescaleDbTableState @new,
        MigrationCommandListBuilder builder)
    {
        switch (old.Columnstore, @new.Columnstore)
        {
            case (false, true):
                Statement(builder, TimescaleDbSql.SetColumnstore(
                    table, schema, @new.SegmentBy, @new.OrderBy, @new.MergeInterval));
                break;

            case (true, false):
                // Decompress every chunk, then disable; run outside the migration transaction.
                // When auto-decompress is off, only the policy is removed and the flag flipped.
                Statement(
                    builder,
                    TimescaleDbSql.DisableColumnstore(table, schema, decompress: @new.AutoDecompress),
                    suppressTransaction: @new.AutoDecompress);
                break;

            case (true, true) when @new.ColumnstoreLayoutDiffers(old):
                Statement(builder, TimescaleDbSql.SetColumnstore(
                    table, schema, @new.SegmentBy, @new.OrderBy, @new.MergeInterval));
                break;
        }
    }

    private void EmitPolicyDiff(
        PolicyState old,
        PolicyState @new,
        MigrationCommandListBuilder builder,
        Func<PolicyState, string> add,
        Func<string> remove)
    {
        if (old == @new)
        {
            return;
        }

        if (old.Exists)
        {
            Statement(builder, remove());
        }

        if (@new.Exists)
        {
            Statement(builder, add(@new));
        }
    }

    // ---------------------------------------------------------------- rebuild

    private static bool RequiresRebuild(TimescaleDbTableState old, TimescaleDbTableState @new)
    {
        if (old.IsHypertable && !@new.IsHypertable)
        {
            return true; // un-hypertable: no native reverse, must rebuild as a plain table
        }

        if (old.IsHypertable && @new.IsHypertable)
        {
            if (old.PartitionColumn != @new.PartitionColumn)
            {
                return true; // cannot repartition in place
            }

            // Any existing space dimension dropped or its partition count changed → rebuild
            // (TimescaleDB cannot drop or alter a dimension). New dimensions are added in place.
            var newByColumn = @new.SpaceDimensions.ToDictionary(d => d.Column, d => d.Partitions, StringComparer.Ordinal);
            foreach (var dimension in old.SpaceDimensions)
            {
                if (!newByColumn.TryGetValue(dimension.Column, out var partitions)
                    || partitions != dimension.Partitions)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void EmitRebuild(
        string table,
        string? schema,
        TimescaleDbTableState target,
        MigrationCommandListBuilder builder)
    {
        var shadow = table + "__ts_rebuild";
        var orig = TimescaleDbSql.Qualified(table, schema);
        var tmp = TimescaleDbSql.Qualified(shadow, schema);

        builder
            .AppendLine("-- WARNING: TimescaleDB cannot apply this change in place; the table is rebuilt by")
            .AppendLine("-- copying every row under lock. FOREIGN KEY constraints (inbound and outbound) and")
            .AppendLine("-- exact index names are NOT preserved by CREATE TABLE (LIKE ...); re-create them manually.");

        Statement(builder, $"CREATE TABLE {tmp} (LIKE {orig} INCLUDING ALL);");

        if (target.IsHypertable)
        {
            Statement(builder, TimescaleDbSql.CreateHypertable(
                shadow, schema, target.PartitionColumn!, target.ChunkInterval, target.CreateDefaultIndexes,
                migrateData: false));

            foreach (var dimension in target.SpaceDimensions)
            {
                Statement(builder, TimescaleDbSql.AddDimension(shadow, schema, dimension.Column, dimension.Partitions));
            }

            if (target.IntegerNowFunction is not null)
            {
                Statement(builder, TimescaleDbSql.SetIntegerNowFunction(shadow, schema, target.IntegerNowFunction));
            }
        }

        Statement(builder, $"INSERT INTO {tmp} SELECT * FROM {orig};");
        Statement(builder, $"DROP TABLE {orig} CASCADE;");
        Statement(builder, $"ALTER TABLE {tmp} RENAME TO {TimescaleDbSql.Quote(table)};");

        if (target.IsHypertable)
        {
            foreach (var tablespace in target.Tablespaces)
            {
                Statement(builder, TimescaleDbSql.AttachTablespace(table, schema, tablespace));
            }

            if (target.Columnstore)
            {
                Statement(builder, TimescaleDbSql.SetColumnstore(
                    table, schema, target.SegmentBy, target.OrderBy, target.MergeInterval));
            }

            foreach (var column in target.ChunkSkipping)
            {
                Statement(builder, TimescaleDbSql.EnableChunkSkipping(table, schema, column));
            }

            EmitAddPolicies(table, schema, target, builder);
        }
    }

    // ---------------------------------------------------------------- helpers

    private void Statement(MigrationCommandListBuilder builder, string sql, bool suppressTransaction = false)
    {
        builder.AppendLine(sql);
        EndStatement(builder, suppressTransaction);
    }
}
