using Sql2Fetch.Engine;
using Sql2Fetch.Engine.Having;
using Sql2Fetch.Engine.Metadata;
using Xunit;

namespace Sql2Fetch.Engine.Tests;

public class HavingTests
{
    private static readonly DictionaryMetadataProvider Metadata = new DictionaryMetadataProvider()
        .Add("account", "accountid", "name", "revenue", "statecode", "ownerid", "createdon");

    private static Func<string, string?> Row(params (string Key, string? Value)[] values) =>
        k => values.FirstOrDefault(v => v.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value;

    [Fact]
    public void HavingOnSelectAlias()
    {
        var r = SqlToFetchXml.Convert("SELECT ownerid, COUNT(*) AS cnt FROM account GROUP BY ownerid HAVING cnt > 10", Metadata);
        Assert.NotNull(r.LocalFilter);
        Assert.Equal("cnt > 10", r.LocalFilterText);
        Assert.Empty(r.HiddenColumns);
        Assert.DoesNotContain("having", r.FetchXml);
        Assert.True(r.LocalFilter!.Evaluate(Row(("cnt", "11"))));
        Assert.False(r.LocalFilter.Evaluate(Row(("cnt", "10"))));
        Assert.False(r.LocalFilter.Evaluate(Row(("cnt", null))));
        Assert.Contains(r.Warnings, w => w.Contains("本地过滤"));
    }

    [Fact]
    public void HavingOnAggregateExpressionMatchesSelect()
    {
        var r = SqlToFetchXml.Convert("SELECT ownerid, COUNT(*) AS cnt FROM account GROUP BY ownerid HAVING COUNT(*) > 1", Metadata);
        Assert.Empty(r.HiddenColumns);
        Assert.Equal("cnt gt 1", r.LocalFilter!.ToString());
    }

    [Fact]
    public void HavingAddsHiddenAggregate()
    {
        var r = SqlToFetchXml.Convert("SELECT name, COUNT(*) AS cnt FROM account GROUP BY name HAVING SUM(revenue) > 1000000", Metadata);
        Assert.Equal(new[] { "having_1" }, r.HiddenColumns);
        Assert.Contains("alias=\"having_1\" aggregate=\"sum\"", r.FetchXml);
        Assert.Equal(new[] { "name", "cnt" }, r.Columns.Select(c => c.Name));
        Assert.True(r.LocalFilter!.Evaluate(Row(("having_1", "1000001"))));
    }

    [Fact]
    public void HavingComplexPredicate()
    {
        var r = SqlToFetchXml.Convert(
            "SELECT statecode, COUNT(*) AS cnt, SUM(revenue) AS total FROM account GROUP BY statecode HAVING (cnt BETWEEN 2 AND 5 OR total >= 100) AND statecode IN (0, 1) AND NOT total IS NULL",
            Metadata);
        var f = r.LocalFilter!;
        Assert.True(f.Evaluate(Row(("cnt", "3"), ("total", "1"), ("statecode", "0"))));
        Assert.True(f.Evaluate(Row(("cnt", "9"), ("total", "100"), ("statecode", "1"))));
        Assert.False(f.Evaluate(Row(("cnt", "9"), ("total", "99"), ("statecode", "1"))));
        Assert.False(f.Evaluate(Row(("cnt", "3"), ("total", "1"), ("statecode", "2"))));
        Assert.False(f.Evaluate(Row(("cnt", "3"), ("total", null), ("statecode", "0"))));
    }

    [Fact]
    public void HavingLiteralOnLeftFlips()
    {
        var r = SqlToFetchXml.Convert("SELECT ownerid, COUNT(*) AS cnt FROM account GROUP BY ownerid HAVING 10 < cnt", Metadata);
        Assert.Equal("cnt gt 10", r.LocalFilter!.ToString());
    }

    [Fact]
    public void HavingOnGroupByColumnAndDateGrouping()
    {
        var r = SqlToFetchXml.Convert(
            "SELECT YEAR(createdon) AS yr, COUNT(*) AS cnt FROM account GROUP BY YEAR(createdon) HAVING YEAR(createdon) >= 2024 AND yr <> 2025",
            Metadata);
        Assert.Equal("(yr ge 2024 AND yr ne 2025)", r.LocalFilter!.ToString());
    }

    [Theory]
    [InlineData("SELECT name FROM account HAVING COUNT(*) > 1", "GROUP BY")]
    [InlineData("SELECT ownerid, COUNT(*) AS cnt FROM account GROUP BY ownerid HAVING name = 'x'", "必须是")]
    [InlineData("SELECT ownerid, COUNT(*) AS cnt FROM account GROUP BY ownerid HAVING cnt IN (SELECT 1)", "子查询")]
    public void HavingErrors(string sql, string messagePart)
    {
        var ex = Assert.Throws<SqlTranslationException>(() => SqlToFetchXml.Convert(sql, Metadata));
        Assert.Contains(messagePart, ex.Message + " " + ex.Suggestion);
    }

    [Fact]
    public void CompareHandlesTypes()
    {
        var p = new ComparePredicate { Column = "v", Operator = "gt", Value = "9" };
        Assert.True(p.Evaluate(Row(("v", "10"))));          // 数字比较,不是字符串比较("10" < "9")
        var d = new ComparePredicate { Column = "v", Operator = "ge", Value = "2024-01-01" };
        Assert.True(d.Evaluate(Row(("v", "2024-06-01T00:00:00Z"))));
        var s = new ComparePredicate { Column = "v", Operator = "eq", Value = "Abc" };
        Assert.True(s.Evaluate(Row(("v", "abc"))));
    }
}
