using System.Collections.Concurrent;
using System.Text.Json;
using Sql2Fetch.Engine.Metadata;

namespace Sql2Fetch.Dataverse;

/// <summary>
/// 从 Web API 读元数据。表清单一次性加载,列按表懒加载并缓存。
/// IMetadataProvider 是同步接口,引擎应在后台线程调用,这里内部阻塞等待 HTTP。
/// </summary>
public sealed class DataverseMetadataProvider : IMetadataProvider
{
    private readonly DataverseConnection _connection;
    private readonly ConcurrentDictionary<string, EntityInfo?> _entities = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, EntitySummary>? _summaries;

    public DataverseMetadataProvider(DataverseConnection connection)
    {
        _connection = connection;
    }

    /// <summary>按元数据粗分的表类型:数据主要由谁产生。Unknown 表示环境没返回分类所需的属性。</summary>
    public enum TableCategory { Business = 0, Config = 1, System = 2, Intersect = 3, Unknown = 4 }

    public sealed record EntitySummary(string LogicalName, string EntitySetName, string PrimaryIdAttribute, string? PrimaryNameAttribute, string? DisplayName,
        TableCategory Category = TableCategory.Unknown, string CategoryDetail = "");

    /// <summary>
    /// 分类规则(启发式,不是 Dataverse 的官方分类):
    /// 中间表 = N:N 关系自动生成;系统 = 平台内部表、业务流程实例表、无所有权的表,以及用户在高级查找里看不到的非自定义表;
    /// 业务 = 记录归用户/团队所有;配置 = 其余(组织级的主数据和设置)。
    /// </summary>
    private static (TableCategory Category, string Detail) Classify(JsonElement e)
    {
        static bool Flag(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return false;
            if (v.ValueKind == JsonValueKind.True) return true;
            // 托管属性是 { "Value": true, ... } 的形式
            return v.ValueKind == JsonValueKind.Object && v.TryGetProperty("Value", out var inner) && inner.ValueKind == JsonValueKind.True;
        }

        if (!e.TryGetProperty("OwnershipType", out var o)) return (TableCategory.Unknown, "");
        var ownership = o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "";

        var isCustom = Flag(e, "IsCustomEntity");
        var isIntersect = Flag(e, "IsIntersect");
        var isPrivate = Flag(e, "IsPrivate");
        var isBpf = Flag(e, "IsBPFEntity");
        var isActivity = Flag(e, "IsActivity");
        var advancedFind = Flag(e, "IsValidForAdvancedFind");
        var userOwned = ownership is "UserOwned" or "TeamOwned";

        var category =
            isIntersect ? TableCategory.Intersect
            : isPrivate || isBpf || ownership is "None" or "" ? TableCategory.System
            : isCustom || isActivity ? (userOwned ? TableCategory.Business : TableCategory.Config)
            : !advancedFind ? TableCategory.System
            : userOwned ? TableCategory.Business
            : TableCategory.Config;

        var parts = new List<string> { "所有权: " + (ownership.Length == 0 ? "无" : ownership) };
        parts.Add(isCustom ? "自定义表" : "系统自带表");
        if (isIntersect) parts.Add("N:N 中间表");
        if (isPrivate) parts.Add("平台内部表");
        if (isBpf) parts.Add("业务流程实例表");
        if (isActivity) parts.Add("活动表");
        parts.Add(advancedFind ? "高级查找可见" : "高级查找不可见");
        return (category, string.Join(" · ", parts));
    }

    public IReadOnlyCollection<EntitySummary> Entities => _summaries?.Values ?? (IReadOnlyCollection<EntitySummary>)Array.Empty<EntitySummary>();

    /// <summary>加载全部表的概要(名称、集合名、主键)。</summary>
    public async Task LoadEntitiesAsync(CancellationToken ct = default)
    {
        const string baseSelect = "EntityDefinitions?$select=LogicalName,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute,DisplayName";
        string json;
        try
        {
            json = await _connection.GetAsync(
                baseSelect + ",OwnershipType,IsCustomEntity,IsIntersect,IsPrivate,IsActivity,IsBPFEntity,IsValidForAdvancedFind", ct);
        }
        catch (DataverseException)
        {
            // 个别环境不认其中某个属性时,退回只取基本信息(表类型显示为未知),不影响连接
            json = await _connection.GetAsync(baseSelect, ct);
        }

        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, EntitySummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var logical = e.GetProperty("LogicalName").GetString()!;
            var (category, categoryDetail) = Classify(e);
            result[logical] = new EntitySummary(
                logical,
                e.GetProperty("EntitySetName").GetString() ?? logical + "s",
                e.GetProperty("PrimaryIdAttribute").GetString() ?? logical + "id",
                e.TryGetProperty("PrimaryNameAttribute", out var pn) ? pn.GetString() : null,
                ReadLabel(e, "DisplayName"),
                category, categoryDetail);
        }

        _summaries = result;
        _entities.Clear();
        _relationships.Clear();
    }

    public EntityInfo? GetEntity(string logicalName)
    {
        if (_summaries is null)
            throw new InvalidOperationException("元数据尚未加载,请先调用 LoadEntitiesAsync");

        if (!_summaries.TryGetValue(logicalName, out var summary))
            return null;

        return _entities.GetOrAdd(logicalName, _ => LoadAttributesAsync(summary).GetAwaiter().GetResult());
    }

    public Task<EntityInfo?> GetEntityAsync(string logicalName) => Task.Run(() => GetEntity(logicalName));

    public IEnumerable<EntityListItem> ListEntities() =>
        _summaries?.Values.OrderBy(s => s.LogicalName).Select(s => new EntityListItem(s.LogicalName, s.DisplayName))
        ?? Enumerable.Empty<EntityListItem>();

    private readonly ConcurrentDictionary<string, IReadOnlyList<RelationshipInfo>> _relationships = new(StringComparer.OrdinalIgnoreCase);

    public enum RelationshipKind { ManyToOne, OneToMany, ManyToMany }

    /// <summary>
    /// 某张表的一条关系。<paramref name="Column"/>:N:1 是本表的查找列,1:N 是对方表的查找列,N:N 是中间表名。
    /// 自关联时 RelatedEntity 就是本表。
    /// </summary>
    public sealed record RelationshipInfo(RelationshipKind Kind, string SchemaName, string RelatedEntity, string Column);

    /// <summary>读取一张表的全部关系(ER 图用),按表缓存。</summary>
    public async Task<IReadOnlyList<RelationshipInfo>> GetRelationshipsAsync(string logicalName, CancellationToken ct = default)
    {
        if (_relationships.TryGetValue(logicalName, out var cachedList)) return cachedList;

        const string oneToMany = "$select=SchemaName,ReferencingEntity,ReferencingAttribute,ReferencedEntity";
        var json = await _connection.GetAsync(
            $"EntityDefinitions(LogicalName='{logicalName}')?$select=LogicalName" +
            $"&$expand=ManyToOneRelationships({oneToMany}),OneToManyRelationships({oneToMany})," +
            "ManyToManyRelationships($select=SchemaName,Entity1LogicalName,Entity2LogicalName,IntersectEntityName)", ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = new List<RelationshipInfo>();

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        if (root.TryGetProperty("ManyToOneRelationships", out var m1) && m1.ValueKind == JsonValueKind.Array)
            foreach (var r in m1.EnumerateArray())
                list.Add(new RelationshipInfo(RelationshipKind.ManyToOne, Str(r, "SchemaName"), Str(r, "ReferencedEntity"), Str(r, "ReferencingAttribute")));

        if (root.TryGetProperty("OneToManyRelationships", out var om) && om.ValueKind == JsonValueKind.Array)
            foreach (var r in om.EnumerateArray())
                list.Add(new RelationshipInfo(RelationshipKind.OneToMany, Str(r, "SchemaName"), Str(r, "ReferencingEntity"), Str(r, "ReferencingAttribute")));

        if (root.TryGetProperty("ManyToManyRelationships", out var mm) && mm.ValueKind == JsonValueKind.Array)
            foreach (var r in mm.EnumerateArray())
            {
                var e1 = Str(r, "Entity1LogicalName");
                var e2 = Str(r, "Entity2LogicalName");
                var other = string.Equals(e1, logicalName, StringComparison.OrdinalIgnoreCase) ? e2 : e1;
                list.Add(new RelationshipInfo(RelationshipKind.ManyToMany, Str(r, "SchemaName"), other, Str(r, "IntersectEntityName")));
            }

        list.RemoveAll(r => string.IsNullOrEmpty(r.RelatedEntity));
        _relationships[logicalName] = list;
        return list;
    }

    /// <summary>
    /// 批量取各表的记录数(RetrieveTotalRecordCount)。这是 Dataverse 定期刷新的快照,不是实时值。
    /// 每批取完就回调一次;取不到的表(不支持统计、无权限)直接跳过。
    /// </summary>
    public async Task LoadRecordCountsAsync(IEnumerable<string> logicalNames,
        Action<IReadOnlyDictionary<string, long>> onBatch, CancellationToken ct = default)
    {
        const int batchSize = 40;
        using var gate = new SemaphoreSlim(4);

        var tasks = logicalNames.Chunk(batchSize).Select(async batch =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var counts = await LoadRecordCountBatchAsync(batch, ct);
                if (counts.Count > 0) onBatch(counts);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
    }

    /// <summary>一批里只要有一张表不支持,整个请求就会失败,所以失败时对半拆开重试,直到定位到单张表。</summary>
    private async Task<Dictionary<string, long>> LoadRecordCountBatchAsync(string[] names, CancellationToken ct)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var list = Uri.EscapeDataString(JsonSerializer.Serialize(names));
            var json = await _connection.GetAsync($"RetrieveTotalRecordCount(EntityNames=@p1)?@p1={list}", ct);

            using var doc = JsonDocument.Parse(json);
            var collection = doc.RootElement.GetProperty("EntityRecordCountCollection");
            var keys = collection.GetProperty("Keys").EnumerateArray().Select(k => k.GetString()).ToArray();
            var values = collection.GetProperty("Values").EnumerateArray().Select(v => v.GetInt64()).ToArray();
            for (var i = 0; i < keys.Length && i < values.Length; i++)
                if (keys[i] is { } key) result[key] = values[i];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            if (names.Length <= 1) return result;
            var half = names.Length / 2;
            foreach (var part in new[] { names[..half], names[half..] })
                foreach (var kv in await LoadRecordCountBatchAsync(part, ct))
                    result[kv.Key] = kv.Value;
        }
        return result;
    }

    /// <summary>后台预加载若干表的列,让补全不卡顿。</summary>
    public void Prefetch(IEnumerable<string> logicalNames)
    {
        foreach (var name in logicalNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_summaries is not null && _summaries.ContainsKey(name) && !_entities.ContainsKey(name))
                _ = Task.Run(() => { try { GetEntity(name); } catch { } });
        }
    }

    private async Task<EntityInfo?> LoadAttributesAsync(EntitySummary summary)
    {
        var json = await _connection.GetAsync(
            $"EntityDefinitions(LogicalName='{summary.LogicalName}')/Attributes?$select=LogicalName,AttributeType,AttributeOf,DisplayName");

        // 选项集类列的取值要按类型分别查(Web API 的类型转换语法),并行发出去
        var optionTasks = new[]
        {
            "PicklistAttributeMetadata", "StateAttributeMetadata", "StatusAttributeMetadata",
            "MultiSelectPicklistAttributeMetadata", "BooleanAttributeMetadata",
        }.Select(type => LoadOptionsAsync(summary.LogicalName, type)).ToArray();

        using var doc = JsonDocument.Parse(json);
        var options = (await Task.WhenAll(optionTasks))
            .SelectMany(d => d)
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

        var attrs = new Dictionary<string, AttributeInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var logical = a.GetProperty("LogicalName").GetString()!;
            attrs[logical] = new AttributeInfo(
                logical,
                a.TryGetProperty("AttributeType", out var t) ? t.GetString() ?? "" : "",
                a.TryGetProperty("AttributeOf", out var of) ? of.GetString() : null,
                ReadLabel(a, "DisplayName"),
                options.TryGetValue(logical, out var opts) ? opts : null);
        }

        return new EntityInfo(summary.LogicalName, summary.EntitySetName, summary.PrimaryIdAttribute, summary.PrimaryNameAttribute, attrs);
    }

    /// <summary>某一类选项集列的 逻辑名 → 选项列表。失败时返回空,不影响列本身的加载。</summary>
    private async Task<Dictionary<string, IReadOnlyList<OptionInfo>>> LoadOptionsAsync(string entity, string typeName)
    {
        var result = new Dictionary<string, IReadOnlyList<OptionInfo>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var expand = typeName == "BooleanAttributeMetadata"
                ? "OptionSet($select=TrueOption,FalseOption)"
                : "OptionSet($select=Options),GlobalOptionSet($select=Options)";
            var json = await _connection.GetAsync(
                $"EntityDefinitions(LogicalName='{entity}')/Attributes/Microsoft.Dynamics.CRM.{typeName}?$select=LogicalName&$expand={expand}");

            using var doc = JsonDocument.Parse(json);
            foreach (var a in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var logical = a.GetProperty("LogicalName").GetString()!;
                var list = new List<OptionInfo>();

                if (typeName == "BooleanAttributeMetadata")
                {
                    if (a.TryGetProperty("OptionSet", out var os) && os.ValueKind == JsonValueKind.Object)
                    {
                        if (os.TryGetProperty("FalseOption", out var f) && f.ValueKind == JsonValueKind.Object) list.Add(ReadOption(f));
                        if (os.TryGetProperty("TrueOption", out var t) && t.ValueKind == JsonValueKind.Object) list.Add(ReadOption(t));
                    }
                }
                else
                {
                    foreach (var setName in new[] { "OptionSet", "GlobalOptionSet" })
                    {
                        if (a.TryGetProperty(setName, out var os) && os.ValueKind == JsonValueKind.Object
                            && os.TryGetProperty("Options", out var opts) && opts.ValueKind == JsonValueKind.Array && opts.GetArrayLength() > 0)
                        {
                            foreach (var o in opts.EnumerateArray()) list.Add(ReadOption(o));
                            break;
                        }
                    }
                }

                if (list.Count > 0) result[logical] = list;
            }
        }
        catch
        {
            // 某些表没有这类列会返回 404,忽略
        }
        return result;
    }

    private static OptionInfo ReadOption(JsonElement o) =>
        new(o.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0, ReadLabel(o, "Label"));

    private static string? ReadLabel(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var label)) return null;
        if (label.TryGetProperty("UserLocalizedLabel", out var ull) && ull.ValueKind == JsonValueKind.Object
            && ull.TryGetProperty("Label", out var l))
            return l.GetString();
        return null;
    }
}
