using System.Text.Json;
using System.Xml.Linq;

namespace Sql2Fetch.Dataverse;

public sealed class QueryResult
{
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<string?[]> Rows { get; init; }
    public required bool MoreRecords { get; init; }
    public required int PagesRetrieved { get; init; }
    public required TimeSpan Elapsed { get; init; }
    /// <summary>被本地过滤(HAVING)掉的行数。</summary>
    public int FilteredOut { get; init; }
}

/// <summary>
/// 用 Web API 执行 FetchXML:GET {entitySet}?fetchXml=...。
/// 自动翻页(paging cookie),优先显示格式化值(选项集标签、查找名称、日期本地化)。
/// </summary>
public sealed class FetchXmlExecutor
{
    private const string FormattedSuffix = "@OData.Community.Display.V1.FormattedValue";
    /// <summary>查找列的注解:这一行实际指向哪张表(customerid、ownerid 这类多态查找每行可能不同)。</summary>
    private const string LookupTableSuffix = "@Microsoft.Dynamics.CRM.lookuplogicalname";
    private readonly DataverseConnection _connection;

    public FetchXmlExecutor(DataverseConnection connection)
    {
        _connection = connection;
    }

    /// <param name="localFilter">对每行原始值求值的本地谓词(HAVING),返回 false 的行丢弃</param>
    /// <param name="hiddenColumns">不放进结果的列(为本地过滤而加的聚合列)</param>
    /// <param name="outputColumns">引擎给出的输出列;提供后结果的列顺序和表头以它为准</param>
    public async Task<QueryResult> ExecuteAsync(
        string fetchXml, string entitySetName, int maxPages = 10, bool preferFormatted = true, CancellationToken ct = default,
        Sql2Fetch.Engine.Having.RowPredicate? localFilter = null, IReadOnlyCollection<string>? hiddenColumns = null,
        IReadOnlyList<Sql2Fetch.Engine.Plan.OutputColumn>? outputColumns = null,
        int? localTop = null, int localOffset = 0)
    {
        var started = DateTime.UtcNow;
        var fetch = XElement.Parse(fetchXml);
        var map = new ResultColumnMap(outputColumns);
        var rows = new List<string?[]>();
        var pages = 0;
        var more = false;
        var filteredOut = 0;
        var skipped = 0;
        // 本地 TOP:过滤之后数够了就不再往下取
        bool TopReached() => localTop is { } limit && rows.Count >= limit;
        var hidden = new HashSet<string>(hiddenColumns ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var explicitPaging = fetch.Attribute("page") is not null || fetch.Attribute("top") is not null;

        // TOP 0:不用发请求,只要表头
        while (localTop != 0)
        {
            var json = await _connection.GetAsync(
                $"{entitySetName}?fetchXml={Uri.EscapeDataString(fetch.ToString(SaveOptions.DisableFormatting))}",
                ct,
                ("Prefer", "odata.include-annotations=\"*\""));
            pages++;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var record in root.GetProperty("value").EnumerateArray())
            {
                if (TopReached()) break;

                var values = new Dictionary<string, string?>();
                var formatted = new Dictionary<string, string?>();
                var lookupTables = new Dictionary<string, string?>();

                foreach (var prop in record.EnumerateObject())
                {
                    var name = prop.Name;
                    if (name.StartsWith("@odata.", StringComparison.Ordinal)) continue;

                    if (name.EndsWith(FormattedSuffix, StringComparison.Ordinal))
                    {
                        formatted[name[..^FormattedSuffix.Length]] = prop.Value.GetString();
                        continue;
                    }
                    if (name.EndsWith(LookupTableSuffix, StringComparison.Ordinal))
                    {
                        lookupTables[name[..^LookupTableSuffix.Length]] = prop.Value.GetString();
                        continue;
                    }
                    if (name.Contains('@')) continue; // 其他注解

                    values[name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.String => prop.Value.GetString(),
                        _ => prop.Value.GetRawText(),
                    };
                }

                // HAVING:用原始值求值,不满足的整行丢掉
                if (localFilter is not null && !localFilter.Evaluate(k => values.TryGetValue(k, out var v) ? v : null))
                {
                    filteredOut++;
                    continue;
                }

                // 本地 OFFSET:跳过的是过滤之后的行
                if (skipped < localOffset)
                {
                    skipped++;
                    continue;
                }

                foreach (var h in hidden) values.Remove(h);

                foreach (var key in values.Keys) map.IndexesFor(key);

                var row = new string?[map.Columns.Count];
                foreach (var (key, value) in values)
                {
                    var text = preferFormatted && formatted.TryGetValue(key, out var f) && f is not null ? f : value;
                    // 查找列:在名称后面带上关联的表名,如 "Contoso (account)"
                    if (text is not null && lookupTables.TryGetValue(key, out var table) && !string.IsNullOrEmpty(table))
                        text = $"{text} ({table})";
                    foreach (var index in map.IndexesFor(key)) row[index] = text;
                }
                rows.Add(row);
            }

            var cookie = root.TryGetProperty("@Microsoft.Dynamics.CRM.fetchxmlpagingcookie", out var c) ? c.GetString() : null;
            more = !string.IsNullOrEmpty(cookie);

            if (TopReached())
            {
                more = false;
                break;
            }

            if (!more || explicitPaging || pages >= maxPages)
                break;

            // 翻页:paging cookie 是被 XML 转义过的字符串,要先解出 pagingcookie 属性的值
            var cookieXml = XElement.Parse(cookie!);
            var pagingCookie = cookieXml.Attribute("pagingcookie")?.Value;
            var page = int.Parse(fetch.Attribute("page")?.Value ?? "1") + 1;
            fetch.SetAttributeValue("page", page);
            fetch.SetAttributeValue("paging-cookie", pagingCookie is null ? null : Uri.UnescapeDataString(Uri.UnescapeDataString(pagingCookie)));
        }

        // 行长度补齐到最终列数
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Length < map.Columns.Count)
            {
                var padded = new string?[map.Columns.Count];
                rows[i].CopyTo(padded, 0);
                rows[i] = padded;
            }
        }

        return new QueryResult
        {
            Columns = map.Columns,
            Rows = rows,
            MoreRecords = more,
            PagesRetrieved = pages,
            Elapsed = DateTime.UtcNow - started,
            FilteredOut = filteredOut,
        };
    }
}
