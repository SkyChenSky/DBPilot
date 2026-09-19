using System.Linq.Expressions;

namespace DBPilot.Common;

/// <summary>
/// 动态条件拼接（移植自 SF.Tookits.ExpressionBuilder，Chloe 查询标准用法）：
/// ExpressionBuilder.Init&lt;T&gt;() 起始恒真条件，再按需 And / Or 追加。
/// </summary>
public static class ExpressionBuilder
{
    /// <summary>默认 True 条件。</summary>
    public static Expression<Func<T, bool>> Init<T>()
        => _ => true;

    public static Expression<Func<T, bool>> And<T>(this Expression<Func<T, bool>> first,
        Expression<Func<T, bool>> second)
        => first.Compose(second, Expression.AndAlso);

    public static Expression<Func<T, bool>> Or<T>(this Expression<Func<T, bool>> first,
        Expression<Func<T, bool>> second)
        => first.Compose(second, Expression.OrElse);

    private static Expression<T> Compose<T>(this Expression<T> first, Expression<T> second,
        Func<Expression, Expression, Expression> merge)
    {
        var map = first.Parameters
            .Select((oldParam, index) => new { oldParam, newParam = second.Parameters[index] })
            .ToDictionary(p => p.newParam, p => p.oldParam);

        var secondBody = ParameterRebinder.ReplaceParameters(map, second.Body);

        return Expression.Lambda<T>(merge(first.Body, secondBody), first.Parameters);
    }
}

internal class ParameterRebinder(Dictionary<ParameterExpression, ParameterExpression> map) : ExpressionVisitor
{
    public static Expression ReplaceParameters(Dictionary<ParameterExpression, ParameterExpression> map,
        Expression expression)
        => new ParameterRebinder(map).Visit(expression);

    protected override Expression VisitParameter(ParameterExpression node)
        => map.TryGetValue(node, out var replacement) ? base.VisitParameter(replacement) : base.VisitParameter(node);
}
