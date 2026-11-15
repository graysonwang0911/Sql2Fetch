using Sql2Fetch.Engine.Metadata;
using Sql2Fetch.Engine.Plan;

namespace Sql2Fetch.Engine;

public sealed class ConversionResult
{
    public required string FetchXml { get; init; }
    public required string EntityLogicalName { get; init; }
    public string? EntitySetName { get; init; }
    public required bool IsAggregate { get; init; }
    public required IReadOnlyList<OutputColumn> Columns { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>HAVING 翻译成的本地过滤条件;执行方拿到结果后逐行求值。null 表示没有。</summary>
    public Having.RowPredicate? LocalFilter { get; init; }
    /// <summary>原始 HAVING 文本,界面提示用。</summary>
    public string? LocalFilterText { get; init; }
    /// <summary>只为本地过滤而加进 FetchXML 的列,结果里应去掉。</summary>
    public IReadOnlyList<string> HiddenColumns { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 对外入口:SQL → FetchXML。
/// </summary>
public static class SqlToFetchXml
{
    public static ConversionResult Convert(string sql, IMetadataProvider? metadata = null)
    {
        metadata ??= NullMetadataProvider.Instance;
        var ctx = new FoldContext(metadata);

        var builder = new PlanBuilder(metadata, ctx);
        var select = builder.Build(sql);
        var folded = select.Fold(ctx);

        if (folded is not FetchXmlScanNode scan)
            throw new SqlTranslationException("查询无法完全翻译为 FetchXML");

        if (metadata is NullMetadataProvider)
            ctx.Warn("离线模式:未校验表名和列名");

        if (builder.HavingPredicate is not null)
            ctx.Warn($"HAVING 无法翻译成 FetchXML,将在取回聚合结果后本地过滤: {builder.HavingText}");

        return new ConversionResult
        {
            FetchXml = scan.Fetch.ToXmlString(),
            EntityLogicalName = scan.Fetch.Entity.Name,
            EntitySetName = scan.Meta?.EntitySetName,
            IsAggregate = scan.Fetch.Aggregate,
            Columns = select.Output,
            Warnings = ctx.Warnings,
            LocalFilter = builder.HavingPredicate,
            LocalFilterText = builder.HavingText,
            HiddenColumns = builder.HiddenColumns,
        };
    }
}
