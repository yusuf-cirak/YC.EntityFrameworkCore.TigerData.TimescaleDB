using YC.EntityFrameworkCore.TigerData.TimescaleDB;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Migrations;

/// <summary>
///     Full add / replace / remove lifecycle of every hypertable policy — retention, columnstore
///     (auto-conversion) and reorder. Each is an in-place, reversible diff; a "replace" must emit
///     <c>remove_*</c> before <c>add_*</c>, and an unchanged policy must emit nothing.
/// </summary>
public class PolicyTransitionTests
{
    private class Reading
    {
        public DateTimeOffset Time { get; set; }
        public string DeviceId { get; set; } = null!;
        public double Value { get; set; }
    }

    private static Action<EntityTypeBuilder<Reading>> Configure()
        => e =>
        {
            e.HasNoKey();
            e.ToTable("readings");
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Value).HasColumnName("value");
        };

    private static Action<ModelBuilder> Hypertable(Action<EntityTypeBuilder<Reading>>? extra = null)
        => mb => mb.Entity<Reading>(e =>
        {
            Configure()(e);
            e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
            extra?.Invoke(e);
        });

    // ---------------------------------------------------------------- retention

    [Fact]
    public void Retention_added_emits_add_retention_policy()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(),
            Hypertable(e => e.HasRetentionPolicy(90, Every.Day)));

        Assert.Contains(sql, s => s.Contains(
            "SELECT add_retention_policy('\"readings\"', drop_after => INTERVAL '90 days');"));
        Assert.DoesNotContain(sql, s => s.Contains("remove_retention_policy"));
    }

    [Fact]
    public void Retention_removed_emits_remove_retention_policy()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasRetentionPolicy(90, Every.Day)),
            Hypertable());

        Assert.Contains(sql, s => s.Contains("SELECT remove_retention_policy('\"readings\"', if_exists => true);"));
        Assert.DoesNotContain(sql, s => s.Contains("add_retention_policy"));
    }

    [Fact]
    public void Retention_replaced_removes_then_adds()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasRetentionPolicy(90, Every.Day)),
            Hypertable(e => e.HasRetentionPolicy(30, Every.Day))).ToList();

        var remove = sql.FindIndex(s => s.Contains("remove_retention_policy"));
        var add = sql.FindIndex(s => s.Contains("add_retention_policy") && s.Contains("INTERVAL '30 days'"));

        Assert.True(remove >= 0 && add > remove, string.Join("\n---\n", sql));
    }

    [Fact]
    public void Retention_unchanged_emits_nothing()
    {
        var model = Hypertable(e => e.HasRetentionPolicy(90, Every.Day));
        Assert.Empty(MigrationSqlHelper.GenerateSql(model, model));
    }

    // ---------------------------------------------------------------- columnstore policy

    private static Action<EntityTypeBuilder<Reading>> WithColumnstore(Action<EntityTypeBuilder<Reading>> extra)
        => e =>
        {
            e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId));
            extra(e);
        };

    [Fact]
    public void Columnstore_policy_added_emits_add_columnstore_policy()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId))),
            Hypertable(WithColumnstore(e => e.HasColumnstorePolicy(7, Every.Day))));

        Assert.Contains(sql, s => s.Contains(
            "CALL add_columnstore_policy('\"readings\"', after => INTERVAL '7 days');"));
        Assert.DoesNotContain(sql, s => s.Contains("remove_columnstore_policy"));
    }

    [Fact]
    public void Columnstore_policy_removed_emits_remove_columnstore_policy()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(WithColumnstore(e => e.HasColumnstorePolicy(7, Every.Day))),
            Hypertable(e => e.HasColumnstore(cs => cs.SegmentBy(x => x.DeviceId))));

        Assert.Contains(sql, s => s.Contains(
            "CALL remove_columnstore_policy('\"readings\"', if_exists => true);"));
        Assert.DoesNotContain(sql, s => s.Contains("add_columnstore_policy"));
    }

    [Fact]
    public void Columnstore_policy_replaced_removes_then_adds()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(WithColumnstore(e => e.HasColumnstorePolicy(7, Every.Day))),
            Hypertable(WithColumnstore(e => e.HasColumnstorePolicy(14, Every.Day)))).ToList();

        var remove = sql.FindIndex(s => s.Contains("remove_columnstore_policy"));
        var add = sql.FindIndex(s => s.Contains("add_columnstore_policy") && s.Contains("INTERVAL '14 days'"));

        Assert.True(remove >= 0 && add > remove, string.Join("\n---\n", sql));
    }

    // ---------------------------------------------------------------- reorder (named index)

    [Fact]
    public void Reorder_by_name_added_emits_add_reorder_policy()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(),
            Hypertable(e => e.HasReorderPolicy("readings_time_idx")));

        Assert.Contains(sql, s => s.Contains(
            "SELECT add_reorder_policy('\"readings\"', 'readings_time_idx', if_not_exists => true);"));
    }

    [Fact]
    public void Reorder_by_name_removed_emits_remove_reorder_policy()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasReorderPolicy("readings_time_idx")),
            Hypertable());

        Assert.Contains(sql, s => s.Contains("SELECT remove_reorder_policy('\"readings\"', if_exists => true);"));
        Assert.DoesNotContain(sql, s => s.Contains("add_reorder_policy"));
    }

    [Fact]
    public void Reorder_by_name_replaced_removes_then_adds()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e => e.HasReorderPolicy("old_idx")),
            Hypertable(e => e.HasReorderPolicy("new_idx"))).ToList();

        var remove = sql.FindIndex(s => s.Contains("remove_reorder_policy"));
        var add = sql.FindIndex(s => s.Contains("add_reorder_policy") && s.Contains("'new_idx'"));

        Assert.True(remove >= 0 && add > remove, string.Join("\n---\n", sql));
    }

    // ---------------------------------------------------------------- reorder (by EF index expression)

    [Fact]
    public void Reorder_by_index_expression_resolves_database_name()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(),
            Hypertable(e =>
            {
                e.HasIndex(x => new { x.DeviceId, x.Time });
                e.HasReorderPolicy(x => new { x.DeviceId, x.Time });
            }));

        // The EF index's resolved database name (whatever Npgsql produced) is passed to add_reorder_policy.
        Assert.Contains(sql, s => s.Contains("SELECT add_reorder_policy('\"readings\"', '")
            && s.Contains("if_not_exists => true);"));
    }

    [Fact]
    public void Reorder_by_index_expression_removed_emits_remove()
    {
        var sql = MigrationSqlHelper.GenerateSql(
            Hypertable(e =>
            {
                e.HasIndex(x => new { x.DeviceId, x.Time });
                e.HasReorderPolicy(x => new { x.DeviceId, x.Time });
            }),
            Hypertable(e => e.HasIndex(x => new { x.DeviceId, x.Time })));

        Assert.Contains(sql, s => s.Contains("SELECT remove_reorder_policy('\"readings\"', if_exists => true);"));
    }

    [Fact]
    public void Reorder_unchanged_emits_nothing()
    {
        var model = Hypertable(e => e.HasReorderPolicy("readings_time_idx"));
        Assert.Empty(MigrationSqlHelper.GenerateSql(model, model));
    }
}
