using Microsoft.SqlServer.TransactSql.ScriptDom;
using Sql2Fetch.Engine.FetchXml;

namespace Sql2Fetch.Engine.Plan;

/// <summary>
/// 把 ScriptDom 的布尔表达式树翻译成 FetchXML 的 filter/condition。
/// 对应 Sql4Cds 的 BaseDataNode.TranslateFetchXMLCriteria。
/// 翻译不了的片段直接抛 SqlTranslationException(Sql4Cds 会退回内存执行,我们不做)。
/// </summary>
internal static class CriteriaTranslator
{
    /// <summary>FetchXML 支持的全部 operator,取自官方 xsd。</summary>
    private static readonly HashSet<string> KnownOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "above","begins-with","between","contain-values","ends-with","eq","eq-businessid","eq-or-above","eq-or-under",
        "eq-userid","eq-userlanguage","eq-useroruserhierarchy","eq-useroruserhierarchyandteams","eq-useroruserteams",
        "eq-userteams","ge","gt","in","in-fiscal-period","in-fiscal-period-and-year","in-fiscal-year",
        "in-or-after-fiscal-period-and-year","in-or-before-fiscal-period-and-year","last-fiscal-period","last-fiscal-year",
        "last-month","last-seven-days","last-week","last-x-days","last-x-fiscal-periods","last-x-fiscal-years","last-x-hours",
        "last-x-months","last-x-weeks","last-x-years","last-year","le","like","lt","ne","ne-businessid","ne-userid","neq",
        "next-fiscal-period","next-fiscal-year","next-month","next-seven-days","next-week","next-x-days","next-x-fiscal-periods",
        "next-x-fiscal-years","next-x-hours","next-x-months","next-x-weeks","next-x-years","next-year","not-begin-with",
        "not-between","not-contain-values","not-end-with","not-in","not-like","not-null","not-under","null","olderthan-x-days",
        "olderthan-x-hours","olderthan-x-minutes","olderthan-x-months","olderthan-x-weeks","olderthan-x-years","on",
        "on-or-after","on-or-before","this-fiscal-period","this-fiscal-year","this-month","this-week","this-year","today",
        "tomorrow","under","yesterday",
    };

    /// <summary>去掉连字符/下划线后的名字 → 正式 operator。让 lastxdays / last_x_days / last-x-days 都能对上。</summary>
    private static readonly Dictionary<string, string> OperatorLookup = KnownOperators
        .ToDictionary(op => op.Replace("-", ""), op => op, StringComparer.OrdinalIgnoreCase);

    /// <summary>翻译结果:要么是单个条件,要么是一组。</summary>
    public static object Translate(BooleanExpression criteria, NodeSchema schema, FoldContext ctx)
    {
        switch (criteria)
        {
            case BooleanParenthesisExpression paren:
                return Translate(paren.Expression, schema, ctx);

            case BooleanBinaryExpression binary:
            {
                var type = binary.BinaryExpressionType == BooleanBinaryExpressionType.And ? FetchFilterType.And : FetchFilterType.Or;
                var filter = new FetchFilter { Type = type };
                Append(filter, Translate(binary.FirstExpression, schema, ctx), type);
                Append(filter, Translate(binary.SecondExpression, schema, ctx), type);
                return filter;
            }

            case BooleanNotExpression not:
                return Negate(Translate(not.Expression, schema, ctx), not);

            case BooleanComparisonExpression comparison:
                return TranslateComparison(comparison, schema, ctx);

            case InPredicate inPred:
            {
                if (inPred.Subquery is not null)
                    throw SqlTranslationException.NotSupported(inPred, "IN (子查询)", "FetchXML 不支持子查询,请改写为 JOIN");
                var col = RequireColumn(inPred.Expression, schema);
                var condition = NewCondition(col, inPred.NotDefined ? "not-in" : "in");
                foreach (var v in inPred.Values)
                    condition.Values.Add(LiteralValue(v));
                return condition;
            }

            case BooleanIsNullExpression isNull:
            {
                var col = RequireColumn(isNull.Expression, schema);
                return NewCondition(col, isNull.IsNot ? "not-null" : "null");
            }

            case LikePredicate like:
            {
                if (like.EscapeExpression is not null)
                    throw SqlTranslationException.NotSupported(like.EscapeExpression, "LIKE ... ESCAPE");
                var col = RequireColumn(like.FirstExpression, schema);
                var value = LiteralValue(like.SecondExpression);
                var condition = NewCondition(col, like.NotDefined ? "not-like" : "like");
                condition.Value = value;
                return condition;
            }

            case BooleanTernaryExpression ternary:
            {
                var col = RequireColumn(ternary.FirstExpression, schema);
                var op = ternary.TernaryExpressionType == BooleanTernaryExpressionType.Between ? "between" : "not-between";
                var condition = NewCondition(col, op);
                condition.Values.Add(LiteralValue(ternary.SecondExpression));
                condition.Values.Add(LiteralValue(ternary.ThirdExpression));
                return condition;
            }

            case ExistsPredicate exists:
                throw SqlTranslationException.NotSupported(exists, "EXISTS 子查询", "请改写为 INNER JOIN");

            default:
                throw SqlTranslationException.NotSupported(criteria, criteria.GetType().Name);
        }
    }

    private static void Append(FetchFilter target, object translated, FetchFilterType type)
    {
        // 同类型的嵌套直接展平:a AND (b AND c) → and[a,b,c]
        if (translated is FetchFilter f && f.Type == type)
            target.Items.AddRange(f.Items);
        else
            target.Items.Add(translated);
    }

    private static object Negate(object translated, TSqlFragment origin)
    {
        if (translated is FetchCondition c)
        {
            c.Operator = c.Operator switch
            {
                "eq" => "ne", "ne" => "eq", "neq" => "eq",
                "lt" => "ge", "ge" => "lt", "gt" => "le", "le" => "gt",
                "like" => "not-like", "not-like" => "like",
                "in" => "not-in", "not-in" => "in",
                "null" => "not-null", "not-null" => "null",
                "between" => "not-between", "not-between" => "between",
                "under" => "not-under", "not-under" => "under",
                "begins-with" => "not-begin-with", "not-begin-with" => "begins-with",
                "ends-with" => "not-end-with", "not-end-with" => "ends-with",
                "contain-values" => "not-contain-values", "not-contain-values" => "contain-values",
                "eq-userid" => "ne-userid", "ne-userid" => "eq-userid",
                "eq-businessid" => "ne-businessid", "ne-businessid" => "eq-businessid",
                _ => throw SqlTranslationException.NotSupported(origin, $"NOT 作用于 {c.Operator}")
            };
            return c;
        }

        if (translated is FetchFilter f)
        {
            // De Morgan: NOT (a AND b) → (NOT a) OR (NOT b)
            var negated = new FetchFilter { Type = f.Type == FetchFilterType.And ? FetchFilterType.Or : FetchFilterType.And };
            foreach (var item in f.Items)
                negated.Items.Add(Negate(item, origin));
            return negated;
        }

        throw SqlTranslationException.NotSupported(origin, "NOT");
    }

    private static object TranslateComparison(BooleanComparisonExpression comparison, NodeSchema schema, FoldContext ctx)
    {
        var left = comparison.FirstExpression;
        var right = comparison.SecondExpression;
        var type = comparison.ComparisonType;

        // 两边都是列:列与列比较 → valueof
        if (left is ColumnReferenceExpression lc && right is ColumnReferenceExpression rc)
        {
            var c1 = schema.Resolve(lc);
            var c2 = schema.Resolve(rc);
            // "x" 按 SQL 规则是列名;没有元数据校验时会悄悄变成列比较,这里提醒一下
            foreach (var quoted in new[] { lc, rc }.Select(DoubleQuotedName).OfType<string>())
                ctx.Warn($"双引号里的 \"{quoted}\" 被当作列名而不是字符串;如果想写字符串,请用单引号 '{quoted}'");
            var condition = NewCondition(c1, ComparisonOperator(type, comparison));
            condition.ValueOf = c1.Alias.Equals(c2.Alias, StringComparison.OrdinalIgnoreCase) ? c2.Attribute : $"{c2.Alias}.{c2.Attribute}";
            if (!c1.Alias.Equals(c2.Alias, StringComparison.OrdinalIgnoreCase))
                ctx.Warn("跨表的列比较需要 Dataverse 2022 以后的版本支持(valueof=\"别名.列\")");
            return condition;
        }

        // 把列换到左边,操作符相应翻转
        if (left is not ColumnReferenceExpression)
        {
            (left, right) = (right, left);
            type = type switch
            {
                BooleanComparisonType.LessThan => BooleanComparisonType.GreaterThan,
                BooleanComparisonType.GreaterThan => BooleanComparisonType.LessThan,
                BooleanComparisonType.LessThanOrEqualTo => BooleanComparisonType.GreaterThanOrEqualTo,
                BooleanComparisonType.GreaterThanOrEqualTo => BooleanComparisonType.LessThanOrEqualTo,
                BooleanComparisonType.NotLessThan => BooleanComparisonType.NotGreaterThan,
                BooleanComparisonType.NotGreaterThan => BooleanComparisonType.NotLessThan,
                _ => type
            };
        }

        if (IsConstant(left) && IsConstant(right))
            throw SqlTranslationException.NotSupported(comparison, "两边都是常量的条件(如 1 = 1)",
                "FetchXML 的条件必须有一边是列,这类恒真 / 恒假的条件请直接去掉");

        var col = RequireColumn(left, schema);

        // 右边是函数:FetchXML 特殊操作符,如 createdon = lastxdays(7)、ownerid = eq_userid()
        if (right is FunctionCall func)
            return TranslateOperatorFunction(col, func, type, comparison);

        if (right is NullLiteral)
            throw new SqlTranslationException("不能用 = NULL 或 <> NULL 比较", comparison, "请使用 IS NULL / IS NOT NULL");

        var cond = NewCondition(col, ComparisonOperator(type, comparison));
        cond.Value = LiteralValue(right);
        return cond;
    }

    private static FetchCondition TranslateOperatorFunction(ResolvedColumn col, FunctionCall func, BooleanComparisonType type, TSqlFragment origin)
    {
        var normalized = func.FunctionName.Value.Replace("_", "").Replace("-", "");

        if (!OperatorLookup.TryGetValue(normalized, out var op))
            throw new SqlTranslationException($"未知的 FetchXML 操作符函数 {func.FunctionName.Value}()", func,
                "例如 lastxdays(7)、thismonth()、eq_userid()、under('guid')");

        if (type == BooleanComparisonType.NotEqualToBrackets || type == BooleanComparisonType.NotEqualToExclamation)
        {
            op = op switch
            {
                "eq-userid" => "ne-userid",
                "eq-businessid" => "ne-businessid",
                _ => throw new SqlTranslationException($"{func.FunctionName.Value}() 不能和 <> 一起使用", origin)
            };
        }
        else if (type != BooleanComparisonType.Equals)
        {
            throw new SqlTranslationException($"{func.FunctionName.Value}() 只能和 = 一起使用", origin);
        }

        var condition = NewCondition(col, op);
        if (func.Parameters.Count == 1)
            condition.Value = LiteralValue(func.Parameters[0]);
        else if (func.Parameters.Count > 1)
            foreach (var p in func.Parameters) condition.Values.Add(LiteralValue(p));
        return condition;
    }

    private static string ComparisonOperator(BooleanComparisonType type, TSqlFragment origin) => type switch
    {
        BooleanComparisonType.Equals => "eq",
        BooleanComparisonType.NotEqualToBrackets => "ne",
        BooleanComparisonType.NotEqualToExclamation => "ne",
        BooleanComparisonType.LessThan => "lt",
        BooleanComparisonType.LessThanOrEqualTo => "le",
        BooleanComparisonType.NotGreaterThan => "le",
        BooleanComparisonType.GreaterThan => "gt",
        BooleanComparisonType.GreaterThanOrEqualTo => "ge",
        BooleanComparisonType.NotLessThan => "ge",
        _ => throw SqlTranslationException.NotSupported(origin, $"比较运算符 {type}")
    };

    private static ResolvedColumn RequireColumn(ScalarExpression expression, NodeSchema schema)
    {
        if (expression is ColumnReferenceExpression col)
            return schema.Resolve(col);
        throw SqlTranslationException.NotSupported(expression, "对列做运算或函数后再比较",
            "FetchXML 只能直接比较列的值,例如 WHERE name = 'x'");
    }

    private static bool IsConstant(ScalarExpression expression) => expression switch
    {
        ParenthesisExpression paren => IsConstant(paren.Expression),
        UnaryExpression unary => IsConstant(unary.Expression),
        Literal => true,
        _ => false,
    };

    /// <summary>列引用是用双引号括起来的单段名字时返回这个名字,否则返回 null。</summary>
    private static string? DoubleQuotedName(ColumnReferenceExpression column) =>
        column.MultiPartIdentifier?.Identifiers is { Count: 1 } ids && ids[0].QuoteType == QuoteType.DoubleQuote ? ids[0].Value : null;

    private static FetchCondition NewCondition(ResolvedColumn col, string op) =>
        new() { Attribute = col.Attribute, Operator = op, EntityName = col.Alias };

    /// <summary>字面量 → FetchXML 的 value 文本。</summary>
    public static string LiteralValue(ScalarExpression expression)
    {
        switch (expression)
        {
            case ParenthesisExpression paren:
                return LiteralValue(paren.Expression);
            case NullLiteral:
                throw new SqlTranslationException("此处不能使用 NULL", expression, "请使用 IS NULL / IS NOT NULL");
            case Literal literal:
                return literal.Value;
            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } neg:
                return "-" + LiteralValue(neg.Expression);
            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } pos:
                return LiteralValue(pos.Expression);
            case VariableReference variable:
                throw SqlTranslationException.NotSupported(variable, "变量", "请直接写字面量");
            case ColumnReferenceExpression column:
                throw SqlTranslationException.NotSupported(column, "这里的值不能是列",
                    DoubleQuotedName(column) is { } quoted
                        ? $"双引号里的 \"{quoted}\" 是列名;字符串请用单引号 '{quoted}'"
                        : "IN / LIKE / BETWEEN 和函数参数的值只能是常量;列与列之间只能用 = <> > >= < <= 比较");
            default:
                throw SqlTranslationException.NotSupported(expression, "表达式", "FetchXML 的 value 只能是字面量");
        }
    }
}
