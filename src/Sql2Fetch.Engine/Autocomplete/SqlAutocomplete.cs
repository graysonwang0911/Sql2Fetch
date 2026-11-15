using Sql2Fetch.Engine.Metadata;

namespace Sql2Fetch.Engine.Autocomplete;

public enum CompletionKind { Table, Column, Alias, Function, Keyword, Value }

/// <param name="Text">插入的文本</param>
/// <param name="ReplaceLength">要替换掉的、光标前已输入的字符数</param>
/// <param name="Description">右侧灰字说明(显示名、类型等)</param>
public sealed record CompletionItem(string Text, int ReplaceLength, CompletionKind Kind, string? Description);

/// <summary>
/// SQL 自动补全。参照 Sql4Cds 的 Autocomplete:不做完整语法解析,而是从光标向前按词扫描,
/// 根据前一个词判断处于哪个子句,再从 FROM/JOIN 里收集表和别名作为列提示的来源。
/// </summary>
public sealed class SqlAutocomplete
{
    private static readonly HashSet<string> PrevWordTriggers = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "select", "case", "when", "like", "where", "on", "by", "having", "join", "distinct", "not", "in", "between",
    };

    private static readonly string[] DateOperators =
    {
        "today()", "yesterday()", "tomorrow()", "thisweek()", "lastweek()", "nextweek()", "thismonth()", "lastmonth()", "nextmonth()",
        "thisyear()", "lastyear()", "nextyear()", "lastxdays(7)", "nextxdays(7)", "lastxhours(24)", "lastxweeks(4)", "lastxmonths(3)",
        "lastxyears(1)", "olderthanxdays(30)", "olderthanxmonths(6)", "on('2024-01-01')", "onorafter('2024-01-01')", "onorbefore('2024-01-01')",
        "thisfiscalyear()", "lastfiscalyear()", "thisfiscalperiod()",
    };

    private static readonly string[] UserOperators =
    {
        "eq_userid()", "ne_userid()", "eq_userteams()", "eq_useroruserteams()", "eq_useroruserhierarchy()", "eq_useroruserhierarchyandteams()",
        "eq_businessid()", "ne_businessid()",
    };

    private static readonly string[] HierarchyOperators = { "under('guid')", "eq_or_under('guid')", "not_under('guid')", "above('guid')", "eq_or_above('guid')" };

    private readonly IMetadataProvider _metadata;

    public SqlAutocomplete(IMetadataProvider metadata)
    {
        _metadata = metadata;
    }

    /// <summary>当前 FROM 子句涉及的表(别名 → 逻辑名),界面可用来预加载元数据。</summary>
    public IReadOnlyDictionary<string, string> LastTables { get; private set; } = new Dictionary<string, string>();

    /// <param name="text">编辑器全文</param>
    /// <param name="caret">光标偏移(光标在该位置之前的字符之后)</param>
    public IReadOnlyList<CompletionItem> GetSuggestions(string text, int caret)
    {
        var pos = Math.Min(caret, text.Length) - 1;
        if (pos < 0 || InCommentOrStringLiteral(text, pos))
            return Array.Empty<CompletionItem>();

        string? currentWord = null, prevWord = null, prevPrevWord = null;
        foreach (var word in ReverseWords(text, pos))
        {
            if (currentWord is null) currentWord = word;
            else if (prevWord is null) prevWord = word;
            else { prevPrevWord = word; break; }
        }

        // 光标前是空白时 ReverseWords 会先给出一个空词,currentWord 即为 ""
        if (currentWord is null) return Array.Empty<CompletionItem>();

        if (decimal.TryParse(currentWord, out _)) return Array.Empty<CompletionItem>();

        if (prevWord is null)
            return Filter(new[] { Keyword("SELECT", currentWord.Length) }, currentWord);

        switch (prevWord.ToLowerInvariant())
        {
            case "from":
            case "join":
                return TableNames(currentWord);
        }

        // 收集 FROM 子句里的表
        var tables = FindTables(text, pos, out var clause);
        LastTables = tables;

        if (clause == "from" && prevWord == ",")
            return TableNames(currentWord);

        var triggered = prevWord.EndsWith('.') || prevWord.EndsWith(',') || prevWord.EndsWith('(') || prevWord.EndsWith('=')
                        || prevWord.EndsWith('>') || prevWord.EndsWith('<') || prevWord.EndsWith('+') || prevWord.EndsWith('-')
                        || prevWord.EndsWith('*') || prevWord.EndsWith('/')
                        || PrevWordTriggers.Contains(prevWord)
                        || prevPrevWord is not null && prevPrevWord.Equals("top", StringComparison.OrdinalIgnoreCase);

        if (!triggered)
        {
            // 刚写完一张表或别名:提示接下来的关键字
            if (clause == "from" || clause == "join")
                return Filter(new[] { "WHERE", "INNER JOIN", "LEFT JOIN", "ORDER BY", "GROUP BY", "ON" }.Select(k => Keyword(k, currentWord.Length)), currentWord);
            return Array.Empty<CompletionItem>();
        }

        if (tables.Count == 0)
            return Array.Empty<CompletionItem>();

        // alias.col → 那张表的列
        var dot = currentWord.IndexOf('.');
        if (dot >= 0)
        {
            var alias = currentWord[..dot];
            var partial = currentWord[(dot + 1)..];
            if (tables.TryGetValue(alias, out var tableName))
            {
                var entity = _metadata.GetEntity(tableName);
                if (entity is null) return Array.Empty<CompletionItem>();
                return Filter(ColumnsOf(entity, partial.Length, null), partial);
            }
            return Array.Empty<CompletionItem>();
        }

        var items = new List<CompletionItem>();
        var entities = new List<(string Alias, EntityInfo Entity)>();

        foreach (var (alias, tableName) in tables)
        {
            var entity = _metadata.GetEntity(tableName);
            if (entity is null) continue;
            entities.Add((alias, entity));
            if (tables.Count > 1)
                items.Add(new CompletionItem(alias, currentWord.Length, CompletionKind.Alias,
                    alias.Equals(tableName, StringComparison.OrdinalIgnoreCase) ? "表" : $"表 {tableName}"));
        }

        // 列:只在一张表里出现的直接给列名;多张表都有的,给 别名.列名
        var byName = entities
            .SelectMany(e => e.Entity.Attributes.Values.Where(IsReadable).Select(a => (e.Alias, Attr: a)))
            .GroupBy(x => x.Attr.LogicalName, StringComparer.OrdinalIgnoreCase);

        foreach (var g in byName)
        {
            var list = g.ToList();
            if (list.Count == 1)
                items.Add(ColumnItem(list[0].Attr, currentWord.Length, entities.Count > 1 ? list[0].Alias : null, false));
            else
                foreach (var (alias, attr) in list)
                    items.Add(ColumnItem(attr, currentWord.Length, alias, true));
        }

        // WHERE x = 之后:选项集列提示取值,日期/查找列提示 FetchXML 操作符函数
        if ((clause == "where" || clause == "on") && (prevWord == "=" || prevWord == "<>" || prevWord == "!=" || prevWord == "(") && prevPrevWord is not null)
        {
            // col IN ( 的情况 prevPrevWord 是 "in",再往前一个才是列名
            var columnRef = prevWord == "(" ? FindWordBefore(text, pos, 2) : prevPrevWord;
            var attr = columnRef is null ? null : ResolveAttribute(columnRef, tables, entities);
            if (attr is not null)
            {
                if (attr.HasOptions)
                {
                    var optionItems = attr.Options!.Select(o => new CompletionItem(
                        o.Value.ToString(), currentWord.Length, CompletionKind.Value, string.IsNullOrEmpty(o.Label) ? "(无标签)" : o.Label));
                    // 选项值放最前面,直接按值或标签过滤
                    return Filter(optionItems, currentWord);
                }

                var ops = attr.AttributeType switch
                {
                    "DateTime" => DateOperators,
                    "Lookup" or "Owner" or "Customer" or "Uniqueidentifier" => UserOperators.Concat(HierarchyOperators).ToArray(),
                    _ => Array.Empty<string>()
                };
                items.AddRange(ops.Select(o => new CompletionItem(o, currentWord.Length, CompletionKind.Function, "FetchXML 操作符")));
            }
        }

        if (clause == "select" || clause == "where")
            items.AddRange(new[] { "COUNT(*)", "SUM(", "AVG(", "MIN(", "MAX(" }
                .Where(_ => clause == "select")
                .Select(f => new CompletionItem(f, currentWord.Length, CompletionKind.Function, "聚合")));

        return Filter(items.OrderBy(i => i.Kind).ThenBy(i => i.Text, StringComparer.OrdinalIgnoreCase), currentWord);
    }

    private IReadOnlyList<CompletionItem> TableNames(string currentWord) =>
        Filter(_metadata.ListEntities().Select(e => new CompletionItem(e.LogicalName, currentWord.Length, CompletionKind.Table, e.DisplayName)), currentWord);

    private static IEnumerable<CompletionItem> ColumnsOf(EntityInfo entity, int replaceLength, string? alias) =>
        entity.Attributes.Values.Where(IsReadable).OrderBy(a => a.LogicalName).Select(a => ColumnItem(a, replaceLength, alias, false));

    private static CompletionItem ColumnItem(AttributeInfo attr, int replaceLength, string? alias, bool qualify)
    {
        var text = qualify && alias is not null ? $"{alias}.{attr.LogicalName}" : attr.LogicalName;
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(attr.DisplayName) && !attr.DisplayName.Equals(attr.LogicalName, StringComparison.OrdinalIgnoreCase))
            parts.Add(attr.DisplayName);
        parts.Add(attr.HasOptions ? DescribeOptions(attr) : FriendlyType(attr.AttributeType));
        if (alias is not null && !qualify)
            parts.Add($"表 {alias}");
        return new CompletionItem(text, replaceLength, CompletionKind.Column, string.Join(" · ", parts));
    }

    /// <summary>选项集列:类型 + 前几个选项,如 "选项集 [0=有効, 1=無効]"。</summary>
    public static string DescribeOptions(AttributeInfo attr, int max = 4)
    {
        var options = attr.Options!;
        var shown = options.Take(max).Select(o => $"{o.Value}={o.Label ?? "?"}");
        var suffix = options.Count > max ? $", …共 {options.Count} 项" : "";
        return $"{FriendlyType(attr.AttributeType)} [{string.Join(", ", shown)}{suffix}]";
    }

    /// <summary>从 pos 向前数第 n 个词(n=1 是当前词)。</summary>
    private static string? FindWordBefore(string text, int pos, int n)
    {
        var i = 0;
        foreach (var w in ReverseWords(text, pos))
        {
            if (w.Length == 0) continue;
            if (++i == n + 1) return w;
        }
        return null;
    }

    /// <summary>Dataverse 属性类型 → 容易看懂的说明。</summary>
    public static string FriendlyType(string attributeType) => attributeType switch
    {
        "String" => "文本",
        "Memo" => "多行文本",
        "Integer" => "整数",
        "BigInt" => "长整数",
        "Decimal" => "小数",
        "Double" => "浮点数",
        "Money" => "货币",
        "DateTime" => "日期时间",
        "Boolean" => "是/否",
        "Picklist" => "选项集",
        "MultiSelectPicklist" => "多选选项集",
        "State" => "状态",
        "Status" => "状态原因",
        "Lookup" => "查找",
        "Owner" => "所有者",
        "Customer" => "客户(查找)",
        "Uniqueidentifier" => "GUID",
        "EntityName" => "表名",
        "Virtual" => "虚拟列",
        "Image" or "File" => "文件",
        "" => "",
        _ => attributeType,
    };

    private static bool IsReadable(AttributeInfo a) => a.AttributeOf is null;

    private static CompletionItem Keyword(string text, int replaceLength) => new(text, replaceLength, CompletionKind.Keyword, "关键字");

    private AttributeInfo? ResolveAttribute(string reference, IReadOnlyDictionary<string, string> tables, List<(string Alias, EntityInfo Entity)> entities)
    {
        var parts = reference.Split('.');
        if (parts.Length == 2)
        {
            return tables.TryGetValue(parts[0], out var tableName) ? _metadata.GetEntity(tableName)?.FindAttribute(parts[1]) : null;
        }
        var matches = entities.Select(e => e.Entity.FindAttribute(parts[0])).Where(a => a is not null).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>前缀匹配优先;没有前缀匹配时退回包含匹配。</summary>
    private static IReadOnlyList<CompletionItem> Filter(IEnumerable<CompletionItem> items, string currentWord)
    {
        var list = items.ToList();
        if (currentWord.Length == 0) return list;
        var starts = list.Where(i => i.Text.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase)).ToList();
        if (starts.Count > 0) return starts;
        return list.Where(i => i.Text.Contains(currentWord, StringComparison.OrdinalIgnoreCase)
                            || i.Description?.Contains(currentWord, StringComparison.OrdinalIgnoreCase) == true).ToList();
    }

    /// <summary>找到当前查询的 FROM 子句,解析出 别名 → 表名。</summary>
    private static Dictionary<string, string> FindTables(string text, int pos, out string? clause)
    {
        var words = new List<string>();
        var foundFrom = false;
        var foundQueryStart = false;
        clause = null;

        foreach (var word in ReverseWords(text, pos))
        {
            switch (word.ToLowerInvariant())
            {
                case "from": foundFrom = true; clause ??= "from"; break;
                case "select": foundQueryStart = true; clause ??= "select"; break;
                case "join": clause ??= "join"; words.Insert(0, word); break;
                case "on": clause ??= "on"; words.Insert(0, word); break;
                case "where": words.Clear(); clause ??= "where"; break;
                case "order": case "group": case "having": words.Clear(); clause ??= word.ToLowerInvariant(); break;
                case "by": break;
                default:
                    if (word.Length > 0) words.Insert(0, word);
                    break;
            }
            if (foundFrom || foundQueryStart) break;
        }

        if (!foundFrom)
        {
            // 光标在 SELECT 列表里:往后找 FROM
            var nextWords = new List<string>();
            var ended = false;
            foreach (var word in Words(text, pos + 1))
            {
                switch (word.ToLowerInvariant())
                {
                    case "from": foundFrom = true; break;
                    case "where": case "group": case "order": case "select": case "having": ended = true; break;
                    default: if (foundFrom) nextWords.Add(word); break;
                }
                if (ended) break;
            }
            if (foundFrom) words = nextWords;
        }

        var tables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!foundFrom) return tables;

        for (var i = 0; i < words.Count; i++)
        {
            var tableName = words[i];
            if (IsJoinKeyword(tableName) || tableName == "," || tableName.Equals("on", StringComparison.OrdinalIgnoreCase))
                continue;

            var alias = tableName;
            if (i < words.Count - 1)
            {
                if (words[i + 1].Equals("as", StringComparison.OrdinalIgnoreCase) && i < words.Count - 2)
                {
                    alias = words[i + 2];
                    i += 2;
                }
                else if (!IsJoinKeyword(words[i + 1]) && !words[i + 1].Equals("on", StringComparison.OrdinalIgnoreCase) && words[i + 1] != "," && !words[i + 1].Equals("with", StringComparison.OrdinalIgnoreCase))
                {
                    alias = words[i + 1];
                    i++;
                }
            }

            tables[alias] = tableName.ToLowerInvariant();

            // 跳过 ON 条件,直到下一个 JOIN 或逗号
            while (i < words.Count && !words[i].Equals("join", StringComparison.OrdinalIgnoreCase) && words[i] != ",")
                i++;
        }

        return tables;
    }

    private static bool IsJoinKeyword(string w) =>
        w.Equals("join", StringComparison.OrdinalIgnoreCase) || w.Equals("inner", StringComparison.OrdinalIgnoreCase)
        || w.Equals("left", StringComparison.OrdinalIgnoreCase) || w.Equals("right", StringComparison.OrdinalIgnoreCase)
        || w.Equals("outer", StringComparison.OrdinalIgnoreCase) || w.Equals("full", StringComparison.OrdinalIgnoreCase)
        || w.Equals("cross", StringComparison.OrdinalIgnoreCase);

    private static bool IsWordChar(char c) => !char.IsWhiteSpace(c) && (!char.IsPunctuation(c) || c is '.' or '_' or '[' or ']' or '@');

    /// <summary>从 end 向前逐词枚举;标点(除 . _ [ ] @)作为独立的词返回。</summary>
    internal static IEnumerable<string> ReverseWords(string text, int end)
    {
        var inWord = true;
        var inQuote = false;

        for (var i = end; i >= 0; i--)
        {
            var c = text[i];
            if (!inQuote && !IsWordChar(c))
            {
                if (inWord)
                {
                    yield return text.Substring(i + 1, end - i);
                    inWord = false;
                }
                if (char.IsPunctuation(c) || char.IsSymbol(c))
                    yield return c.ToString();
            }
            else if (inWord && c == ']') inQuote = true;
            else if (inQuote && c == '[') inQuote = false;
            else if (!inWord)
            {
                end = i;
                inWord = true;
            }
        }

        if (inWord) yield return text[..(end + 1)];
    }

    /// <summary>从 start 向后逐词枚举。</summary>
    internal static IEnumerable<string> Words(string text, int start)
    {
        var inWord = false;
        var inQuote = false;
        var wordStart = start;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (!inQuote && !IsWordChar(c))
            {
                if (inWord)
                {
                    yield return text[wordStart..i];
                    inWord = false;
                }
                if (char.IsPunctuation(c) || char.IsSymbol(c))
                    yield return c.ToString();
            }
            else if (inQuote && c == ']') inQuote = false;
            else if (!inWord)
            {
                wordStart = i;
                inWord = true;
                inQuote = c == '[';
            }
            else if (!inQuote && c == '[') inQuote = true;
        }

        if (inWord) yield return text[wordStart..];
    }

    internal static bool InCommentOrStringLiteral(string text, int pos)
    {
        var inLineComment = false;
        var commentDepth = 0;
        var inQuotes = false;

        for (var i = 0; i <= pos && i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n') inLineComment = false;
            else if (!inQuotes && commentDepth == 0 && c == '-' && i > 0 && text[i - 1] == '-') inLineComment = true;
            else if (!inQuotes && !inLineComment && c == '/' && i + 1 < text.Length && text[i + 1] == '*') commentDepth++;
            else if (!inQuotes && !inLineComment && c == '/' && i > 0 && text[i - 1] == '*' && commentDepth > 0) commentDepth--;
            else if (c == '\'' && !inLineComment && commentDepth == 0) inQuotes = !inQuotes;
        }

        return inLineComment || commentDepth > 0 || inQuotes;
    }
}
