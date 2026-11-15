using Microsoft.SqlServer.TransactSql.ScriptDom;
using Sql2Fetch.Engine.Metadata;
using Sql2Fetch.Engine.Plan;

namespace Sql2Fetch.Engine;

/// <summary>
/// ScriptDom 语法树 → 逻辑计划树。对应 Sql4Cds 的 ExecutionPlanBuilder.ConvertSelectQuerySpec。
/// 处理顺序和 SQL 的逻辑执行顺序一致:FROM → WHERE → GROUP BY → DISTINCT → SELECT → ORDER BY → TOP/OFFSET。
/// </summary>
internal sealed class PlanBuilder
{
    /// <summary>FetchXML 的 top 属性只接受 1 到 5000。</summary>
    internal const int MaxFetchTop = 5000;

    private static readonly HashSet<string> AggregateFunctions = new(StringComparer.OrdinalIgnoreCase) { "count", "sum", "avg", "min", "max" };

    private readonly IMetadataProvider _metadata;
    private readonly FoldContext _ctx;

    public PlanBuilder(IMetadataProvider metadata, FoldContext ctx)
    {
        _metadata = metadata;
        _ctx = ctx;
    }

    /// <summary>HAVING 翻译出的本地谓词;没有 HAVING 时为 null。</summary>
    public Having.RowPredicate? HavingPredicate { get; private set; }
    public string? HavingText { get; private set; }
    /// <summary>为 HAVING / ORDER BY 补进 FetchXML、但不该出现在结果里的列别名。</summary>
    public List<string> HiddenColumns { get; } = new();

    /// <summary>
    /// 有 HAVING 时,TOP / OFFSET-FETCH 不能交给服务器(那样会先截断再过滤,结果是错的),
    /// 改为本地过滤之后再跳过 LocalOffset 行、取 LocalTop 行。
    /// TOP 0 和超过 FetchXML 上限的 TOP 也走 LocalTop。
    /// </summary>
    public int? LocalTop { get; private set; }
    public int LocalOffset { get; private set; }

    private static string FragmentText(TSqlFragment fragment)
    {
        if (fragment.ScriptTokenStream is null || fragment.FirstTokenIndex < 0) return fragment.ToString() ?? "";
        return string.Concat(fragment.ScriptTokenStream
            .Skip(fragment.FirstTokenIndex)
            .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
            .Select(t => t.Text)).Trim();
    }

    public SelectNode Build(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var errors);

        if (errors.Count > 0)
            throw new SqlTranslationException(errors[0]);

        var script = (TSqlScript)fragment;
        var statements = script.Batches.SelectMany(b => b.Statements).ToList();

        if (statements.Count == 0)
            throw new SqlTranslationException("没有可翻译的语句");
        if (statements.Count > 1)
            throw new SqlTranslationException("一次只能翻译一条 SELECT 语句", statements[1]);

        if (statements[0] is not SelectStatement select)
            throw SqlTranslationException.NotSupported(statements[0], statements[0].GetType().Name.Replace("Statement", ""),
                "FetchXML 只能表达查询,请使用 SELECT");

        if (select.WithCtesAndXmlNamespaces is not null)
            throw SqlTranslationException.NotSupported(select.WithCtesAndXmlNamespaces, "WITH (CTE)");

        return BuildQuery(select.QueryExpression);
    }

    private SelectNode BuildQuery(QueryExpression query)
    {
        switch (query)
        {
            case QueryParenthesisExpression paren:
                return BuildQuery(paren.QueryExpression);
            case BinaryQueryExpression binary:
                throw SqlTranslationException.NotSupported(binary, binary.BinaryQueryExpressionType.ToString().ToUpperInvariant());
            case QuerySpecification spec:
                return BuildQuerySpec(spec);
            default:
                throw SqlTranslationException.NotSupported(query, query.GetType().Name);
        }
    }

    private SelectNode BuildQuerySpec(QuerySpecification spec)
    {
        // FROM
        if (spec.FromClause is null || spec.FromClause.TableReferences.Count == 0)
            throw new SqlTranslationException("缺少 FROM 子句", spec);
        if (spec.FromClause.TableReferences.Count > 1)
            throw SqlTranslationException.NotSupported(spec.FromClause, "逗号分隔的多表(CROSS JOIN)", "请使用 JOIN ... ON");

        PlanNode node = BuildTableReference(spec.FromClause.TableReferences[0]);

        // WHERE
        if (spec.WhereClause is not null)
            node = new FilterNode { Source = node, Filter = spec.WhereClause.SearchCondition, Origin = spec.WhereClause };

        // SELECT 列 + 聚合
        var selectColumns = new List<SelectColumn>();
        var aggregates = new List<AggregateColumn>();
        var selectAliases = new Dictionary<string, ScalarExpression>(StringComparer.OrdinalIgnoreCase);
        var hasAggregate = false;

        foreach (var element in spec.SelectElements)
        {
            switch (element)
            {
                case SelectStarExpression star:
                {
                    var col = new ColumnReferenceExpression { ColumnType = ColumnType.Wildcard, MultiPartIdentifier = star.Qualifier };
                    CopyPosition(star, col);
                    selectColumns.Add(new SelectColumn("*", col, null));
                    break;
                }
                case SelectScalarExpression scalar:
                {
                    var alias = scalar.ColumnName?.Value;
                    var expr = scalar.Expression;

                    // COUNT(*) OVER () 不拦的话会被当成普通聚合,报出不相干的 GROUP BY 错误
                    if (expr is FunctionCall { OverClause: not null })
                        throw SqlTranslationException.NotSupported(expr, "窗口函数 (OVER)", "FetchXML 没有窗口函数,请取回数据后自己计算");

                    if (expr is FunctionCall func && AggregateFunctions.Contains(func.FunctionName.Value))
                    {
                        hasAggregate = true;
                        var agg = EnsureUniqueAlias(BuildAggregate(func, alias, aggregates.Count), alias is not null,
                            aggregates.Select(a => a.Alias), func);
                        aggregates.Add(agg);
                        selectColumns.Add(new SelectColumn(agg.Alias, expr, alias));
                        selectAliases[agg.Alias] = expr;
                    }
                    else if (expr is ColumnReferenceExpression col)
                    {
                        var name = alias ?? col.MultiPartIdentifier.Identifiers[^1].Value;
                        selectColumns.Add(new SelectColumn(name, col, alias));
                        if (alias is not null) selectAliases[alias] = col;
                    }
                    else if (expr is FunctionCall dateFunc && TryGetDateGrouping(dateFunc, out _, out _))
                    {
                        hasAggregate = true;
                        var name = alias ?? dateFunc.FunctionName.Value.ToLowerInvariant();
                        selectColumns.Add(new SelectColumn(name, expr, alias));
                        selectAliases[name] = expr;
                    }
                    else
                    {
                        throw SqlTranslationException.NotSupported(expr, "SELECT 中的表达式",
                            "FetchXML 只能返回列本身或聚合(count/sum/avg/min/max)");
                    }
                    break;
                }
                default:
                    throw SqlTranslationException.NotSupported(element, element.GetType().Name);
            }
        }

        // GROUP BY
        var groupBy = new List<GroupByColumn>();
        var isAggregateQuery = spec.GroupByClause is not null || hasAggregate;
        if (isAggregateQuery)
        {
            // 聚合查询里每个输出列都要有唯一的 alias。两张表的同名列(a.name, c.name)默认都叫 name,这里错开
            var usedNames = new HashSet<string>(aggregates.Select(a => a.Alias), StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < selectColumns.Count; i++)
            {
                var sc = selectColumns[i];
                if (sc.Expression is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard })
                    throw new SqlTranslationException("聚合查询不能使用 *", spec, "请写出 GROUP BY 的列和聚合函数");
                if (sc.Expression is FunctionCall f && AggregateFunctions.Contains(f.FunctionName.Value)) continue;
                if (usedNames.Add(sc.OutputName)) continue;
                if (sc.ExplicitAlias is not null)
                    throw new SqlTranslationException($"列别名 \"{sc.OutputName}\" 重复", sc.Expression, "每个输出列的别名必须唯一");
                var unique = Enumerable.Range(2, int.MaxValue - 2).Select(n => $"{sc.OutputName}_{n}").First(usedNames.Add);
                selectColumns[i] = sc with { OutputName = unique };
                selectAliases[unique] = sc.Expression;
            }

            var groupExprs = spec.GroupByClause?.GroupingSpecifications
                .Select(g => g is ExpressionGroupingSpecification e ? e.Expression
                    : throw SqlTranslationException.NotSupported(g, "GROUP BY " + g.GetType().Name))
                .ToList() ?? new List<ScalarExpression>();

            // SELECT 里的非聚合列必须出现在 GROUP BY 中;别名来自 SELECT
            foreach (var sc in selectColumns)
            {
                if (sc.Expression is FunctionCall f && AggregateFunctions.Contains(f.FunctionName.Value)) continue;
                var match = groupExprs.FirstOrDefault(g => SameGroupExpression(g, sc.Expression))
                    ?? throw (sc.Expression is FunctionCall
                        ? new SqlTranslationException($"{FragmentText(sc.Expression)} 是按日期分组的写法,只能用在分组查询里", sc.Expression,
                            $"在 GROUP BY 里也写上 {FragmentText(sc.Expression)}")
                        : new SqlTranslationException($"列 \"{sc.OutputName}\" 没有出现在 GROUP BY 中,也不是聚合函数", sc.Expression));
                groupBy.Add(ToGroupBy(match, sc.OutputName));
            }

            // GROUP BY 里有、SELECT 里没有的列也要加上(否则分组结果不对)
            foreach (var g in groupExprs)
            {
                var candidate = ToGroupBy(g, null);
                if (groupBy.Any(gb => SameColumn(gb.Column, candidate.Column) && gb.DateGrouping == candidate.DateGrouping))
                    continue;
                groupBy.Add(candidate);
                _ctx.Warn($"GROUP BY 的列 {candidate.Alias} 不在 SELECT 中,已自动加入输出");
            }

            // HAVING:FetchXML 没有等价物,翻译成本地谓词,执行后在客户端过滤
            if (spec.HavingClause is not null)
            {
                var translator = new Having.HavingTranslator(
                    aggregates, groupBy, selectAliases,
                    (func, alias) => BuildAggregate(func, alias, aggregates.Count),
                    expr =>
                    {
                        if (expr is FunctionCall f && TryGetDateGrouping(f, out var c, out var dg))
                            return groupBy.FirstOrDefault(g => SameColumn(g.Column, c!) && g.DateGrouping == dg);
                        if (expr is ColumnReferenceExpression col)
                            return groupBy.FirstOrDefault(g => SameColumn(g.Column, col) && g.DateGrouping is null);
                        return null;
                    });
                HavingPredicate = translator.Translate(spec.HavingClause.SearchCondition);
                HavingText = FragmentText(spec.HavingClause.SearchCondition);
                HiddenColumns.AddRange(translator.HiddenColumns);
            }

            node = new AggregateNode { Source = node, GroupBy = groupBy, Aggregates = aggregates, Origin = spec.GroupByClause ?? (TSqlFragment)spec };
        }
        else if (spec.HavingClause is not null)
        {
            throw new SqlTranslationException("HAVING 必须配合 GROUP BY 或聚合函数使用", spec.HavingClause);
        }

        // DISTINCT
        if (spec.UniqueRowFilter == UniqueRowFilter.Distinct)
            node = new DistinctNode { Source = node, Origin = spec };

        // SELECT
        var selectNode = new SelectNode { Source = node, Columns = selectColumns, Origin = spec };
        node = selectNode;

        // ORDER BY —— 放在 SELECT 之后,这样可以引用 SELECT 的别名
        if (spec.OrderByClause is not null)
        {
            // 聚合查询里直接写 ORDER BY COUNT(*) / YEAR(col):对应到已有的聚合列或分组列的 alias;
            // SELECT 里没有的聚合,作为隐藏列补进 FetchXML
            var expressionAliases = new Dictionary<ScalarExpression, string>();
            if (isAggregateQuery)
            {
                foreach (var order in spec.OrderByClause.OrderByElements)
                {
                    var expr = order.Expression;
                    while (expr is ParenthesisExpression p) expr = p.Expression;
                    if (expr is not FunctionCall func) continue;

                    if (AggregateFunctions.Contains(func.FunctionName.Value))
                    {
                        var wanted = BuildAggregate(func, null, aggregates.Count);
                        var existing = aggregates.FirstOrDefault(a => a.Function == wanted.Function && a.Distinct == wanted.Distinct
                            && (a.Column is null || wanted.Column is null ? a.Column is null && wanted.Column is null : SameColumn(a.Column, wanted.Column)));
                        if (existing is null)
                        {
                            existing = wanted with { Alias = $"orderby_{HiddenColumns.Count + 1}" };
                            aggregates.Add(existing);
                            HiddenColumns.Add(existing.Alias);
                        }
                        expressionAliases[order.Expression] = existing.Alias;
                    }
                    else if (TryGetDateGrouping(func, out var dateCol, out var grouping))
                    {
                        var match = groupBy.FirstOrDefault(g => SameColumn(g.Column, dateCol!) && g.DateGrouping == grouping);
                        if (match is not null) expressionAliases[order.Expression] = match.Alias;
                    }
                }
            }

            selectNode.Source = new SortNode
            {
                Source = selectNode.Source, Orders = spec.OrderByClause.OrderByElements, SelectAliases = selectAliases,
                ExpressionAliases = expressionAliases, Origin = spec.OrderByClause,
            };
        }

        // TOP
        if (spec.TopRowFilter is not null)
        {
            if (spec.TopRowFilter.Percent) throw SqlTranslationException.NotSupported(spec.TopRowFilter, "TOP ... PERCENT");
            if (spec.TopRowFilter.WithTies) throw SqlTranslationException.NotSupported(spec.TopRowFilter, "TOP ... WITH TIES");
            if (spec.OffsetClause is not null)
                throw new SqlTranslationException("TOP 和 OFFSET/FETCH 不能同时使用", spec.TopRowFilter, "分页请只用 OFFSET ... FETCH NEXT");
            var top = IntLiteral(spec.TopRowFilter.Expression);

            if (HavingPredicate is not null)
            {
                if (spec.OffsetClause is not null)
                    throw new SqlTranslationException("TOP 和 OFFSET/FETCH 不能同时使用", spec.TopRowFilter);
                LocalTop = top;
                _ctx.Warn($"TOP 要在 HAVING 之后生效,因此也改为本地处理:过滤后再取前 {top} 行");
            }
            else if (top == 0 || top > MaxFetchTop)
            {
                // FetchXML 的 top 表达不了这两种情况,改由执行时本地控制行数
                if (spec.OffsetClause is not null)
                    throw new SqlTranslationException("TOP 和 OFFSET/FETCH 不能同时使用", spec.TopRowFilter);
                LocalTop = top;
                _ctx.Warn(top == 0
                    ? "TOP 0 不会发送查询,只返回表头;生成的 FetchXML 里不含 top"
                    : $"FetchXML 的 top 最大为 {MaxFetchTop},TOP {top} 改为执行时翻页取够 {top} 行;生成的 FetchXML 里不含 top");
            }
            else
            {
                selectNode.Source = new TopNode { Source = selectNode.Source, Top = top, Origin = spec.TopRowFilter };
            }
        }

        // OFFSET / FETCH
        if (spec.OffsetClause is not null)
        {
            if (spec.OffsetClause.FetchExpression is null)
                throw new SqlTranslationException("OFFSET 必须配合 FETCH NEXT n ROWS ONLY 使用", spec.OffsetClause);
            var offset = IntLiteral(spec.OffsetClause.OffsetExpression);
            var fetch = IntLiteral(spec.OffsetClause.FetchExpression);
            if (fetch == 0)
                throw new SqlTranslationException("FETCH NEXT 的行数必须大于 0", spec.OffsetClause.FetchExpression);

            if (HavingPredicate is not null)
            {
                LocalOffset = offset;
                LocalTop = fetch;
                _ctx.Warn($"OFFSET/FETCH 要在 HAVING 之后生效,因此也改为本地处理:过滤后跳过 {offset} 行,再取 {fetch} 行");
            }
            else if (fetch > MaxFetchTop)
            {
                // FetchXML 每页最多 5000 行,更大的 FETCH NEXT 表达不了,和超限的 TOP 一样改由执行时翻页处理
                LocalOffset = offset;
                LocalTop = fetch;
                _ctx.Warn($"FetchXML 每页最多 {MaxFetchTop} 行,FETCH NEXT {fetch} 改为执行时翻页:跳过 {offset} 行后取够 {fetch} 行;生成的 FetchXML 里不含分页");
            }
            else
            {
                selectNode.Source = new OffsetNode { Source = selectNode.Source, Offset = offset, Fetch = fetch, Origin = spec.OffsetClause };
            }
        }

        return selectNode;
    }

    private PlanNode BuildTableReference(TableReference reference)
    {
        switch (reference)
        {
            case NamedTableReference table:
            {
                var schemaName = table.SchemaObject.SchemaIdentifier?.Value;
                if (schemaName is not null && !schemaName.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                    throw SqlTranslationException.NotSupported(table.SchemaObject, $"架构 \"{schemaName}\"", "直接写表的逻辑名即可,如 account");
                if (table.SchemaObject.DatabaseIdentifier is not null || table.SchemaObject.ServerIdentifier is not null)
                    throw SqlTranslationException.NotSupported(table.SchemaObject, "带数据库/服务器名的表名");

                var entityName = table.SchemaObject.BaseIdentifier.Value.ToLowerInvariant();
                var alias = table.Alias?.Value ?? entityName;

                var unsupportedHint = table.TableHints.FirstOrDefault(h => h.HintKind != TableHintKind.NoLock);
                if (unsupportedHint is not null)
                    throw SqlTranslationException.NotSupported(unsupportedHint, $"表提示 {unsupportedHint.HintKind}");

                var meta = _metadata.GetEntity(entityName);
                if (meta is null && _metadata is not NullMetadataProvider)
                    throw new SqlTranslationException($"表 \"{entityName}\" 不存在", table.SchemaObject, "请使用表的逻辑名(如 account、contact)");

                return new FetchXmlScanNode(entityName, alias, meta, table.TableHints.Any(h => h.HintKind == TableHintKind.NoLock)) { Origin = table };
            }

            case QualifiedJoin join:
                return new JoinNode
                {
                    Left = BuildTableReference(join.FirstTableReference),
                    Right = BuildTableReference(join.SecondTableReference),
                    JoinType = join.QualifiedJoinType,
                    On = join.SearchCondition,
                    Origin = join,
                };

            case JoinParenthesisTableReference paren:
                return BuildTableReference(paren.Join);

            case UnqualifiedJoin cross:
                throw SqlTranslationException.NotSupported(cross, "CROSS JOIN / CROSS APPLY");

            case QueryDerivedTable derived:
                throw SqlTranslationException.NotSupported(derived, "FROM (子查询)", "FetchXML 不支持派生表");

            default:
                throw SqlTranslationException.NotSupported(reference, reference.GetType().Name);
        }
    }

    private static AggregateColumn BuildAggregate(FunctionCall func, string? alias, int index)
    {
        var name = func.FunctionName.Value.ToLowerInvariant();
        var distinct = func.UniqueRowFilter == UniqueRowFilter.Distinct;

        if (func.Parameters.Count != 1)
            throw new SqlTranslationException($"{name}() 需要且只能有一个参数", func);

        if (func.Parameters[0] is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard })
        {
            if (name != "count") throw new SqlTranslationException($"{name}(*) 无效", func);
            if (distinct) throw new SqlTranslationException("COUNT(DISTINCT *) 无效", func);
            return new AggregateColumn("count", null, false, alias ?? "count", func);
        }

        if (func.Parameters[0] is not ColumnReferenceExpression col)
            throw SqlTranslationException.NotSupported(func.Parameters[0], "聚合函数里的表达式", "聚合函数的参数只能是列");

        var attr = col.MultiPartIdentifier.Identifiers[^1].Value.ToLowerInvariant();
        var function = name == "count" ? "countcolumn" : name;
        var defaultAlias = $"{attr}_{name}";

        if (alias is not null && !JoinNode.IsValidAlias(alias))
            throw new SqlTranslationException($"别名 \"{alias}\" 不能用于 FetchXML", func, "别名只能包含字母、数字和下划线");

        return new AggregateColumn(function, col, distinct, alias ?? defaultAlias, func);
    }

    /// <summary>聚合列的 alias 不能和已有的重复:默认别名自动加序号(count, count_2),显式写的别名重复则报错。</summary>
    private static AggregateColumn EnsureUniqueAlias(AggregateColumn agg, bool isExplicit, IEnumerable<string> usedAliases, TSqlFragment origin)
    {
        var used = new HashSet<string>(usedAliases, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(agg.Alias)) return agg;
        if (isExplicit)
            throw new SqlTranslationException($"列别名 \"{agg.Alias}\" 重复", origin, "每个输出列的别名必须唯一");
        for (var n = 2; ; n++)
        {
            var candidate = $"{agg.Alias}_{n}";
            if (!used.Contains(candidate)) return agg with { Alias = candidate };
        }
    }

    private GroupByColumn ToGroupBy(ScalarExpression expr, string? alias)
    {
        if (expr is ColumnReferenceExpression col)
            return new GroupByColumn(col, null, alias ?? col.MultiPartIdentifier.Identifiers[^1].Value.ToLowerInvariant());

        if (expr is FunctionCall func && TryGetDateGrouping(func, out var dateCol, out var grouping))
            return new GroupByColumn(dateCol!, grouping, alias ?? $"{dateCol!.MultiPartIdentifier.Identifiers[^1].Value.ToLowerInvariant()}_{grouping!.Replace('-', '_')}");

        throw SqlTranslationException.NotSupported(expr, "GROUP BY 表达式",
            "只能按列或 YEAR()/MONTH()/DAY()/DATEPART(...) 分组");
    }

    /// <summary>YEAR(col) / MONTH(col) / DAY(col) / DATEPART(part, col) → FetchXML dategrouping</summary>
    private static bool TryGetDateGrouping(FunctionCall? func, out ColumnReferenceExpression? column, out string? grouping)
    {
        column = null;
        grouping = null;
        if (func is null) return false;

        var name = func.FunctionName.Value.ToLowerInvariant();
        string? part = null;
        ScalarExpression? arg = null;

        if (name is "year" or "month" or "day" && func.Parameters.Count == 1)
        {
            part = name;
            arg = func.Parameters[0];
        }
        else if (name == "datepart" && func.Parameters.Count == 2)
        {
            // ScriptDom 把 DATEPART 的第一个参数解析成 IdentifierLiteral(有时是 ColumnReferenceExpression)
            part = func.Parameters[0] switch
            {
                IdentifierLiteral id => id.Value.ToLowerInvariant(),
                ColumnReferenceExpression partRef => partRef.MultiPartIdentifier.Identifiers[0].Value.ToLowerInvariant(),
                _ => null
            };
            arg = func.Parameters[1];
        }

        if (part is null || arg is not ColumnReferenceExpression col) return false;

        grouping = part switch
        {
            "year" or "yy" or "yyyy" => "year",
            "quarter" or "qq" or "q" => "quarter",
            "month" or "mm" or "m" => "month",
            "week" or "wk" or "ww" => "week",
            "day" or "dd" or "d" => "day",
            "fiscalperiod" => "fiscal-period",
            "fiscalyear" => "fiscal-year",
            _ => throw SqlTranslationException.NotSupported(func, $"DATEPART({part})", "支持 year/quarter/month/week/day/fiscalperiod/fiscalyear")
        };
        column = col;
        return true;
    }

    private static bool SameGroupExpression(ScalarExpression groupExpr, ScalarExpression selectExpr)
    {
        if (groupExpr is ColumnReferenceExpression g && selectExpr is ColumnReferenceExpression s)
            return SameColumn(g, s);

        if (groupExpr is FunctionCall gf && selectExpr is FunctionCall sf
            && TryGetDateGrouping(gf, out var gc, out var gg) && TryGetDateGrouping(sf, out var sc, out var sg))
            return gg == sg && SameColumn(gc!, sc!);

        return false;
    }

    private static bool SameColumn(ColumnReferenceExpression a, ColumnReferenceExpression b)
    {
        var pa = a.MultiPartIdentifier.Identifiers.Select(i => i.Value).ToList();
        var pb = b.MultiPartIdentifier.Identifiers.Select(i => i.Value).ToList();
        if (pa.Count == pb.Count) return pa.Zip(pb).All(p => p.First.Equals(p.Second, StringComparison.OrdinalIgnoreCase));
        // 一边有前缀一边没有:只比较列名
        return pa[^1].Equals(pb[^1], StringComparison.OrdinalIgnoreCase);
    }

    private static int IntLiteral(ScalarExpression expr)
    {
        while (expr is ParenthesisExpression p) expr = p.Expression;
        if (expr is IntegerLiteral lit && int.TryParse(lit.Value, out var value) && value >= 0)
            return value;
        throw new SqlTranslationException("这里只能是非负整数", expr);
    }

    private static void CopyPosition(TSqlFragment from, TSqlFragment to)
    {
        // ScriptDom 的位置属性是只读的,构造的节点不带位置;报错时退回到外层片段即可
        _ = from;
        _ = to;
    }
}
