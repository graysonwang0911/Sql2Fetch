using Microsoft.SqlServer.TransactSql.ScriptDom;
using Sql2Fetch.Engine.Plan;

namespace Sql2Fetch.Engine.Having;

/// <summary>
/// 把 HAVING 子句翻译成本地谓词。HAVING 里的每个表达式必须能对应到聚合结果的某一列
/// (SELECT 别名、聚合函数、GROUP BY 列);SELECT 里没有的聚合会作为隐藏列补进 FetchXML。
/// </summary>
internal sealed class HavingTranslator
{
    private readonly IList<AggregateColumn> _aggregates;
    private readonly IList<GroupByColumn> _groupBy;
    private readonly IReadOnlyDictionary<string, ScalarExpression> _selectAliases;
    private readonly Func<FunctionCall, string?, AggregateColumn> _buildAggregate;
    private readonly Func<ScalarExpression, GroupByColumn?> _matchGroupBy;

    public List<string> HiddenColumns { get; } = new();

    public HavingTranslator(
        IList<AggregateColumn> aggregates,
        IList<GroupByColumn> groupBy,
        IReadOnlyDictionary<string, ScalarExpression> selectAliases,
        Func<FunctionCall, string?, AggregateColumn> buildAggregate,
        Func<ScalarExpression, GroupByColumn?> matchGroupBy)
    {
        _aggregates = aggregates;
        _groupBy = groupBy;
        _selectAliases = selectAliases;
        _buildAggregate = buildAggregate;
        _matchGroupBy = matchGroupBy;
    }

    public RowPredicate Translate(BooleanExpression expr)
    {
        switch (expr)
        {
            case BooleanParenthesisExpression paren:
                return Translate(paren.Expression);

            case BooleanBinaryExpression binary:
            {
                var a = Translate(binary.FirstExpression);
                var b = Translate(binary.SecondExpression);
                if (binary.BinaryExpressionType == BooleanBinaryExpressionType.And)
                {
                    var and = new AndPredicate();
                    Flatten(and.Items, a, typeof(AndPredicate));
                    Flatten(and.Items, b, typeof(AndPredicate));
                    return and;
                }
                var or = new OrPredicate();
                Flatten(or.Items, a, typeof(OrPredicate));
                Flatten(or.Items, b, typeof(OrPredicate));
                return or;
            }

            case BooleanNotExpression not:
                return new NotPredicate { Inner = Translate(not.Expression) };

            case BooleanComparisonExpression cmp:
            {
                var (left, right, type) = (cmp.FirstExpression, cmp.SecondExpression, cmp.ComparisonType);
                if (!IsColumnLike(left) && IsColumnLike(right))
                {
                    (left, right) = (right, left);
                    type = Flip(type);
                }
                var column = ResolveColumn(left);
                var value = CriteriaTranslator.LiteralValue(right);
                return new ComparePredicate { Column = column, Operator = Op(type, cmp), Value = value };
            }

            case BooleanIsNullExpression isNull:
                return new IsNullPredicate { Column = ResolveColumn(isNull.Expression), Not = isNull.IsNot };

            case Microsoft.SqlServer.TransactSql.ScriptDom.InPredicate inPred:
            {
                if (inPred.Subquery is not null)
                    throw SqlTranslationException.NotSupported(inPred, "HAVING 中的子查询");
                return new InPredicate
                {
                    Column = ResolveColumn(inPred.Expression),
                    Values = inPred.Values.Select(CriteriaTranslator.LiteralValue).ToList(),
                    Not = inPred.NotDefined,
                };
            }

            case BooleanTernaryExpression between:
                return new BetweenPredicate
                {
                    Column = ResolveColumn(between.FirstExpression),
                    Low = CriteriaTranslator.LiteralValue(between.SecondExpression),
                    High = CriteriaTranslator.LiteralValue(between.ThirdExpression),
                    Not = between.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween,
                };

            default:
                throw SqlTranslationException.NotSupported(expr, "HAVING 中的 " + expr.GetType().Name);
        }
    }

    private static void Flatten(List<RowPredicate> target, RowPredicate p, Type sameType)
    {
        if (p.GetType() == sameType)
            target.AddRange(p is AndPredicate a ? a.Items : ((OrPredicate)p).Items);
        else
            target.Add(p);
    }

    private static bool IsColumnLike(ScalarExpression e) => e is ColumnReferenceExpression or FunctionCall;

    /// <summary>HAVING 里的表达式 → 聚合结果中的列别名。</summary>
    private string ResolveColumn(ScalarExpression expr)
    {
        while (expr is ParenthesisExpression p) expr = p.Expression;

        if (expr is ColumnReferenceExpression col)
        {
            var name = col.MultiPartIdentifier.Identifiers[^1].Value;

            // SELECT 别名
            if (col.MultiPartIdentifier.Identifiers.Count == 1 && _selectAliases.TryGetValue(name, out var target))
            {
                if (target is FunctionCall f && _aggregates.FirstOrDefault(a => a.Origin == f) is { } agg) return agg.Alias;
                var gbAlias = _groupBy.FirstOrDefault(g => g.Alias.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (gbAlias is not null) return gbAlias.Alias;
                var gb = _matchGroupBy(target);
                if (gb is not null) return gb.Alias;
            }

            // 聚合列的别名直接写出来(如 cnt)
            var byAlias = _aggregates.FirstOrDefault(a => a.Alias.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (byAlias is not null) return byAlias.Alias;

            // GROUP BY 的列
            var grouped = _matchGroupBy(col);
            if (grouped is not null) return grouped.Alias;

            throw new SqlTranslationException($"HAVING 中的列 \"{name}\" 必须是 GROUP BY 的列、聚合函数或 SELECT 中的别名", col);
        }

        if (expr is FunctionCall func)
        {
            // 日期分组函数:YEAR(createdon) 等
            var gb = _matchGroupBy(func);
            if (gb is not null) return gb.Alias;

            // 聚合函数:找 SELECT 里相同的;没有就补一个隐藏列
            if (func.FunctionName.Value.ToLowerInvariant() is not ("count" or "sum" or "avg" or "min" or "max"))
                throw SqlTranslationException.NotSupported(func, $"HAVING 中的函数 {func.FunctionName.Value}()",
                    "只能是 COUNT/SUM/AVG/MIN/MAX,或与 GROUP BY 相同的 YEAR()/MONTH()/DATEPART()");
            var wanted = _buildAggregate(func, null);
            var existing = _aggregates.FirstOrDefault(a =>
                a.Function == wanted.Function && a.Distinct == wanted.Distinct && SameColumn(a.Column, wanted.Column));
            if (existing is not null) return existing.Alias;

            var hidden = wanted with { Alias = $"having_{HiddenColumns.Count + 1}" };
            _aggregates.Add(hidden);
            HiddenColumns.Add(hidden.Alias);
            return hidden.Alias;
        }

        throw SqlTranslationException.NotSupported(expr, "HAVING 中的表达式", "只能是聚合函数、GROUP BY 列或 SELECT 别名");
    }

    private static bool SameColumn(ColumnReferenceExpression? a, ColumnReferenceExpression? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.MultiPartIdentifier.Identifiers[^1].Value.Equals(b.MultiPartIdentifier.Identifiers[^1].Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string Op(BooleanComparisonType type, TSqlFragment origin) => type switch
    {
        BooleanComparisonType.Equals => "eq",
        BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => "ne",
        BooleanComparisonType.LessThan => "lt",
        BooleanComparisonType.LessThanOrEqualTo or BooleanComparisonType.NotGreaterThan => "le",
        BooleanComparisonType.GreaterThan => "gt",
        BooleanComparisonType.GreaterThanOrEqualTo or BooleanComparisonType.NotLessThan => "ge",
        _ => throw SqlTranslationException.NotSupported(origin, $"比较运算符 {type}"),
    };

    private static BooleanComparisonType Flip(BooleanComparisonType t) => t switch
    {
        BooleanComparisonType.LessThan => BooleanComparisonType.GreaterThan,
        BooleanComparisonType.GreaterThan => BooleanComparisonType.LessThan,
        BooleanComparisonType.LessThanOrEqualTo => BooleanComparisonType.GreaterThanOrEqualTo,
        BooleanComparisonType.GreaterThanOrEqualTo => BooleanComparisonType.LessThanOrEqualTo,
        _ => t,
    };
}
