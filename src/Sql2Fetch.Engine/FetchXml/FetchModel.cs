using System.Xml.Linq;

namespace Sql2Fetch.Engine.FetchXml;

/// <summary>
/// FetchXML 的内存对象模型。折叠阶段直接操作这些对象,最后再序列化成 XML。
/// 这是参考 Sql4Cds 的做法:FetchXML 不是字符串拼接出来的,而是可以随意修改结构的对象树。
/// </summary>
public sealed class FetchQuery
{
    public int? Top { get; set; }
    public int? Count { get; set; }
    public int? Page { get; set; }
    public bool Distinct { get; set; }
    public bool Aggregate { get; set; }
    public bool NoLock { get; set; }
    public FetchEntity Entity { get; } = new();

    /// <summary>生成根元素 &lt;fetch&gt;:只输出设置过的属性(top、count、page、distinct、aggregate、no-lock),再挂上主实体。</summary>
    public XElement ToXml()
    {
        var fetch = new XElement("fetch");
        if (Top is not null) fetch.SetAttributeValue("top", Top);
        if (Count is not null) fetch.SetAttributeValue("count", Count);
        if (Page is not null) fetch.SetAttributeValue("page", Page);
        if (Distinct) fetch.SetAttributeValue("distinct", "true");
        if (Aggregate) fetch.SetAttributeValue("aggregate", "true");
        if (NoLock) fetch.SetAttributeValue("no-lock", "true");
        fetch.Add(Entity.ToXml());
        return fetch;
    }

    /// <summary>序列化成带缩进的 FetchXML 文本,即最终显示和执行用的字符串。</summary>
    public string ToXmlString() => ToXml().ToString(SaveOptions.None);
}

public abstract class FetchEntityBase
{
    public string Name { get; set; } = "";
    public List<object> Items { get; } = new();

    public IEnumerable<FetchAttribute> Attributes => Items.OfType<FetchAttribute>();
    public IEnumerable<FetchFilter> Filters => Items.OfType<FetchFilter>();
    public IEnumerable<FetchOrder> Orders => Items.OfType<FetchOrder>();
    public IEnumerable<FetchLinkEntity> LinkEntities => Items.OfType<FetchLinkEntity>();
    public bool HasAllAttributes => Items.OfType<FetchAllAttributes>().Any();

    /// <summary>递归枚举所有 link-entity(含嵌套)。</summary>
    public IEnumerable<FetchLinkEntity> AllLinkEntities()
    {
        foreach (var link in LinkEntities)
        {
            yield return link;
            foreach (var nested in link.AllLinkEntities())
                yield return nested;
        }
    }

    /// <summary>按别名找到 link-entity。</summary>
    public FetchLinkEntity? FindLinkEntity(string alias) =>
        AllLinkEntities().FirstOrDefault(l => string.Equals(l.Alias, alias, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 添加一个普通列(非聚合、非分组)。已经有 all-attributes 且没有别名时不用再加;
    /// 同名同别名的普通列已存在时也不重复添加。
    /// </summary>
    public void AddAttribute(string name, string? alias = null)
    {
        if (HasAllAttributes && alias is null) return;
        if (Attributes.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(a.Alias, alias, StringComparison.OrdinalIgnoreCase)
                             && a.Aggregate is null && !a.GroupBy))
            return;
        Items.Add(new FetchAttribute { Name = name, Alias = alias });
    }

    /// <summary>
    /// 添加一个过滤器,空过滤器直接忽略。实体上已有 and 过滤器时并入其中:
    /// 新过滤器也是 and 就把条件平铺进去,是 or 就作为子过滤器挂上去。
    /// </summary>
    public void AddFilter(FetchFilter filter)
    {
        if (filter.Items.Count == 0) return;
        // 已有 and 过滤器时直接合并进去,避免嵌套太多层
        var existing = Filters.FirstOrDefault(f => f.Type == FetchFilterType.And);
        if (existing is not null && filter.Type == FetchFilterType.And)
            existing.Items.AddRange(filter.Items);
        else if (existing is not null)
            existing.Items.Add(filter);
        else
            Items.Add(filter);
    }

    /// <summary>把子节点(列、排序、过滤器、link-entity)按固定顺序写到给定的 XML 元素下,供子类的 ToXml 复用。</summary>
    protected void WriteChildren(XElement element)
    {
        // 按官方文档里的习惯顺序输出:attribute → all-attributes → order → filter → link-entity
        foreach (var a in Attributes) element.Add(a.ToXml());
        if (HasAllAttributes) element.Add(new XElement("all-attributes"));
        foreach (var o in Orders) element.Add(o.ToXml());
        foreach (var f in Filters) element.Add(f.ToXml());
        foreach (var l in LinkEntities) element.Add(l.ToXml());
    }

    /// <summary>生成本节点的 XML 元素:entity 或 link-entity,由子类决定。</summary>
    public abstract XElement ToXml();
}

public sealed class FetchEntity : FetchEntityBase
{
    /// <summary>生成 &lt;entity name="..."&gt; 元素及其子节点。</summary>
    public override XElement ToXml()
    {
        var e = new XElement("entity", new XAttribute("name", Name));
        WriteChildren(e);
        return e;
    }
}

public sealed class FetchLinkEntity : FetchEntityBase
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Alias { get; set; } = "";
    public string LinkType { get; set; } = "inner";

    /// <summary>生成 &lt;link-entity&gt; 元素(name、from、to、link-type、alias)及其子节点。</summary>
    public override XElement ToXml()
    {
        var e = new XElement("link-entity",
            new XAttribute("name", Name),
            new XAttribute("from", From),
            new XAttribute("to", To),
            new XAttribute("link-type", LinkType),
            new XAttribute("alias", Alias));
        WriteChildren(e);
        return e;
    }
}

public sealed class FetchAttribute
{
    public string Name { get; set; } = "";
    public string? Alias { get; set; }
    public string? Aggregate { get; set; }
    public bool GroupBy { get; set; }
    public string? DateGrouping { get; set; }
    public bool Distinct { get; set; }

    /// <summary>生成 &lt;attribute&gt; 元素;别名、聚合函数、分组、日期分组、distinct 只在设置了时输出。</summary>
    public XElement ToXml()
    {
        var e = new XElement("attribute", new XAttribute("name", Name));
        if (Alias is not null) e.SetAttributeValue("alias", Alias);
        if (Aggregate is not null) e.SetAttributeValue("aggregate", Aggregate);
        if (GroupBy) e.SetAttributeValue("groupby", "true");
        if (DateGrouping is not null) e.SetAttributeValue("dategrouping", DateGrouping);
        if (Distinct) e.SetAttributeValue("distinct", "true");
        return e;
    }
}

public sealed class FetchAllAttributes { }

public enum FetchFilterType { And, Or }

public sealed class FetchFilter
{
    public FetchFilterType Type { get; set; } = FetchFilterType.And;
    /// <summary>FetchCondition 或 FetchFilter</summary>
    public List<object> Items { get; } = new();

    /// <summary>递归枚举过滤器里的所有条件(含嵌套过滤器里的)。</summary>
    public IEnumerable<FetchCondition> AllConditions()
    {
        foreach (var item in Items)
        {
            if (item is FetchCondition c) yield return c;
            else if (item is FetchFilter f) foreach (var nested in f.AllConditions()) yield return nested;
        }
    }

    /// <summary>生成 &lt;filter type="and|or"&gt; 元素,条件和嵌套过滤器按添加顺序输出。</summary>
    public XElement ToXml()
    {
        var e = new XElement("filter", new XAttribute("type", Type == FetchFilterType.And ? "and" : "or"));
        foreach (var item in Items)
        {
            if (item is FetchCondition c) e.Add(c.ToXml());
            else if (item is FetchFilter f) e.Add(f.ToXml());
        }
        return e;
    }
}

public sealed class FetchCondition
{
    public string Attribute { get; set; } = "";
    public string Operator { get; set; } = "eq";
    public string? Value { get; set; }
    public List<string> Values { get; } = new();
    /// <summary>条件属于哪个 link-entity(别名);为 null 表示属于所在的实体。</summary>
    public string? EntityName { get; set; }
    /// <summary>列与列比较时使用。</summary>
    public string? ValueOf { get; set; }

    /// <summary>
    /// 生成 &lt;condition&gt; 元素。单值写在 value 属性里,多值(如 in、between)写成多个 &lt;value&gt; 子元素,
    /// 列与列比较时用 valueof。
    /// </summary>
    public XElement ToXml()
    {
        var e = new XElement("condition");
        if (EntityName is not null) e.SetAttributeValue("entityname", EntityName);
        e.SetAttributeValue("attribute", Attribute);
        e.SetAttributeValue("operator", Operator);
        if (Value is not null) e.SetAttributeValue("value", Value);
        if (ValueOf is not null) e.SetAttributeValue("valueof", ValueOf);
        foreach (var v in Values) e.Add(new XElement("value", v));
        return e;
    }
}

public sealed class FetchOrder
{
    public string? Attribute { get; set; }
    public string? Alias { get; set; }
    public bool Descending { get; set; }
    public string? EntityName { get; set; }

    /// <summary>生成 &lt;order&gt; 元素:普通查询按 attribute 排序,聚合查询按 alias 排序。</summary>
    public XElement ToXml()
    {
        var e = new XElement("order");
        if (EntityName is not null) e.SetAttributeValue("entityname", EntityName);
        if (Attribute is not null) e.SetAttributeValue("attribute", Attribute);
        if (Alias is not null) e.SetAttributeValue("alias", Alias);
        if (Descending) e.SetAttributeValue("descending", "true");
        return e;
    }
}
