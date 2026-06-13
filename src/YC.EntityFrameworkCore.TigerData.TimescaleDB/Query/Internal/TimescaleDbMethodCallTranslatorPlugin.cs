using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Query.Internal;

/// <summary>Registers the TimescaleDB scalar function translators.</summary>
public class TimescaleDbMethodCallTranslatorPlugin : IMethodCallTranslatorPlugin
{
    public TimescaleDbMethodCallTranslatorPlugin(
        ISqlExpressionFactory sqlExpressionFactory,
        IRelationalTypeMappingSource typeMappingSource)
        => Translators = [new TimescaleDbFunctionsTranslator(sqlExpressionFactory, typeMappingSource)];

    public virtual IEnumerable<IMethodCallTranslator> Translators { get; }
}

/// <summary>
///     Translates the scalar <c>EF.Functions</c> TimescaleDB methods:
///     <c>time_bucket</c>, <c>time_bucket_gapfill</c>, <c>locf</c>, <c>interpolate</c>
///     and the UUIDv7 helpers.
/// </summary>
public class TimescaleDbFunctionsTranslator : IMethodCallTranslator
{
    private static readonly MethodInfo[] TimeBucketMethods = GetMethods(nameof(TimescaleDbDbFunctionsExtensions.TimeBucket));
    private static readonly MethodInfo[] GapfillMethods = GetMethods(nameof(TimescaleDbDbFunctionsExtensions.TimeBucketGapfill));
    private static readonly MethodInfo[] LocfMethods = GetMethods(nameof(TimescaleDbDbFunctionsExtensions.Locf));
    private static readonly MethodInfo[] InterpolateMethods = GetMethods(nameof(TimescaleDbDbFunctionsExtensions.Interpolate));
    private static readonly MethodInfo[] GenerateUuid7Methods = GetMethods(nameof(TimescaleDbDbFunctionsExtensions.GenerateUuid7));
    private static readonly MethodInfo[] ToUuid7Methods = GetMethods(nameof(TimescaleDbDbFunctionsExtensions.ToUuid7));
    private static readonly MethodInfo[] UuidTimestampMethods = GetMethods(nameof(TimescaleDbDbFunctionsExtensions.UuidTimestamp));

    private readonly ISqlExpressionFactory _sqlExpressionFactory;
    private readonly RelationalTypeMapping _intervalTypeMapping;

    public TimescaleDbFunctionsTranslator(
        ISqlExpressionFactory sqlExpressionFactory,
        IRelationalTypeMappingSource typeMappingSource)
    {
        _sqlExpressionFactory = sqlExpressionFactory;
        _intervalTypeMapping = typeMappingSource.FindMapping(typeof(TimeSpan))!;
    }

    public virtual SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        var definition = method.IsGenericMethod ? method.GetGenericMethodDefinition() : method;

        if (TimeBucketMethods.Contains(definition))
        {
            return BucketFunction("time_bucket", method, arguments);
        }

        if (GapfillMethods.Contains(definition))
        {
            return BucketFunction("time_bucket_gapfill", method, arguments);
        }

        if (LocfMethods.Contains(definition))
        {
            return PassthroughFunction("locf", method, arguments);
        }

        if (InterpolateMethods.Contains(definition))
        {
            return PassthroughFunction("interpolate", method, arguments);
        }

        if (GenerateUuid7Methods.Contains(definition))
        {
            return _sqlExpressionFactory.Function(
                "generate_uuidv7", [], nullable: false, [], typeof(Guid));
        }

        if (ToUuid7Methods.Contains(definition))
        {
            return _sqlExpressionFactory.Function(
                "to_uuidv7", [arguments[1]], nullable: true, [true], typeof(Guid));
        }

        if (UuidTimestampMethods.Contains(definition))
        {
            return _sqlExpressionFactory.Function(
                "uuid_timestamp", [arguments[1]], nullable: true, [true], typeof(DateTimeOffset));
        }

        return null;
    }

    /// <summary>time_bucket / time_bucket_gapfill: a width (interval) and a timestamp.</summary>
    private SqlExpression BucketFunction(string name, MethodInfo method, IReadOnlyList<SqlExpression> arguments)
    {
        // String widths become CAST('...' AS interval); TimeSpan widths map to interval natively.
        var width = arguments[1].Type == typeof(string)
            ? _sqlExpressionFactory.Convert(arguments[1], typeof(TimeSpan), _intervalTypeMapping)
            : _sqlExpressionFactory.ApplyTypeMapping(arguments[1], _intervalTypeMapping);

        var timestamp = arguments[2];

        return _sqlExpressionFactory.Function(
            name,
            [width, timestamp],
            nullable: true,
            argumentsPropagateNullability: [true, true],
            method.ReturnType,
            timestamp.TypeMapping);
    }

    /// <summary>locf / interpolate: single value argument, return type follows the argument.</summary>
    private SqlExpression PassthroughFunction(string name, MethodInfo method, IReadOnlyList<SqlExpression> arguments)
        => _sqlExpressionFactory.Function(
            name,
            [arguments[1]],
            nullable: true,
            argumentsPropagateNullability: [true],
            method.ReturnType,
            arguments[1].TypeMapping);

    private static MethodInfo[] GetMethods(string name)
        => typeof(TimescaleDbDbFunctionsExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == name)
            .ToArray();
}
