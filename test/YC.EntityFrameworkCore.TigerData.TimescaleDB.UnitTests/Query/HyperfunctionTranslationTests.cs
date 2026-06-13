using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Query;

public class HyperfunctionTranslationTests : IDisposable
{
    public class Reading
    {
        public long Id { get; set; }
        public DateTimeOffset Time { get; set; }
        public string DeviceId { get; set; } = null!;
        public double Value { get; set; }
        public Guid TraceId { get; set; }
    }

    private sealed class QueryContext : DbContext
    {
        public DbSet<Reading> Readings => Set<Reading>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseNpgsql("Host=localhost;Database=unit_test_only")
                .UseTimescaleDb();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Reading>(e =>
            {
                e.ToTable("readings");
                e.HasKey(x => new { x.Id, x.Time });
                e.IsHypertable(x => x.Time);
            });
    }

    private readonly QueryContext _context = new();

    public void Dispose() => _context.Dispose();

    [Fact]
    public void TimeBucket_with_timespan_translates()
    {
        var sql = _context.Readings
            .Select(r => EF.Functions.TimeBucket(TimeSpan.FromMinutes(15), r.Time))
            .ToQueryString();

        Assert.Contains("time_bucket(INTERVAL '00:15:00'", sql);
    }

    [Fact]
    public void TimeBucket_with_string_translates_with_interval_cast()
    {
        var sql = _context.Readings
            .Select(r => EF.Functions.TimeBucket("1 hour", r.Time))
            .ToQueryString();

        Assert.Contains("time_bucket('1 hour'::interval", sql);
    }

    [Fact]
    public void TimeBucket_groups_and_aggregates()
    {
        var sql = _context.Readings
            .GroupBy(r => EF.Functions.TimeBucket(TimeSpan.FromHours(1), r.Time))
            .Select(g => new { Bucket = g.Key, Avg = g.Average(x => x.Value) })
            .ToQueryString();

        Assert.Contains("time_bucket(", sql);
        Assert.Contains("GROUP BY", sql);
        Assert.Contains("avg(", sql);
    }

    [Fact]
    public void TimeBucketGapfill_translates()
    {
        var sql = _context.Readings
            .GroupBy(r => EF.Functions.TimeBucketGapfill(TimeSpan.FromHours(1), r.Time))
            .Select(g => new { Bucket = g.Key, Avg = g.Average(x => x.Value) })
            .ToQueryString();

        Assert.Contains("time_bucket_gapfill(", sql);
    }

    [Fact]
    public void First_and_last_aggregates_translate()
    {
        var sql = _context.Readings
            .GroupBy(r => r.DeviceId)
            .Select(g => new
            {
                Device = g.Key,
                First = EF.Functions.First(g.Select(x => ValueTuple.Create(x.Value, x.Time))),
                Last = EF.Functions.Last(g.Select(x => ValueTuple.Create(x.Value, x.Time))),
            })
            .ToQueryString();

        Assert.Contains("""first(r."Value", r."Time")""", sql);
        Assert.Contains("""last(r."Value", r."Time")""", sql);
    }

    [Fact]
    public void Histogram_aggregate_translates()
    {
        var sql = _context.Readings
            .GroupBy(r => r.DeviceId)
            .Select(g => new
            {
                Device = g.Key,
                Distribution = EF.Functions.Histogram(g.Select(x => x.Value), 0.0, 100.0, 10),
            })
            .ToQueryString();

        Assert.Contains("histogram(", sql);
    }

    [Fact]
    public void Uuid7_functions_translate()
    {
        var sql = _context.Readings
            .Select(r => new
            {
                Generated = EF.Functions.GenerateUuid7(),
                Extracted = EF.Functions.UuidTimestamp(r.TraceId),
            })
            .ToQueryString();

        Assert.Contains("generate_uuidv7()", sql);
        Assert.Contains("uuid_timestamp(", sql);
    }
}
