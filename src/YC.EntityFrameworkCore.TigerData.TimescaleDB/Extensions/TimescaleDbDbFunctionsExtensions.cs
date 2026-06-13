// ReSharper disable once CheckNamespace

namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     TimescaleDB hyperfunctions for LINQ queries via <see cref="EF.Functions" />.
///     These methods are only usable in queries translated to SQL.
/// </summary>
public static class TimescaleDbDbFunctionsExtensions
{
    private const string OnlyForQuerying =
        "This method is for use in LINQ queries only and has no in-memory implementation.";

    // ---------------------------------------------------------------- time_bucket

    /// <summary>Translates to <c>time_bucket(bucket_width, timestamp)</c>.</summary>
    public static DateTime TimeBucket(this DbFunctions _, TimeSpan bucketWidth, DateTime timestamp)
        => throw new InvalidOperationException(OnlyForQuerying);

    /// <inheritdoc cref="TimeBucket(DbFunctions, TimeSpan, DateTime)" />
    public static DateTimeOffset TimeBucket(this DbFunctions _, TimeSpan bucketWidth, DateTimeOffset timestamp)
        => throw new InvalidOperationException(OnlyForQuerying);

    /// <inheritdoc cref="TimeBucket(DbFunctions, TimeSpan, DateTime)" />
    public static DateOnly TimeBucket(this DbFunctions _, TimeSpan bucketWidth, DateOnly date)
        => throw new InvalidOperationException(OnlyForQuerying);

    /// <summary>Translates to <c>time_bucket(bucket_width, timestamp)</c> with a PostgreSQL interval literal.</summary>
    public static DateTime TimeBucket(this DbFunctions _, string bucketWidth, DateTime timestamp)
        => throw new InvalidOperationException(OnlyForQuerying);

    /// <inheritdoc cref="TimeBucket(DbFunctions, string, DateTime)" />
    public static DateTimeOffset TimeBucket(this DbFunctions _, string bucketWidth, DateTimeOffset timestamp)
        => throw new InvalidOperationException(OnlyForQuerying);

    /// <inheritdoc cref="TimeBucket(DbFunctions, string, DateTime)" />
    public static DateOnly TimeBucket(this DbFunctions _, string bucketWidth, DateOnly date)
        => throw new InvalidOperationException(OnlyForQuerying);

    // ---------------------------------------------------------------- time_bucket_gapfill

    /// <summary>Translates to <c>time_bucket_gapfill(bucket_width, timestamp)</c>.</summary>
    public static DateTime TimeBucketGapfill(this DbFunctions _, TimeSpan bucketWidth, DateTime timestamp)
        => throw new InvalidOperationException(OnlyForQuerying);

    /// <inheritdoc cref="TimeBucketGapfill(DbFunctions, TimeSpan, DateTime)" />
    public static DateTimeOffset TimeBucketGapfill(this DbFunctions _, TimeSpan bucketWidth, DateTimeOffset timestamp)
        => throw new InvalidOperationException(OnlyForQuerying);

    /// <inheritdoc cref="TimeBucketGapfill(DbFunctions, TimeSpan, DateTime)" />
    public static DateTime TimeBucketGapfill(this DbFunctions _, string bucketWidth, DateTime timestamp)
        => throw new InvalidOperationException(OnlyForQuerying);

    /// <inheritdoc cref="TimeBucketGapfill(DbFunctions, TimeSpan, DateTime)" />
    public static DateTimeOffset TimeBucketGapfill(this DbFunctions _, string bucketWidth, DateTimeOffset timestamp)
        => throw new InvalidOperationException(OnlyForQuerying);

    // ---------------------------------------------------------------- gapfill companions

    /// <summary>Translates to <c>locf(value)</c>: last observation carried forward.</summary>
    public static T Locf<T>(this DbFunctions _, T value)
        => throw new InvalidOperationException(OnlyForQuerying);

    /// <summary>Translates to <c>interpolate(value)</c>: linear interpolation between points.</summary>
    public static T Interpolate<T>(this DbFunctions _, T value)
        => throw new InvalidOperationException(OnlyForQuerying);

    // ---------------------------------------------------------------- ordered-selection aggregates

    /// <summary>
    ///     Translates to the <c>first(value, time)</c> aggregate. Use with a tuple projection:
    ///     <c>EF.Functions.First(g.Select(x => ValueTuple.Create(x.Value, x.Time)))</c>.
    /// </summary>
    public static TValue First<TValue, TTime>(this DbFunctions _, IEnumerable<(TValue Value, TTime Time)> rows)
        => throw new InvalidOperationException(OnlyForQuerying);

    /// <summary>
    ///     Translates to the <c>last(value, time)</c> aggregate. Use with a tuple projection:
    ///     <c>EF.Functions.Last(g.Select(x => ValueTuple.Create(x.Value, x.Time)))</c>.
    /// </summary>
    public static TValue Last<TValue, TTime>(this DbFunctions _, IEnumerable<(TValue Value, TTime Time)> rows)
        => throw new InvalidOperationException(OnlyForQuerying);

    /// <summary>
    ///     Translates to the <c>histogram(value, min, max, nbuckets)</c> aggregate, returning
    ///     bucket counts with underflow and overflow bins.
    /// </summary>
    public static double[] Histogram(
        this DbFunctions _,
        IEnumerable<double> values,
        double min,
        double max,
        int buckets)
        => throw new InvalidOperationException(OnlyForQuerying);

    // ---------------------------------------------------------------- UUIDv7

    /// <summary>Translates to <c>generate_uuidv7()</c>.</summary>
    public static Guid GenerateUuid7(this DbFunctions _)
        => throw new InvalidOperationException(OnlyForQuerying);

    /// <summary>Translates to <c>to_uuidv7(timestamp)</c>.</summary>
    public static Guid ToUuid7(this DbFunctions _, DateTimeOffset timestamp)
        => throw new InvalidOperationException(OnlyForQuerying);

    /// <summary>Translates to <c>uuid_timestamp(uuid)</c>: extracts the timestamp of a UUIDv7.</summary>
    public static DateTimeOffset UuidTimestamp(this DbFunctions _, Guid uuid)
        => throw new InvalidOperationException(OnlyForQuerying);
}
