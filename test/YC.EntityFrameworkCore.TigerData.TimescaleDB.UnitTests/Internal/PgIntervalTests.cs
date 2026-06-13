using YC.EntityFrameworkCore.TigerData.TimescaleDB.Internal;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.UnitTests.Internal;

public class PgIntervalTests
{
    [Theory]
    [InlineData(7, 0, 0, 0, "7 days")]
    [InlineData(1, 0, 0, 0, "1 day")]
    [InlineData(90, 0, 0, 0, "90 days")]
    [InlineData(0, 1, 30, 0, "01:30:00")]
    [InlineData(0, 0, 0, 30, "00:00:30")]
    [InlineData(2, 4, 0, 0, "2 days 04:00:00")]
    [InlineData(0, 0, 0, 0, "00:00:00")]
    public void Formats_timespans_as_pg_intervals(int days, int hours, int minutes, int seconds, string expected)
        => Assert.Equal(expected, PgInterval.Format(new TimeSpan(days, hours, minutes, seconds)));

    [Fact]
    public void Includes_subsecond_precision_when_present()
        => Assert.Equal("00:00:00.5", PgInterval.Format(TimeSpan.FromMilliseconds(500)));

    [Fact]
    public void Rejects_negative_intervals()
        => Assert.Throws<ArgumentOutOfRangeException>(() => PgInterval.Format(TimeSpan.FromHours(-1)));
}
