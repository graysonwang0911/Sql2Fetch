using System.Globalization;

namespace Sql2Fetch.Engine.Having;

/// <summary>
/// 在客户端内存里对结果行求值的谓词。HAVING 没有 FetchXML 等价物,只能这样做。
/// 取值函数按 FetchXML 的列别名拿到原始值(数字/字符串文本),null 表示该列为空。
/// </summary>
public abstract class RowPredicate
{
    public abstract bool Evaluate(Func<string, string?> getValue);

    /// <summary>SQL 语义:和 NULL 比较永远为 false。</summary>
    protected static bool Compare(string? left, string op, string right)
    {
        if (left is null) return false;

        int c;
        if (decimal.TryParse(left, NumberStyles.Any, CultureInfo.InvariantCulture, out var ln)
            && decimal.TryParse(right, NumberStyles.Any, CultureInfo.InvariantCulture, out var rn))
            c = ln.CompareTo(rn);
        else if (DateTime.TryParse(left, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ld)
                 && DateTime.TryParse(right, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var rd))
            c = ld.CompareTo(rd);
        else if (bool.TryParse(left, out var lb) && (bool.TryParse(right, out var rb) || TryBoolFromNumber(right, out rb)))
            c = lb.CompareTo(rb);
        else
            c = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);

        return op switch
        {
            "eq" => c == 0,
            "ne" => c != 0,
            "lt" => c < 0,
            "le" => c <= 0,
            "gt" => c > 0,
            "ge" => c >= 0,
            _ => throw new InvalidOperationException(op),
        };
    }

    private static bool TryBoolFromNumber(string s, out bool value)
    {
        value = s == "1";
        return s is "0" or "1";
    }
}

public sealed class AndPredicate : RowPredicate
{
    public List<RowPredicate> Items { get; } = new();
    public override bool Evaluate(Func<string, string?> getValue) => Items.All(i => i.Evaluate(getValue));
    public override string ToString() => "(" + string.Join(" AND ", Items) + ")";
}

public sealed class OrPredicate : RowPredicate
{
    public List<RowPredicate> Items { get; } = new();
    public override bool Evaluate(Func<string, string?> getValue) => Items.Any(i => i.Evaluate(getValue));
    public override string ToString() => "(" + string.Join(" OR ", Items) + ")";
}

public sealed class NotPredicate : RowPredicate
{
    public required RowPredicate Inner { get; init; }
    public override bool Evaluate(Func<string, string?> getValue) => !Inner.Evaluate(getValue);
    public override string ToString() => "NOT " + Inner;
}

public sealed class ComparePredicate : RowPredicate
{
    public required string Column { get; init; }
    public required string Operator { get; init; }
    public required string Value { get; init; }
    public override bool Evaluate(Func<string, string?> getValue) => Compare(getValue(Column), Operator, Value);
    public override string ToString() => $"{Column} {Operator} {Value}";
}

public sealed class IsNullPredicate : RowPredicate
{
    public required string Column { get; init; }
    public bool Not { get; init; }
    public override bool Evaluate(Func<string, string?> getValue) => getValue(Column) is null != Not;
    public override string ToString() => $"{Column} IS {(Not ? "NOT " : "")}NULL";
}

public sealed class InPredicate : RowPredicate
{
    public required string Column { get; init; }
    public required IReadOnlyList<string> Values { get; init; }
    public bool Not { get; init; }
    public override bool Evaluate(Func<string, string?> getValue)
    {
        var v = getValue(Column);
        if (v is null) return false;
        return Values.Any(x => Compare(v, "eq", x)) != Not;
    }
    public override string ToString() => $"{Column} {(Not ? "NOT " : "")}IN ({string.Join(", ", Values)})";
}

public sealed class BetweenPredicate : RowPredicate
{
    public required string Column { get; init; }
    public required string Low { get; init; }
    public required string High { get; init; }
    public bool Not { get; init; }
    public override bool Evaluate(Func<string, string?> getValue)
    {
        var v = getValue(Column);
        if (v is null) return false;
        return (Compare(v, "ge", Low) && Compare(v, "le", High)) != Not;
    }
    public override string ToString() => $"{Column} {(Not ? "NOT " : "")}BETWEEN {Low} AND {High}";
}
