using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Query.Internal;

/// <summary>
///     Prevents the <see cref="TimescaleDbDbFunctionsExtensions" /> methods from being evaluated
///     client-side so they always reach the SQL translators.
/// </summary>
public class TimescaleDbEvaluatableExpressionFilterPlugin : IEvaluatableExpressionFilterPlugin
{
    public virtual bool IsEvaluatableExpression(Expression expression)
        => expression is not MethodCallExpression
        {
            Method.DeclaringType: { } declaringType,
        } || declaringType != typeof(TimescaleDbDbFunctionsExtensions);
}
