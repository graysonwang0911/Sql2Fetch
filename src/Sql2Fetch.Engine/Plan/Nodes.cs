using Microsoft.SqlServer.TransactSql.ScriptDom;
using Sql2Fetch.Engine.FetchXml;
using Sql2Fetch.Engine.Metadata;

namespace Sql2Fetch.Engine.Plan;

/// <summary>
/// 逻辑执行计划节点。每种 SQL 子句一个节点;Fold() 把自己"压"进下方的 FetchXmlScanNode。
/// 对应 Sql4Cds 的 IDataExecutionPlanNode.FoldQuery。
/// </summary>
public abstract class PlanNode
{
    public TSqlFragment? Origin { get; init; }
    public abstract NodeSchema GetSchema();
    public abstract PlanNode Fold(FoldContext ctx);

    /// <summary>折叠后必须变成一个 FetchXmlScanNode,否则就是翻译不了。</summary>
    protected static FetchXmlScanNode RequireScan(PlanNode node, TSqlFragment? origin, string what)
    {
        if (node is FetchXmlScanNode scan) return scan;
        throw SqlTranslationException.NotSupported(origin!, what);
    }
}

/// <summary>叶子节点:一棵 FetchXML。FROM 里每张表最初都是一个只有 &lt;entity&gt; 的空壳。</summary>
public sealed class FetchXmlScanNode : PlanNode
{
    private readonly List<TableRef> _tables = new();

    public FetchXmlScanNode(string entityName, string alias, EntityInfo? meta, bool noLock)
    {
        Fetch = new FetchQuery { NoLock = noLock };
        Fetch.Entity.Name = entityName;
        Alias = alias;
        Meta = meta;
        _tables.Add(new TableRef(alias, entityName, meta));
    }

    public FetchQuery Fetch { get; }
    public string Alias { get; }
    public EntityInfo? Meta { get; }
    public IReadOnlyList<TableRef> Tables => _tables;

    public void AddTables(IEnumerable<TableRef> tables) => _tables.AddRange(tables);

    public override NodeSchema GetSchema() => new(_tables);
    public override PlanNode Fold(FoldContext ctx) => this;

    public bool IsRoot(string alias) => alias.Equals(Alias, StringComparison.OrdinalIgnoreCase);

    public FetchEntityBase GetEntity(string alias) =>
        IsRoot(alias) ? Fetch.Entity
        : Fetch.Entity.FindLinkEntity(alias) ?? throw new InvalidOperationException($"找不到别名 {alias} 对应的实体");

    /// <summary>该别名对应的 link-entity 及其所有祖先是否都是 inner 连接。</summary>
    public bool IsInnerPath(string alias)
    {
        if (IsRoot(alias)) return true;
        return PathTo(Fetch.Entity, alias)?.All(l => l.LinkType == "inner") ?? false;
    }

    private static List<FetchLinkEntity>? PathTo(FetchEntityBase entity, string alias)
    {
        foreach (var link in entity.LinkEntities)
        {
            if (link.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase))
                return new List<FetchLinkEntity> { link };
            var nested = PathTo(link, alias);
            if (nested is not null)
            {
                nested.Insert(0, link);
                return nested;
            }
        }
        return null;
    }
}

public sealed class JoinNode : PlanNode
{
    public required PlanNode Left { get; set; }
    public required PlanNode Right { get; set; }
    public required QualifiedJoinType JoinType { get; init; }
    public required BooleanExpression On { get; init; }

    public override NodeSchema GetSchema() => new(Left.GetSchema().Tables.Concat(Right.GetSchema().Tables));

    public override PlanNode Fold(FoldContext ctx)
    {
        var left = RequireScan(Left.Fold(ctx), Origin, "JOIN 的左侧");
        var right = RequireScan(Right.Fold(ctx), Origin, "JOIN 的右侧");

        if (JoinType == QualifiedJoinType.FullOuter)
            throw SqlTranslationException.NotSupported(Origin!, "FULL OUTER JOIN", "FetchXML 只支持 inner / left outer");

        // 拆 ON 条件:必须恰好有一个 左列 = 右列,其余条件放进 link-entity 的 filter
        var terms = new List<BooleanExpression>();
        FlattenAnd(On, terms);

        var leftSchema = left.GetSchema();
        var rightSchema = right.GetSchema();
        (ResolvedColumn Left, ResolvedColumn Right)? key = null;
        var extra = new List<BooleanExpression>();

        foreach (var term in terms)
        {
            if (key is null
                && term is BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } cmp
                && cmp.FirstExpression is ColumnReferenceExpression c1
                && cmp.SecondExpression is ColumnReferenceExpression c2)
            {
                var side1 = SideOf(c1, leftSchema, rightSchema);
                var side2 = SideOf(c2, leftSchema, rightSchema);
                if (side1 == 'L' && side2 == 'R') { key = (leftSchema.Resolve(c1), rightSchema.Resolve(c2)); continue; }
                if (side1 == 'R' && side2 == 'L') { key = (leftSchema.Resolve(c2), rightSchema.Resolve(c1)); continue; }
            }
            extra.Add(term);
        }

        if (key is null)
            throw new SqlTranslationException("JOIN 的 ON 条件里必须有一个 左表列 = 右表列 的等值条件", On);

        var joinType = JoinType;
        var (leftCol, rightCol) = key.Value;

        if (joinType == QualifiedJoinType.RightOuter)
        {
            // RIGHT JOIN 翻转成 LEFT JOIN
            (left, right) = (right, left);
            (leftCol, rightCol) = (rightCol, leftCol);
            (leftSchema, rightSchema) = (rightSchema, leftSchema);
            joinType = QualifiedJoinType.LeftOuter;
        }

        if (right.Fetch.Top is not null || right.Fetch.Distinct || right.Fetch.Aggregate || right.Fetch.Page is not null)
            throw SqlTranslationException.NotSupported(Origin!, "带 TOP/DISTINCT/聚合 的子查询作为 JOIN 的一侧");

        if (!right.IsRoot(rightCol.Alias))
            throw new SqlTranslationException($"只能通过右表的主表({right.Alias})进行连接,不能用 {rightCol.Alias}", On);

        var leftAliases = left.Tables.Select(t => t.Alias);
        var rightAliases = right.Tables.Select(t => t.Alias);
        var dup = leftAliases.Intersect(rightAliases, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (dup is not null)
            throw new SqlTranslationException($"表别名 \"{dup}\" 重复", Origin);

        if (!IsValidAlias(right.Alias))
            throw new SqlTranslationException($"别名 \"{right.Alias}\" 不能用于 FetchXML link-entity", Origin,
                "别名只能包含字母、数字和下划线,且不能以数字开头");

        var link = new FetchLinkEntity
        {
            Name = right.Fetch.Entity.Name,
            Alias = right.Alias,
            From = rightCol.Attribute,
            To = leftCol.Attribute,
            LinkType = joinType == QualifiedJoinType.Inner ? "inner" : "outer",
        };
        link.Items.AddRange(right.Fetch.Entity.Items);

        // 额外的 ON 条件:只引用右表的放进 link-entity 的 filter
        foreach (var term in extra)
        {
            var translated = CriteriaTranslator.Translate(term, rightSchema, ctx);
            var filter = translated as FetchFilter ?? new FetchFilter { Items = { translated } };
            foreach (var c in filter.AllConditions())
            {
                if (!right.IsRoot(c.EntityName!))
                    throw new SqlTranslationException("JOIN 的附加条件只能引用右表自身的列", term, "其他条件请移到 WHERE 里");
                c.EntityName = null;
            }
            link.AddFilter(filter);
        }

        left.GetEntity(leftCol.Alias).Items.Add(link);
        left.AddTables(right.Tables);
        left.Fetch.NoLock |= right.Fetch.NoLock;

        var linkCount = left.Fetch.Entity.AllLinkEntities().Count();
        if (linkCount > 10)
            ctx.Warn($"link-entity 数量为 {linkCount},Dataverse 最多允许 10 个(新版本 15 个)");

        return left;
    }

    private static char SideOf(ColumnReferenceExpression col, NodeSchema left, NodeSchema right)
    {
        var parts = col.MultiPartIdentifier.Identifiers;
        if (parts.Count == 2)
        {
            if (left.FindTable(parts[0].Value) is not null) return 'L';
            if (right.FindTable(parts[0].Value) is not null) return 'R';
            return '?';
        }
        // 无前缀:靠元数据猜
        var inLeft = left.Tables.Any(t => t.Meta?.FindAttribute(parts[0].Value) is not null);
        var inRight = right.Tables.Any(t => t.Meta?.FindAttribute(parts[0].Value) is not null);
        if (inLeft && !inRight) return 'L';
        if (inRight && !inLeft) return 'R';
        throw new SqlTranslationException($"无法判断列 \"{parts[0].Value}\" 属于 JOIN 的哪一侧", col, "请加上表别名前缀");
    }

    internal static void FlattenAnd(BooleanExpression expr, List<BooleanExpression> terms)
    {
        switch (expr)
        {
            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and:
                FlattenAnd(and.FirstExpression, terms);
                FlattenAnd(and.SecondExpression, terms);
                break;
            case BooleanParenthesisExpression paren:
                FlattenAnd(paren.Expression, terms);
                break;
            default:
                terms.Add(expr);
                break;
        }
    }

    internal static bool IsValidAlias(string alias) =>
        alias.Length > 0 && (char.IsLetter(alias[0]) || alias[0] == '_') && alias.All(c => char.IsLetterOrDigit(c) || c == '_');
}

public sealed class FilterNode : PlanNode
{
    public required PlanNode Source { get; set; }
    public required BooleanExpression Filter { get; init; }

    public override NodeSchema GetSchema() => Source.GetSchema();

    public override PlanNode Fold(FoldContext ctx)
    {
        var scan = RequireScan(Source.Fold(ctx), Origin, "WHERE 的数据源");
        var schema = scan.GetSchema();

        // 顶层 AND 的每一项单独决定放哪:
        //  - 只涉及一张 inner 路径上的表 → 放进那张表自己的 filter
        //  - 否则 → 放根实体的 filter,用 entityname 指向其他表(outer join 的列必须这样,否则语义会变成连接条件)
        var terms = new List<BooleanExpression>();
        JoinNode.FlattenAnd(Filter, terms);

        foreach (var term in terms)
        {
            var translated = CriteriaTranslator.Translate(term, schema, ctx);
            var filter = translated as FetchFilter ?? new FetchFilter { Items = { translated } };
            var aliases = filter.AllConditions().Select(c => c.EntityName!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (aliases.Count == 1 && scan.IsInnerPath(aliases[0]))
            {
                foreach (var c in filter.AllConditions()) c.EntityName = null;
                scan.GetEntity(aliases[0]).AddFilter(filter);
            }
            else
            {
                foreach (var c in filter.AllConditions())
                    if (scan.IsRoot(c.EntityName!)) c.EntityName = null;
                scan.Fetch.Entity.AddFilter(filter);
            }
        }

        return scan;
    }
}

public sealed class SortNode : PlanNode
{
    public required PlanNode Source { get; set; }
    public required IList<ExpressionWithSortOrder> Orders { get; init; }
    /// <summary>SELECT 别名 → 实际列(或聚合的 FetchXML alias)。</summary>
    public required IReadOnlyDictionary<string, ScalarExpression> SelectAliases { get; init; }
    /// <summary>ORDER BY 里的聚合/日期分组表达式 → 对应输出列的 alias(由 PlanBuilder 事先匹配好)。</summary>
    public IReadOnlyDictionary<ScalarExpression, string> ExpressionAliases { get; init; } = new Dictionary<ScalarExpression, string>();

    public override NodeSchema GetSchema() => Source.GetSchema();

    public override PlanNode Fold(FoldContext ctx)
    {
        var scan = RequireScan(Source.Fold(ctx), Origin, "ORDER BY 的数据源");
        var schema = scan.GetSchema();

        foreach (var order in Orders)
        {
            var descending = order.SortOrder == SortOrder.Descending;
            var expr = order.Expression;

            // ORDER BY COUNT(*) 这类表达式,已经对应到某个 alias
            if (ExpressionAliases.TryGetValue(expr, out var expressionAlias))
            {
                scan.Fetch.Entity.Items.Add(new FetchOrder { Alias = expressionAlias, Descending = descending });
                continue;
            }

            // ORDER BY 里的 SELECT 别名
            if (expr is ColumnReferenceExpression { MultiPartIdentifier.Identifiers.Count: 1 } aliasRef
                && SelectAliases.TryGetValue(aliasRef.MultiPartIdentifier.Identifiers[0].Value, out var target))
            {
                if (scan.Fetch.Aggregate)
                {
                    scan.Fetch.Entity.Items.Add(new FetchOrder { Alias = aliasRef.MultiPartIdentifier.Identifiers[0].Value, Descending = descending });
                    continue;
                }
                expr = target;
            }

            if (scan.Fetch.Aggregate)
            {
                // 聚合查询只能按 alias 排序
                if (expr is ColumnReferenceExpression groupCol)
                {
                    var resolved = schema.Resolve(groupCol);
                    var attr = scan.GetEntity(resolved.Alias).Attributes
                        .FirstOrDefault(a => a.GroupBy && a.Name.Equals(resolved.Attribute, StringComparison.OrdinalIgnoreCase));
                    if (attr?.Alias is not null)
                    {
                        scan.Fetch.Entity.Items.Add(new FetchOrder { Alias = attr.Alias, Descending = descending });
                        continue;
                    }
                }
                throw new SqlTranslationException("聚合查询的 ORDER BY 只能使用 SELECT 中的别名或 GROUP BY 的列", order);
            }

            if (expr is not ColumnReferenceExpression col)
                throw SqlTranslationException.NotSupported(expr, "ORDER BY 表达式", "只能按列排序");

            var c = schema.Resolve(col);
            scan.Fetch.Entity.Items.Add(new FetchOrder
            {
                Attribute = c.Attribute,
                Descending = descending,
                EntityName = scan.IsRoot(c.Alias) ? null : c.Alias,
            });
        }

        return scan;
    }
}

public sealed class TopNode : PlanNode
{
    public required PlanNode Source { get; set; }
    public required int Top { get; init; }

    public override NodeSchema GetSchema() => Source.GetSchema();

    public override PlanNode Fold(FoldContext ctx)
    {
        var scan = RequireScan(Source.Fold(ctx), Origin, "TOP 的数据源");
        if (scan.Fetch.Page is not null)
            throw new SqlTranslationException("TOP 和 OFFSET/FETCH 不能同时使用", Origin);
        scan.Fetch.Top = Top;
        return scan;
    }
}

public sealed class OffsetNode : PlanNode
{
    public required PlanNode Source { get; set; }
    public required int Offset { get; init; }
    public required int Fetch { get; init; }

    public override NodeSchema GetSchema() => Source.GetSchema();

    public override PlanNode Fold(FoldContext ctx)
    {
        var scan = RequireScan(Source.Fold(ctx), Origin, "OFFSET 的数据源");
        if (Fetch <= 0)
            throw new SqlTranslationException("FETCH NEXT 的行数必须大于 0", Origin);
        if (Offset % Fetch != 0)
            throw new SqlTranslationException("OFFSET 必须是 FETCH NEXT 行数的整数倍(FetchXML 只支持按页取)", Origin,
                $"例如 OFFSET {Fetch * (Offset / Fetch)} ROWS FETCH NEXT {Fetch} ROWS ONLY");
        scan.Fetch.Count = Fetch;
        scan.Fetch.Page = Offset / Fetch + 1;
        return scan;
    }
}

public sealed class DistinctNode : PlanNode
{
    public required PlanNode Source { get; set; }

    public override NodeSchema GetSchema() => Source.GetSchema();

    public override PlanNode Fold(FoldContext ctx)
    {
        var scan = RequireScan(Source.Fold(ctx), Origin, "DISTINCT 的数据源");
        scan.Fetch.Distinct = true;
        return scan;
    }
}

public sealed record GroupByColumn(ColumnReferenceExpression Column, string? DateGrouping, string Alias);

public sealed record AggregateColumn(string Function, ColumnReferenceExpression? Column, bool Distinct, string Alias, TSqlFragment Origin);

public sealed class AggregateNode : PlanNode
{
    public required PlanNode Source { get; set; }
    public required IList<GroupByColumn> GroupBy { get; init; }
    public required IList<AggregateColumn> Aggregates { get; init; }

    public override NodeSchema GetSchema() => Source.GetSchema();

    public override PlanNode Fold(FoldContext ctx)
    {
        var scan = RequireScan(Source.Fold(ctx), Origin, "GROUP BY 的数据源");
        var schema = scan.GetSchema();
        scan.Fetch.Aggregate = true;

        foreach (var g in GroupBy)
        {
            var c = schema.Resolve(g.Column);
            scan.GetEntity(c.Alias).Items.Add(new FetchAttribute
            {
                Name = c.Attribute,
                Alias = g.Alias,
                GroupBy = true,
                DateGrouping = g.DateGrouping,
            });
        }

        foreach (var a in Aggregates)
        {
            if (a.Column is null)
            {
                // COUNT(*) → 对主键做 count
                var root = scan.Tables[0];
                var pk = root.Meta?.PrimaryIdAttribute;
                if (pk is null)
                {
                    pk = root.EntityName + "id";
                    ctx.Warn($"没有元数据,COUNT(*) 假定主键为 {pk}");
                }
                scan.Fetch.Entity.Items.Add(new FetchAttribute { Name = pk, Alias = a.Alias, Aggregate = "count" });
                continue;
            }

            var c = schema.Resolve(a.Column);
            scan.GetEntity(c.Alias).Items.Add(new FetchAttribute
            {
                Name = c.Attribute,
                Alias = a.Alias,
                Aggregate = a.Function,
                Distinct = a.Distinct,
            });
        }

        return scan;
    }
}

public sealed record SelectColumn(string OutputName, ScalarExpression Expression, string? ExplicitAlias);

/// <summary>最终输出的一列:界面用来把结果 JSON 映射到表格。</summary>
public sealed record OutputColumn(string Name, string FetchKey, string? TableAlias, string? Attribute);

public sealed class SelectNode : PlanNode
{
    public required PlanNode Source { get; set; }
    public required IList<SelectColumn> Columns { get; init; }
    public List<OutputColumn> Output { get; } = new();

    public override NodeSchema GetSchema() => Source.GetSchema();

    public override PlanNode Fold(FoldContext ctx)
    {
        var scan = RequireScan(Source.Fold(ctx), Origin, "SELECT 的数据源");
        var schema = scan.GetSchema();

        foreach (var col in Columns)
        {
            switch (col.Expression)
            {
                case ColumnReferenceExpression { ColumnType: ColumnType.Wildcard } star:
                {
                    if (scan.Fetch.Aggregate)
                        throw new SqlTranslationException("聚合查询不能使用 *", star);
                    var entity = star.MultiPartIdentifier is { Identifiers.Count: > 0 } mpi
                        ? scan.GetEntity(schema.FindTable(mpi.Identifiers[0].Value)?.Alias
                            ?? throw new SqlTranslationException($"找不到表别名 \"{mpi.Identifiers[0].Value}\"", star))
                        : scan.Fetch.Entity;
                    entity.Items.RemoveAll(i => i is FetchAttribute { Aggregate: null, GroupBy: false, Alias: null });
                    if (!entity.HasAllAttributes) entity.Items.Add(new FetchAllAttributes());
                    var alias = entity is FetchLinkEntity l ? l.Alias : null;
                    Output.Add(new OutputColumn("*", alias is null ? "*" : alias + ".*", alias, null));
                    break;
                }

                case ColumnReferenceExpression column when !scan.Fetch.Aggregate:
                {
                    var c = schema.Resolve(column);
                    var entity = scan.GetEntity(c.Alias);
                    var alias = col.ExplicitAlias is not null && !col.ExplicitAlias.Equals(c.Attribute, StringComparison.OrdinalIgnoreCase)
                        ? col.ExplicitAlias : null;
                    if (alias is not null && !JoinNode.IsValidAlias(alias))
                        throw new SqlTranslationException($"列别名 \"{alias}\" 不能用于 FetchXML", column, "别名只能包含字母、数字和下划线");
                    entity.AddAttribute(c.Attribute, alias);
                    var key = alias ?? (scan.IsRoot(c.Alias) ? c.Attribute : $"{c.Alias}.{c.Attribute}");
                    Output.Add(new OutputColumn(col.OutputName, key, c.Alias, c.Attribute));
                    break;
                }

                default:
                    // 聚合模式:属性已经由 AggregateNode 加好,这里只记录输出列
                    if (scan.Fetch.Aggregate)
                    {
                        Output.Add(new OutputColumn(col.OutputName, col.OutputName, null, null));
                        break;
                    }
                    throw SqlTranslationException.NotSupported(col.Expression, "SELECT 中的表达式",
                        "FetchXML 只能返回列本身;计算请在取回数据后进行");
            }
        }

        return scan;
    }
}
