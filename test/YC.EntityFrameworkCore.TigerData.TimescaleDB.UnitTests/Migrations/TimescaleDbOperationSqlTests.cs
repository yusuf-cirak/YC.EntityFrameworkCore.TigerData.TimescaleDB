using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Migrations;

/// <summary>
///     Non-table objects (extension, continuous aggregates, jobs) emitted by the differ as
///     <c>migrationBuilder.Sql(...)</c>, and their reversals.
/// </summary>
public class TimescaleDbOperationSqlTests
{
    private class Reading
    {
        public DateTime Time { get; set; }
        public string DeviceId { get; set; } = null!;
        public string Region { get; set; } = null!;
        public double Value { get; set; }
    }

    private class HourlyAvg
    {
        public DateTime Bucket { get; set; }
        public double Avg { get; set; }
    }

    private const string CaggQuery =
        "SELECT time_bucket('1 hour', time) AS bucket, avg(value) AS avg FROM readings GROUP BY 1";

    private static void Hypertable(ModelBuilder mb) => mb.Entity<Reading>(e =>
    {
        e.HasNoKey();
        e.ToTable("readings");
        e.Property(x => x.Time).HasColumnName("time");
        e.Property(x => x.DeviceId).HasColumnName("device_id");
        e.Property(x => x.Value).HasColumnName("value");
        e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
    });

    [Fact]
    public void Initial_migration_emits_extension_before_create_table()
    {
        var sql = MigrationSqlHelper.GenerateSql(Hypertable);
        var list = sql.ToList();

        var extension = list.FindIndex(s => s.Contains("CREATE EXTENSION IF NOT EXISTS timescaledb"));
        var createTable = list.FindIndex(s => s.Contains("CREATE TABLE"));

        Assert.True(extension >= 0 && extension < createTable, string.Join("\n---\n", sql));
    }

    [Fact]
    public void Continuous_aggregate_and_job_emitted_as_sql()
    {
        var sql = MigrationSqlHelper.GenerateSql(mb =>
        {
            Hypertable(mb);
            mb.Entity<HourlyAvg>(e =>
            {
                e.HasNoKey();
                e.Property(x => x.Bucket).HasColumnName("bucket");
                e.Property(x => x.Avg).HasColumnName("avg");
                e.IsContinuousAggregate("hourly_avg", CaggQuery);
                e.HasRefreshPolicy(TimeSpan.FromDays(3), TimeSpan.FromHours(1), TimeSpan.FromHours(1));
            });
            mb.HasTimescaleDbJob("nightly_cleanup", "public.cleanup", scheduleInterval: TimeSpan.FromDays(1),
                config: """{"drop_after":"30 days"}""");
        });

        Assert.Contains(sql, s => s.Contains("CREATE MATERIALIZED VIEW \"hourly_avg\"")
            && s.Contains("WITH (timescaledb.continuous)")
            && s.Contains(CaggQuery)
            && s.Contains("WITH NO DATA"));
        Assert.Contains(sql, s => s.Contains(
            "SELECT add_continuous_aggregate_policy('\"hourly_avg\"', start_offset => INTERVAL '3 days', "
            + "end_offset => INTERVAL '01:00:00', schedule_interval => INTERVAL '01:00:00');"));
        Assert.Contains(sql, s => s.Contains("SELECT add_job('public.cleanup'::regproc")
            && s.Contains("schedule_interval => INTERVAL '1 day'")
            && s.Contains("""config => '{"drop_after":"30 days"}'::jsonb""")
            && s.Contains("job_name => 'nightly_cleanup'"));
    }

    [Fact]
    public void Dropping_continuous_aggregate_and_job_emits_drop_and_delete()
    {
        Action<ModelBuilder> withCagg = mb =>
        {
            Hypertable(mb);
            mb.Entity<HourlyAvg>(e =>
            {
                e.HasNoKey();
                e.Property(x => x.Bucket).HasColumnName("bucket");
                e.Property(x => x.Avg).HasColumnName("avg");
                e.IsContinuousAggregate("hourly_avg", CaggQuery);
            });
            mb.HasTimescaleDbJob("nightly_cleanup", "public.cleanup");
        };

        var sql = MigrationSqlHelper.GenerateSql(withCagg, Hypertable);

        Assert.Contains(sql, s => s.Contains("DROP MATERIALIZED VIEW IF EXISTS \"hourly_avg\";"));
        Assert.Contains(sql, s => s.Contains("delete_job") && s.Contains("'nightly_cleanup'"));
    }

    [Fact]
    public void Toggling_materialized_only_emits_alter_materialized_view()
    {
        Action<ModelBuilder> Cagg(bool materializedOnly) => mb => mb.Entity<HourlyAvg>(e =>
        {
            e.HasNoKey();
            e.IsContinuousAggregate("hourly_avg", CaggQuery, materializedOnly: materializedOnly);
        });

        var sql = MigrationSqlHelper.GenerateSql(Cagg(true), Cagg(false));

        Assert.Contains(sql, s => s.Contains(
            "ALTER MATERIALIZED VIEW \"hourly_avg\" SET (timescaledb.materialized_only = false);"));
        Assert.DoesNotContain(sql, s => s.Contains("DROP MATERIALIZED VIEW"));
    }

    [Fact]
    public void Changing_cagg_query_recreates_view_and_refresh_policy()
    {
        Action<ModelBuilder> Cagg(string query) => mb => mb.Entity<HourlyAvg>(e =>
        {
            e.HasNoKey();
            e.IsContinuousAggregate("hourly_avg", query);
            e.HasRefreshPolicy(TimeSpan.FromDays(3), TimeSpan.FromHours(1));
        });

        const string newQuery =
            "SELECT time_bucket('2 hours', time) AS bucket, avg(value) AS avg FROM readings GROUP BY 1";

        var sql = MigrationSqlHelper.GenerateSql(Cagg(CaggQuery), Cagg(newQuery));

        Assert.Contains(sql, s => s.Contains("DROP MATERIALIZED VIEW IF EXISTS \"hourly_avg\";"));
        Assert.Contains(sql, s => s.Contains("CREATE MATERIALIZED VIEW \"hourly_avg\"") && s.Contains(newQuery));
        Assert.Contains(sql, s => s.Contains("add_continuous_aggregate_policy"));
    }

    [Fact]
    public void Cagg_columnstore_emits_alter_materialized_view()
    {
        Action<ModelBuilder> Cagg(bool columnstore) => mb => mb.Entity<HourlyAvg>(e =>
        {
            e.HasNoKey();
            e.Property(x => x.Bucket).HasColumnName("bucket");
            e.IsContinuousAggregate("hourly_avg", CaggQuery);
            if (columnstore)
            {
                e.HasColumnstore(cs => cs.OrderByDescending(x => x.Bucket));
            }
        });

        var sql = MigrationSqlHelper.GenerateSql(Cagg(false), Cagg(true));

        Assert.Contains(sql, s => s.Contains(
            "ALTER MATERIALIZED VIEW \"hourly_avg\" SET (timescaledb.enable_columnstore = true, "
            + "timescaledb.orderby = 'bucket DESC');"));
    }

    [Fact]
    public void Cagg_retention_policy_added_and_removed()
    {
        Action<ModelBuilder> Cagg(bool retention) => mb =>
        {
            Hypertable(mb);
            mb.Entity<HourlyAvg>(e =>
            {
                e.HasNoKey();
                e.Property(x => x.Bucket).HasColumnName("bucket");
                e.Property(x => x.Avg).HasColumnName("avg");
                e.IsContinuousAggregate("hourly_avg", CaggQuery);
                if (retention)
                {
                    e.HasRetentionPolicy(7, Every.Day);
                }
            });
        };

        var added = MigrationSqlHelper.GenerateSql(Cagg(false), Cagg(true));
        Assert.Contains(added, s => s.Contains(
            "SELECT add_retention_policy('\"hourly_avg\"', drop_after => INTERVAL '7 days');"));

        var removed = MigrationSqlHelper.GenerateSql(Cagg(true), Cagg(false));
        Assert.Contains(removed, s => s.Contains(
            "SELECT remove_retention_policy('\"hourly_avg\"', if_exists => true);"));
    }

    [Fact]
    public void Cagg_columnstore_policy_added_and_removed()
    {
        Action<ModelBuilder> Cagg(bool policy) => mb =>
        {
            Hypertable(mb);
            mb.Entity<HourlyAvg>(e =>
            {
                e.HasNoKey();
                e.Property(x => x.Bucket).HasColumnName("bucket");
                e.Property(x => x.Avg).HasColumnName("avg");
                e.IsContinuousAggregate("hourly_avg", CaggQuery);
                e.HasColumnstore(cs => cs.OrderByDescending(x => x.Bucket));
                if (policy)
                {
                    e.HasColumnstorePolicy(7, Every.Day);
                }
            });
        };

        var added = MigrationSqlHelper.GenerateSql(Cagg(false), Cagg(true));
        Assert.Contains(added, s => s.Contains(
            "CALL add_columnstore_policy('\"hourly_avg\"', after => INTERVAL '7 days');"));

        var removed = MigrationSqlHelper.GenerateSql(Cagg(true), Cagg(false));
        Assert.Contains(removed, s => s.Contains(
            "CALL remove_columnstore_policy('\"hourly_avg\"', if_exists => true);"));
    }

    [Fact]
    public void Retention_policy_drop_created_before_emits_interval()
    {
        Action<ModelBuilder> Model(bool policy) => mb =>
        {
            Hypertable(mb);
            if (policy)
            {
                mb.Entity<Reading>(e => e.HasRetentionPolicy(dropCreatedBefore: TimeSpan.FromDays(180)));
            }
        };

        var added = MigrationSqlHelper.GenerateSql(Model(false), Model(true));
        Assert.Contains(added, s => s.Contains(
            "SELECT add_retention_policy('\"readings\"', drop_created_before => INTERVAL '180 days');"));

        var removed = MigrationSqlHelper.GenerateSql(Model(true), Model(false));
        Assert.Contains(removed, s => s.Contains(
            "SELECT remove_retention_policy('\"readings\"', if_exists => true);"));
    }

    [Fact]
    public void Columnstore_policy_created_before_emits_interval()
    {
        Action<ModelBuilder> Model(bool policy) => mb =>
        {
            Hypertable(mb);
            mb.Entity<Reading>(e =>
            {
                e.HasColumnstore(cs => cs.OrderByDescending(x => x.Time));
                if (policy)
                {
                    e.HasColumnstorePolicy(createdBefore: TimeSpan.FromDays(7));
                }
            });
        };

        var added = MigrationSqlHelper.GenerateSql(Model(false), Model(true));
        Assert.Contains(added, s => s.Contains(
            "CALL add_columnstore_policy('\"readings\"', created_before => INTERVAL '7 days');"));

        var removed = MigrationSqlHelper.GenerateSql(Model(true), Model(false));
        Assert.Contains(removed, s => s.Contains(
            "CALL remove_columnstore_policy('\"readings\"', if_exists => true);"));
    }

    [Fact]
    public void Creation_time_policies_round_trip_emits_no_policy_sql()
    {
        Action<ModelBuilder> Model = mb =>
        {
            Hypertable(mb);
            mb.Entity<Reading>(e =>
            {
                e.HasColumnstore(cs => cs.OrderByDescending(x => x.Time));
                e.HasRetentionPolicy(dropCreatedBefore: TimeSpan.FromDays(180));
                e.HasColumnstorePolicy(createdBefore: TimeSpan.FromDays(7));
            });
        };

        var sql = MigrationSqlHelper.GenerateSql(Model, Model);

        Assert.DoesNotContain(sql, s =>
            s.Contains("add_retention_policy") || s.Contains("remove_retention_policy")
            || s.Contains("add_columnstore_policy") || s.Contains("remove_columnstore_policy"));
    }

    [Fact]
    public void Retention_policy_drop_after_and_drop_created_before_together_throws()
    {
        Action<ModelBuilder> Model = mb =>
        {
            Hypertable(mb);
            mb.Entity<Reading>(e => e.HasRetentionPolicy(
                dropAfter: TimeSpan.FromDays(30), dropCreatedBefore: TimeSpan.FromDays(180)));
        };

        Assert.Throws<InvalidOperationException>(() => MigrationSqlHelper.GenerateSql(Model).ToList());
    }

    [Fact]
    public void Retention_policy_drop_created_before_on_continuous_aggregate_throws()
    {
        Action<ModelBuilder> Model = mb =>
        {
            Hypertable(mb);
            mb.Entity<HourlyAvg>(e =>
            {
                e.HasNoKey();
                e.Property(x => x.Bucket).HasColumnName("bucket");
                e.Property(x => x.Avg).HasColumnName("avg");
                e.IsContinuousAggregate("hourly_avg", CaggQuery);
                e.HasRetentionPolicy(dropCreatedBefore: TimeSpan.FromDays(180));
            });
        };

        Assert.Throws<InvalidOperationException>(() => MigrationSqlHelper.GenerateSql(Model).ToList());
    }

    [Fact]
    public void Cagg_columnstore_disable_emits_alter_set_false()
    {
        Action<ModelBuilder> Cagg(bool columnstore) => mb =>
        {
            Hypertable(mb);
            mb.Entity<HourlyAvg>(e =>
            {
                e.HasNoKey();
                e.Property(x => x.Bucket).HasColumnName("bucket");
                e.IsContinuousAggregate("hourly_avg", CaggQuery);
                if (columnstore)
                {
                    e.HasColumnstore(cs => cs.OrderByDescending(x => x.Bucket));
                }
            });
        };

        var sql = MigrationSqlHelper.GenerateSql(Cagg(true), Cagg(false));

        Assert.Contains(sql, s => s.Contains(
            "ALTER MATERIALIZED VIEW \"hourly_avg\" SET (timescaledb.enable_columnstore = false);"));
    }

    [Fact]
    public void Cagg_refresh_policy_schedule_change_recreates_policy()
    {
        Action<ModelBuilder> Cagg(TimeSpan schedule) => mb =>
        {
            Hypertable(mb);
            mb.Entity<HourlyAvg>(e =>
            {
                e.HasNoKey();
                e.Property(x => x.Bucket).HasColumnName("bucket");
                e.IsContinuousAggregate("hourly_avg", CaggQuery);
                e.HasRefreshPolicy(TimeSpan.FromDays(3), TimeSpan.FromHours(1), scheduleInterval: schedule);
            });
        };

        var sql = MigrationSqlHelper.GenerateSql(Cagg(TimeSpan.FromHours(1)), Cagg(TimeSpan.FromHours(2))).ToList();

        var remove = sql.FindIndex(s => s.Contains("remove_continuous_aggregate_policy"));
        var add = sql.FindIndex(s => s.Contains("add_continuous_aggregate_policy")
            && s.Contains("schedule_interval => INTERVAL '02:00:00'"));

        Assert.True(remove >= 0 && add > remove, string.Join("\n---\n", sql));
    }

    [Fact]
    public void Job_config_change_deletes_then_re_adds()
    {
        Action<ModelBuilder> Model(string config) => mb =>
        {
            Hypertable(mb);
            mb.HasTimescaleDbJob("cleanup", "public.cleanup",
                scheduleInterval: TimeSpan.FromDays(1), config: config);
        };

        var sql = MigrationSqlHelper.GenerateSql(
            Model("""{"drop_after":"30 days"}"""),
            Model("""{"drop_after":"60 days"}"""));

        Assert.Contains(sql, s => s.Contains("delete_job") && s.Contains("'cleanup'"));
        Assert.Contains(sql, s => s.Contains("add_job")
            && s.Contains("""config => '{"drop_after":"60 days"}'::jsonb"""));
    }

    [Fact]
    public void Job_reliability_change_deletes_then_re_adds_with_new_value()
    {
        Action<ModelBuilder> Model(int retries) => mb =>
        {
            Hypertable(mb);
            mb.HasTimescaleDbJob("cleanup", "public.cleanup",
                scheduleInterval: TimeSpan.FromDays(1), maxRetries: retries);
        };

        var sql = MigrationSqlHelper.GenerateSql(Model(3), Model(5));

        Assert.Contains(sql, s => s.Contains("delete_job") && s.Contains("'cleanup'"));
        Assert.Contains(sql, s => s.Contains("add_job") && s.Contains("max_retries => 5"));
    }

    [Fact]
    public void Job_reliability_removed_re_adds_without_reliability_args()
    {
        Action<ModelBuilder> withReliability = mb =>
        {
            Hypertable(mb);
            mb.HasTimescaleDbJob("cleanup", "public.cleanup",
                scheduleInterval: TimeSpan.FromDays(1), maxRuntime: TimeSpan.FromMinutes(5), maxRetries: 3);
        };
        Action<ModelBuilder> without = mb =>
        {
            Hypertable(mb);
            mb.HasTimescaleDbJob("cleanup", "public.cleanup", scheduleInterval: TimeSpan.FromDays(1));
        };

        var sql = MigrationSqlHelper.GenerateSql(withReliability, without);

        var add = Assert.Single(sql, s => s.Contains("add_job"));
        Assert.DoesNotContain("max_runtime", add);
        Assert.DoesNotContain("max_retries", add);
    }

    // The columnstore sparse-index column names are canonicalized to store column names and
    // double-quoted inside the single-quoted sparse_index value.
    private const string SparseIndexSetSql =
        "ALTER TABLE \"readings\" SET (timescaledb.enable_columnstore = true, timescaledb.segmentby = 'device', "
        + "timescaledb.sparse_index = 'minmax(\"temperature\"), bloom(\"device\")');";

    private static void SparseFluent(ModelBuilder mb) => mb.Entity<Reading>(e =>
    {
        e.HasNoKey();
        e.ToTable("readings");
        e.Property(x => x.Time).HasColumnName("time");
        e.Property(x => x.DeviceId).HasColumnName("device");
        e.Property(x => x.Value).HasColumnName("temperature");
        e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
        e.HasColumnstore(cs => cs
            .SegmentBy(x => x.DeviceId)
            .SparseIndexMinMax(x => x.Value)
            .SparseIndexBloom(x => x.DeviceId));
    });

    private class SparseReadingAttr
    {
        [PartitionColumn(1, Every.Day)]
        [Column("time")]
        public DateTime Time { get; set; }

        [Column("device")]
        [SegmentBy]
        [SparseIndex(SparseIndexKind.Bloom, order: 1)]
        public string DeviceId { get; set; } = null!;

        [Column("temperature")]
        [SparseIndex(SparseIndexKind.MinMax, order: 0)]
        public double Value { get; set; }
    }

    private static void SparseAttribute(ModelBuilder mb) => mb.Entity<SparseReadingAttr>(e =>
    {
        e.HasNoKey();
        e.ToTable("readings");
    });

    [Fact]
    public void Columnstore_sparse_index_fluent_emits_sparse_index_clause()
    {
        var sql = MigrationSqlHelper.GenerateSql(SparseFluent);

        Assert.Contains(sql, s => s.Contains(SparseIndexSetSql));
    }

    [Fact]
    public void Columnstore_sparse_index_unchanged_emits_no_columnstore_sql()
    {
        var sql = MigrationSqlHelper.GenerateSql(SparseFluent, SparseFluent);

        Assert.DoesNotContain(sql, s => s.Contains("timescaledb.sparse_index"));
        Assert.DoesNotContain(sql, s => s.Contains("SET (timescaledb.enable_columnstore"));
    }

    [Fact]
    public void Columnstore_sparse_index_attribute_matches_fluent_sql()
    {
        var fluent = MigrationSqlHelper.GenerateSql(SparseFluent);
        var attribute = MigrationSqlHelper.GenerateSql(SparseAttribute);

        Assert.Contains(fluent, s => s.Contains(SparseIndexSetSql));
        Assert.Contains(attribute, s => s.Contains(SparseIndexSetSql));
    }

    // ---------------------------------------------------------------- range (secondary) dimensions

    private const string AddRangeDimensionSql =
        "SELECT add_dimension('\"readings\"', by_range('region', INTERVAL '7 days'));";

    // A hypertable with (optionally) a by_range secondary dimension on the region column.
    private static Action<ModelBuilder> RangeModel(bool withRange, int days = 7) => mb => mb.Entity<Reading>(e =>
    {
        e.HasNoKey();
        e.ToTable("readings");
        e.Property(x => x.Time).HasColumnName("time");
        e.Property(x => x.DeviceId).HasColumnName("device_id");
        e.Property(x => x.Region).HasColumnName("region");
        e.Property(x => x.Value).HasColumnName("value");
        e.IsHypertable(x => x.Time, chunkInterval: TimeSpan.FromDays(1));
        if (withRange)
        {
            e.HasRangePartition(x => x.Region, TimeSpan.FromDays(days));
        }
    });

    [Fact]
    public void Range_dimension_emits_add_dimension_by_range()
    {
        var sql = MigrationSqlHelper.GenerateSql(RangeModel(withRange: true));

        Assert.Contains(sql, s => s.Contains(AddRangeDimensionSql));
    }

    [Fact]
    public void Adding_range_dimension_to_existing_hypertable_is_in_place()
    {
        var sql = MigrationSqlHelper.GenerateSql(RangeModel(withRange: false), RangeModel(withRange: true));

        Assert.Contains(sql, s => s.Contains(AddRangeDimensionSql));
        Assert.DoesNotContain(sql, s => s.Contains("__ts_rebuild"));
    }

    [Fact]
    public void Removing_range_dimension_rebuilds()
    {
        var sql = MigrationSqlHelper.GenerateSql(RangeModel(withRange: true), RangeModel(withRange: false));

        Assert.Contains(sql, s => s.Contains("__ts_rebuild"));
    }

    [Fact]
    public void Changing_range_dimension_interval_rebuilds()
    {
        var sql = MigrationSqlHelper.GenerateSql(RangeModel(withRange: true, days: 7), RangeModel(withRange: true, days: 14));

        Assert.Contains(sql, s => s.Contains("__ts_rebuild"));
    }

    [Fact]
    public void Unchanged_range_dimension_emits_no_dimension_sql()
    {
        var sql = MigrationSqlHelper.GenerateSql(RangeModel(withRange: true), RangeModel(withRange: true));

        Assert.DoesNotContain(sql, s => s.Contains("add_dimension"));
        Assert.DoesNotContain(sql, s => s.Contains("__ts_rebuild"));
    }

    private class RangeReadingAttr
    {
        [PartitionColumn(1, Every.Day)]
        [Column("time")]
        public DateTime Time { get; set; }

        [Column("region")]
        [RangePartition(7, Every.Day)]
        public string Region { get; set; } = null!;

        [Column("value")]
        public double Value { get; set; }
    }

    [Fact]
    public void Range_dimension_attribute_matches_fluent_sql()
    {
        var attribute = MigrationSqlHelper.GenerateSql(mb => mb.Entity<RangeReadingAttr>(e =>
        {
            e.HasNoKey();
            e.ToTable("readings");
        }));

        Assert.Contains(attribute, s => s.Contains(AddRangeDimensionSql));
    }

    [Fact]
    public void No_extension_for_plain_model()
    {
        var sql = MigrationSqlHelper.GenerateSql(mb => mb.Entity<Reading>(e =>
        {
            e.HasNoKey();
            e.ToTable("readings");
        }));

        Assert.DoesNotContain(sql, s => s.Contains("CREATE EXTENSION"));
    }
}
