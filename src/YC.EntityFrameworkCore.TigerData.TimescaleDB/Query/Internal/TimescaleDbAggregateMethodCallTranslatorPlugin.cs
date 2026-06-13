using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Query.Internal;

/// <summary>Registers the TimescaleDB aggregate function translators.</summary>
public class TimescaleDbAggregateMethodCallTranslatorPlugin : IAggregateMethodCallTranslatorPlugin
{
    public TimescaleDbAggregateMethodCallTranslatorPlugin(
        ISqlExpressionFactory sqlExpressionFactory,
        IRelationalTypeMappingSource typeMappingSource)
        => Translators =
        [
            new TimescaleDbAggregateFunctionsTranslator(
                (NpgsqlSqlExpressionFactory)sqlExpressionFactory, typeMappingSource),
        ];

    public virtual IEnumerable<IAggregateMethodCallTranslator> Translators { get; }
}

/// <summary>
///     Translates <c>EF.Functions.First/Last</c> (ordered-selection aggregates over a
///     <c>(value, time)</c> tuple projection) and <c>EF.Functions.Histogram</c>.
/// </summary>
public class TimescaleDbAggregateFunctionsTranslator : IAggregateMethodCallTranslator
{
    private static readonly MethodInfo FirstMethod = GetMethod(nameof(TimescaleDbDbFunctionsExtensions.First));
    private static readonly MethodInfo LastMethod = GetMethod(nameof(TimescaleDbDbFunctionsExtensions.Last));
    private static readonly MethodInfo HistogramMethod = GetMethod(nameof(TimescaleDbDbFunctionsExtensions.Histogram));

    private readonly NpgsqlSqlExpressionFactory _sqlExpressionFactory;
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    public TimescaleDbAggregateFunctionsTranslator(
        NpgsqlSqlExpressionFactory sqlExpressionFactory,
        IRelationalTypeMappingSource typeMappingSource)
    {
        _sqlExpressionFactory = sqlExpressionFactory;
        _typeMappingSource = typeMappingSource;
    }

    public virtual SqlExpression? Translate(
        MethodInfo method,
        EnumerableExpression source,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        var definition = method.IsGenericMethod ? method.GetGenericMethodDefinition() : method;

        if (definition == FirstMethod || definition == LastMethod)
        {
            // The selector is a (value, time) tuple, surfaced as a PostgreSQL row value.
            if (source.Selector is not PgRowValueExpression rowValue || rowValue.Values.Count != 2)
            {
                return null;
            }

            var value = rowValue.Values[0];
            var time = rowValue.Values[1];

            return _sqlExpressionFactory.AggregateFunction(
                definition == FirstMethod ? "first" : "last",
                [value, time],
                source,
                nullable: true,
                argumentsPropagateNullability: [false, false],
                method.ReturnType,
                value.TypeMapping);
        }

        if (definition == HistogramMethod)
        {
            if (source.Selector is not SqlExpression value)
            {
                return null;
            }

            // arguments[0] is the EF.Functions instance; the scalars follow it.
            return _sqlExpressionFactory.AggregateFunction(
                "histogram",
                [value, arguments[1], arguments[2], arguments[3]],
                source,
                nullable: true,
                argumentsPropagateNullability: [false, false, false, false],
                typeof(double[]),
                _typeMappingSource.FindMapping(typeof(double[])));
        }

        return null;
    }

    private static MethodInfo GetMethod(string name)
        => typeof(TimescaleDbDbFunctionsExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == name);
}
