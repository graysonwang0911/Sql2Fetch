using Microsoft.SqlServer.TransactSql.ScriptDom;
using Sql2Fetch.Engine.Metadata;

namespace Sql2Fetch.Engine.Plan;

/// <summary>FROM 子句里出现的一张表(含别名和元数据)。</summary>
public sealed class TableRef
{
    public TableRef(string alias, string entityName, EntityInfo? meta)
    {
        Alias = alias;
        EntityName = entityName;
        Meta = meta;
    }

    public string Alias { get; }
    public string EntityName { get; }
    public EntityInfo? Meta { get; }
}

/// <summary>解析完成的列引用:属于哪张表、哪个属性。</summary>
public sealed record ResolvedColumn(TableRef Table, string Attribute)
{
    public string Alias => Table.Alias;
    public string Key => $"{Table.Alias}.{Attribute}";
}

/// <summary>折叠过程中的共享上下文。</summary>
public sealed class FoldContext
{
    public FoldContext(IMetadataProvider metadata)
    {
        Metadata = metadata;
    }

    public IMetadataProvider Metadata { get; }
    public List<string> Warnings { get; } = new();

    public void Warn(string message)
    {
        if (!Warnings.Contains(message)) Warnings.Add(message);
    }
}

/// <summary>
/// 一个节点对外暴露的"有哪些表可用"。列解析按 SQL 规则:有别名就按别名找,
/// 没别名时如果只有一张表就归它,多张表时靠元数据判断属于哪张,判断不了就报歧义。
/// </summary>
public sealed class NodeSchema
{
    private readonly List<TableRef> _tables;

    public NodeSchema(IEnumerable<TableRef> tables)
    {
        _tables = tables.ToList();
    }

    public IReadOnlyList<TableRef> Tables => _tables;

    public TableRef? FindTable(string alias) =>
        _tables.FirstOrDefault(t => t.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));

    public ResolvedColumn Resolve(ColumnReferenceExpression column) =>
        Resolve(column.MultiPartIdentifier, column);

    public ResolvedColumn Resolve(MultiPartIdentifier identifier, TSqlFragment origin)
    {
        var parts = identifier.Identifiers.Select(i => i.Value).ToList();

        if (parts.Count > 2)
            throw new SqlTranslationException($"列名层级过多: {string.Join(".", parts)}", origin, "只支持 列名 或 表别名.列名");

        if (parts.Count == 2)
        {
            var table = FindTable(parts[0])
                ?? throw new SqlTranslationException($"找不到表别名 \"{parts[0]}\"", origin,
                    "可用的表: " + string.Join(", ", _tables.Select(t => t.Alias)));
            ValidateAttribute(table, parts[1], origin);
            return new ResolvedColumn(table, parts[1].ToLowerInvariant());
        }

        var name = parts[0];

        if (_tables.Count == 1)
        {
            ValidateAttribute(_tables[0], name, origin);
            return new ResolvedColumn(_tables[0], name.ToLowerInvariant());
        }

        var known = _tables.Where(t => t.Meta is not null && t.Meta.FindAttribute(name) is not null).ToList();
        var unknown = _tables.Where(t => t.Meta is null).ToList();

        if (known.Count == 1 && unknown.Count == 0)
            return new ResolvedColumn(known[0], name.ToLowerInvariant());

        if (known.Count > 1)
            throw new SqlTranslationException($"列名 \"{name}\" 有歧义,存在于: {string.Join(", ", known.Select(t => t.Alias))}", origin,
                "请加上表别名前缀");

        if (known.Count == 0 && unknown.Count == 0)
            throw new SqlTranslationException($"找不到列 \"{name}\"", origin);

        throw new SqlTranslationException($"无法确定列 \"{name}\" 属于哪张表", origin,
            "多表查询时请使用 表别名.列名 的写法");
    }

    private static void ValidateAttribute(TableRef table, string attribute, TSqlFragment origin)
    {
        if (table.Meta is null) return;
        if (table.Meta.FindAttribute(attribute) is not null) return;

        var closest = table.Meta.Attributes.Keys
            .Select(k => (Name: k, Distance: Levenshtein(k, attribute)))
            .OrderBy(x => x.Distance)
            .FirstOrDefault();

        var suggestion = closest.Name is not null && closest.Distance <= Math.Max(2, attribute.Length / 3)
            ? $"是不是想写 \"{closest.Name}\"?"
            : null;

        throw new SqlTranslationException($"表 {table.EntityName} 中不存在列 \"{attribute}\"", origin, suggestion);
    }

    private static int Levenshtein(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
