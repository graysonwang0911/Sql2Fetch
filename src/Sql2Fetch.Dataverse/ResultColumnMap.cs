using System.Text.RegularExpressions;
using Sql2Fetch.Engine.Plan;

namespace Sql2Fetch.Dataverse;

/// <summary>
/// 决定结果表有哪些列、每个 JSON 属性落在哪一列。
/// 以引擎给出的输出列为准(顺序、表头用 SQL 里写的名字),这样:
///  - 列顺序和 SELECT 一致;
///  - 查找列不再显示成 _xxx_value;
///  - 整列都为空(Web API 不返回空值属性)时列也还在。
/// 输出列之外的属性(SELECT * 展开的列等)按出现顺序追加在后面。
/// </summary>
public sealed class ResultColumnMap
{
    private static readonly Regex LookupKey = new(@"^(?:(?<alias>.+)\.)?_(?<attr>.+)_value$", RegexOptions.Compiled);

    private readonly List<string> _columns = new();
    // 一个 JSON 属性可能对应多列:SELECT name, name 在 FetchXML 里只能取一次,但结果里要显示两列
    private readonly Dictionary<string, List<int>> _index = new(StringComparer.OrdinalIgnoreCase);

    public ResultColumnMap(IReadOnlyList<OutputColumn>? outputColumns = null)
    {
        if (outputColumns is null) return;

        // "*" 没法事先展开,交给后面按 JSON 追加
        var named = outputColumns.Where(c => c.Name != "*").ToList();
        var duplicated = named.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var column in named)
        {
            var index = _columns.Count;
            // 两张表都选了同名列(c.name, a.name)时,用带别名的键区分
            _columns.Add(duplicated.Contains(column.Name) ? column.FetchKey : column.Name);
            foreach (var key in CandidateKeys(column))
            {
                if (!_index.TryGetValue(key, out var list)) _index[key] = list = new List<int>();
                // 同一列被选了多次时每一列都要填;不同列的候选键撞上时先到先得
                if (list.Count == 0 || IsSameSource(named[list[0]], column)) list.Add(index);
            }
        }
    }

    public IReadOnlyList<string> Columns => _columns;

    /// <summary>JSON 属性对应的(第一个)列下标;没见过的属性追加成新列。</summary>
    public int IndexFor(string jsonKey) => IndexesFor(jsonKey)[0];

    /// <summary>JSON 属性要填进的所有列;没见过的属性追加成新列。</summary>
    public IReadOnlyList<int> IndexesFor(string jsonKey)
    {
        if (_index.TryGetValue(jsonKey, out var list)) return list;
        list = new List<int> { _columns.Count };
        _columns.Add(Prettify(jsonKey));
        _index[jsonKey] = list;
        return list;
    }

    private static bool IsSameSource(OutputColumn a, OutputColumn b) =>
        a.FetchKey.Equals(b.FetchKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>一个输出列在 Web API 返回的 JSON 里可能叫什么。</summary>
    private static IEnumerable<string> CandidateKeys(OutputColumn column)
    {
        yield return column.FetchKey;
        if (column.Attribute is null) yield break; // 聚合 / 分组列:只会以别名返回

        var attr = column.Attribute;
        if (column.FetchKey.Equals(attr, StringComparison.OrdinalIgnoreCase))
        {
            // 主表的列;查找列会以 _xxx_value 返回
            yield return $"_{attr}_value";
        }
        else if (column.TableAlias is not null
                 && column.FetchKey.Equals($"{column.TableAlias}.{attr}", StringComparison.OrdinalIgnoreCase))
        {
            // link-entity 的列:alias.attr;点号有时被编码成 _x002e_
            var alias = column.TableAlias;
            yield return $"{alias}._{attr}_value";
            yield return $"{alias}_x002e_{attr}";
            yield return $"{alias}_x002e__{attr}_value";
        }
        // 其余情况是显式别名,JSON 里就是别名本身
    }

    /// <summary>追加列的表头:_xxx_value → xxx,_x002e_ → 点号。</summary>
    internal static string Prettify(string jsonKey)
    {
        var key = jsonKey.Replace("_x002e_", ".");
        var match = LookupKey.Match(key);
        if (!match.Success) return key;
        var alias = match.Groups["alias"];
        return alias.Success ? $"{alias.Value}.{match.Groups["attr"].Value}" : match.Groups["attr"].Value;
    }
}
