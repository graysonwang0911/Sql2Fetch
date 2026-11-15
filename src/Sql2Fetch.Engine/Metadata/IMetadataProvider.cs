namespace Sql2Fetch.Engine.Metadata;

/// <summary>
/// 提供表/列元数据。返回 null 表示"不知道",引擎会跳过校验并给出警告;
/// 这样离线模式和在线模式可以共用同一套引擎。
/// </summary>
public interface IMetadataProvider
{
    EntityInfo? GetEntity(string logicalName);

    /// <summary>所有表的清单(自动补全用)。不知道时返回空。</summary>
    IEnumerable<EntityListItem> ListEntities() => Enumerable.Empty<EntityListItem>();
}

public sealed record EntityListItem(string LogicalName, string? DisplayName);

public sealed record EntityInfo(
    string LogicalName,
    string EntitySetName,
    string PrimaryIdAttribute,
    string? PrimaryNameAttribute,
    IReadOnlyDictionary<string, AttributeInfo> Attributes)
{
    public AttributeInfo? FindAttribute(string logicalName) =>
        Attributes.TryGetValue(logicalName, out var a) ? a : null;
}

public sealed record AttributeInfo(
    string LogicalName,
    string AttributeType,
    string? AttributeOf,
    string? DisplayName,
    IReadOnlyList<OptionInfo>? Options = null)
{
    /// <summary>选项集、状态、状态原因、是/否 这类有固定取值的列。</summary>
    public bool HasOptions => Options is { Count: > 0 };
}

public sealed record OptionInfo(int Value, string? Label);

/// <summary>离线模式:什么都不知道。</summary>
public sealed class NullMetadataProvider : IMetadataProvider
{
    public static readonly NullMetadataProvider Instance = new();
    public EntityInfo? GetEntity(string logicalName) => null;
}

/// <summary>内存字典实现,主要给测试用。</summary>
public sealed class DictionaryMetadataProvider : IMetadataProvider
{
    private readonly Dictionary<string, EntityInfo> _entities = new(StringComparer.OrdinalIgnoreCase);

    public DictionaryMetadataProvider Add(string logicalName, string primaryId, params string[] attributes)
    {
        var attrs = attributes
            .Concat(new[] { primaryId })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(a => a, a => new AttributeInfo(a, "String", null, null), StringComparer.OrdinalIgnoreCase);
        _entities[logicalName] = new EntityInfo(logicalName, logicalName + "s", primaryId, null, attrs);
        return this;
    }

    public EntityInfo? GetEntity(string logicalName) =>
        _entities.TryGetValue(logicalName, out var e) ? e : null;

    public IEnumerable<EntityListItem> ListEntities() =>
        _entities.Keys.OrderBy(k => k).Select(k => new EntityListItem(k, null));
}
