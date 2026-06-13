using System.Linq.Expressions;

namespace YC.EntityFrameworkCore.TigerData.TimescaleDB.Internal;

public static class ExpressionHelpers
{
    /// <summary>Extracts the property name from a simple member-access lambda.</summary>
    public static string GetPropertyName<TEntity>(Expression<Func<TEntity, object?>> expression)
    {
        var body = expression.Body is UnaryExpression { NodeType: ExpressionType.Convert } unary
            ? unary.Operand
            : expression.Body;

        return body is MemberExpression member
            ? member.Member.Name
            : throw new ArgumentException(
                $"Expression '{expression}' must be a simple property access.", nameof(expression));
    }

    /// <summary>
    ///     Extracts property names from a lambda selecting either a single property or an
    ///     anonymous type of properties, e.g. <c>x => new { x.A, x.B }</c>.
    /// </summary>
    public static IReadOnlyList<string> GetPropertyNames<TEntity>(Expression<Func<TEntity, object?>> expression)
    {
        if (expression.Body is NewExpression newExpression)
        {
            return newExpression.Arguments
                .Select(argument => argument is MemberExpression member
                    ? member.Member.Name
                    : throw new ArgumentException(
                        $"Expression '{expression}' must select properties only.", nameof(expression)))
                .ToList();
        }

        return [GetPropertyName(expression)];
    }
}
