using System.Xml.Linq;
using Sql2Fetch.Dataverse;
using Sql2Fetch.Engine;
using Sql2Fetch.Engine.Metadata;
using Xunit;

namespace Sql2Fetch.Engine.Tests;

/// <summary>TOP+HAVING、ORDER BY 聚合函数、FETCH 0、重复列 这几类边界情况。</summary>
public class EdgeCaseTests
{
    private static readonly DictionaryMetadataProvider Metadata = new DictionaryMetadataProvider()
        .Add("account", "accountid", "name", "revenue", "statecode", "createdon")
        .Add("contact", "contactid", "firstname", "name", "parentcustomerid");

    private static ConversionResult Convert(string sql) => SqlToFetchXml.Convert(sql, Metadata);
    private static XElement Fetch(ConversionResult r) => XElement.Parse(r.FetchXml);

    // ---- TOP / OFFSET 和 HAVING ----

    [Fact]
    public void TopWithHavingIsAppliedLocallyAfterTheFilter()
    {
        var r = Convert("SELECT TOP 5 name, COUNT(*) AS c FROM account GROUP BY name HAVING COUNT(*) > 1");
        Assert.Null(Fetch(r).Attribute("top"));
        Assert.Equal(5, r.LocalTop);
        Assert.Equal(0, r.LocalOffset);
        Assert.NotNull(r.LocalFilter);
        Assert.Contains(r.Warnings, w => w.Contains("TOP"));
    }

    [Fact]
    public void OffsetFetchWithHavingIsAppliedLocally()
    {
        var r = Convert("SELECT name, COUNT(*) AS c FROM account GROUP BY name HAVING COUNT(*) > 1 ORDER BY name OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY");
        Assert.Null(Fetch(r).Attribute("page"));
        Assert.Null(Fetch(r).Attribute("count"));
        Assert.Equal(5, r.LocalTop);
        Assert.Equal(10, r.LocalOffset);
    }

    [Fact]
    public void TopWithoutHavingStillGoesToTheServer()
    {
        var r = Convert("SELECT TOP 5 name, COUNT(*) AS c FROM account GROUP BY name");
        Assert.Equal("5", Fetch(r).Attribute("top")?.Value);
        Assert.Null(r.LocalTop);
    }

    // ---- TOP 超出 FetchXML 的范围 ----

    [Theory]
    [InlineData(1)]
    [InlineData(5000)]
    public void TopWithinTheFetchXmlRangeGoesToTheServer(int top)
    {
        var r = Convert($"SELECT TOP {top} name FROM account");
        Assert.Equal(top.ToString(), Fetch(r).Attribute("top")?.Value);
        Assert.Null(r.LocalTop);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("TOP"));
    }

    [Theory]
    [InlineData(5001)]
    [InlineData(10000)]
    public void TopAboveTheFetchXmlLimitIsAppliedLocally(int top)
    {
        var r = Convert($"SELECT TOP {top} name FROM account");
        Assert.Null(Fetch(r).Attribute("top"));
        Assert.Equal(top, r.LocalTop);
        Assert.Contains(r.Warnings, w => w.Contains("5000"));
    }

    [Fact]
    public void TopZeroIsAppliedLocally()
    {
        var r = Convert("SELECT TOP 0 name FROM account");
        Assert.Null(Fetch(r).Attribute("top"));
        Assert.Equal(0, r.LocalTop);
        Assert.Contains(r.Warnings, w => w.Contains("TOP 0"));
    }

    // ---- ORDER BY 聚合函数 ----

    [Fact]
    public void OrderByAggregateFunctionUsesTheMatchingSelectAlias()
    {
        var r = Convert("SELECT name, COUNT(*) AS c FROM account GROUP BY name ORDER BY COUNT(*) DESC");
        var order = Fetch(r).Descendants("order").Single();
        Assert.Equal("c", order.Attribute("alias")?.Value);
        Assert.Equal("true", order.Attribute("descending")?.Value);
        Assert.Empty(r.HiddenColumns);
    }

    [Fact]
    public void OrderByAggregateNotInSelectAddsAHiddenColumn()
    {
        var r = Convert("SELECT name FROM account GROUP BY name ORDER BY SUM(revenue) DESC");
        var hidden = Assert.Single(r.HiddenColumns);
        Assert.Contains(Fetch(r).Descendants("attribute"),
            a => a.Attribute("alias")?.Value == hidden && a.Attribute("aggregate")?.Value == "sum" && a.Attribute("name")?.Value == "revenue");
        Assert.Equal(hidden, Fetch(r).Descendants("order").Single().Attribute("alias")?.Value);
        Assert.Equal(new[] { "name" }, r.Columns.Select(c => c.Name));
    }

    [Fact]
    public void OrderByDateGroupingFunction()
    {
        var r = Convert("SELECT YEAR(createdon) AS yr, COUNT(*) AS c FROM account GROUP BY YEAR(createdon) ORDER BY YEAR(createdon)");
        Assert.Equal("yr", Fetch(r).Descendants("order").Single().Attribute("alias")?.Value);
    }

    // ---- FETCH NEXT 0 ----

    [Fact]
    public void FetchNextZeroIsARegularError()
    {
        var ex = Assert.Throws<SqlTranslationException>(() =>
            Convert("SELECT name FROM account ORDER BY name OFFSET 0 ROWS FETCH NEXT 0 ROWS ONLY"));
        Assert.Contains("大于 0", ex.Message);
    }

    // ---- 重复列 ----

    [Fact]
    public void RepeatedAggregateGetsDistinctAliases()
    {
        var r = Convert("SELECT COUNT(*), COUNT(*) FROM account");
        var aliases = Fetch(r).Descendants("attribute").Select(a => a.Attribute("alias")!.Value).ToArray();
        Assert.Equal(new[] { "count", "count_2" }, aliases);
        Assert.Equal(aliases, r.Columns.Select(c => c.FetchKey));
    }

    [Fact]
    public void DuplicateExplicitAliasIsRejected()
    {
        var ex = Assert.Throws<SqlTranslationException>(() => Convert("SELECT COUNT(*) AS c, SUM(revenue) AS c FROM account"));
        Assert.Contains("重复", ex.Message);
    }

    [Fact]
    public void SameNamedGroupByColumnsFromTwoTablesGetDistinctAliases()
    {
        var r = Convert("SELECT a.name, c.name, COUNT(*) AS n FROM account a JOIN contact c ON c.parentcustomerid = a.accountid GROUP BY a.name, c.name");
        var aliases = Fetch(r).Descendants("attribute").Where(a => a.Attribute("groupby") is not null)
            .Select(a => a.Attribute("alias")!.Value).ToArray();
        Assert.Equal(new[] { "name", "name_2" }, aliases);
    }

    [Fact]
    public void SameColumnSelectedTwiceFillsBothResultColumns()
    {
        var map = new ResultColumnMap(Convert("SELECT name, name FROM account").Columns);
        Assert.Equal(new[] { "name", "name" }, map.Columns);
        Assert.Equal(new[] { 0, 1 }, map.IndexesFor("name"));
    }
}
