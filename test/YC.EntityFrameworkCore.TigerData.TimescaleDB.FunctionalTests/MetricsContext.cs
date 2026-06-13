using Microsoft.EntityFrameworkCore;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.FunctionalTests;

[Hypertable(ChunkIntervalDays = 1)]
[Columnstore]
[ColumnstorePolicy(AfterDays = 7)]
[RetentionPolicy(DropAfterDays = 90)]
public class Reading
{
    [HypertablePartition]
    [ColumnstoreOrderBy(Descending = true)]
    public DateTimeOffset Time { get; set; }

    [ColumnstoreSegmentBy]
    public string DeviceId { get; set; } = null!;

    public double Value { get; set; }
}

public class HourlyAverage
{
    public DateTimeOffset Bucket { get; set; }
    public string DeviceId { get; set; } = null!;
    public double Average { get; set; }
}

public class MetricsContext(string connectionString) : DbContext
{
    public DbSet<Reading> Readings => Set<Reading>();
    public DbSet<HourlyAverage> HourlyAverages => Set<HourlyAverage>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder
            .UseNpgsql(connectionString)
            .UseTimescaleDb();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Reading>(e =>
        {
            e.HasNoKey();
            e.ToTable("readings");
            e.Property(x => x.Time).HasColumnName("time");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Value).HasColumnName("value");
            e.HasSpacePartition(x => x.DeviceId, partitions: 4);
        });

        modelBuilder.Entity<HourlyAverage>(e =>
        {
            e.HasNoKey();
            e.Property(x => x.Bucket).HasColumnName("bucket");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Average).HasColumnName("average");
            e.IsContinuousAggregate(
                "hourly_averages",
                """
                SELECT time_bucket(INTERVAL '1 hour', time) AS bucket,
                       device_id,
                       avg(value) AS average
                FROM readings
                GROUP BY 1, 2
                """);
            e.HasRefreshPolicy(startOffset: "3 days", endOffset: "1 hour", scheduleInterval: "1 hour");
        });

        modelBuilder.HasTimescaleDbJob(
            "functional_test_job",
            "test_job_proc",
            scheduleInterval: "1 day");
    }
}
