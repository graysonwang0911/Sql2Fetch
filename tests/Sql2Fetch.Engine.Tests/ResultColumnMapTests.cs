using Sql2Fetch.Dataverse;
using Sql2Fetch.Engine;
using Sql2Fetch.Engine.Metadata;
using Xunit;

namespace Sql2Fetch.Engine.Tests;

public class ResultColumnMapTests
{
    private static readonly DictionaryMetadataProvider Metadata = new DictionaryMetadataProvider()
        .Add("account", "accountid", "name", "telephone1", "primarycontactid", "revenue")
        .Add("contact", "contactid", "firstname", "name", "parentcustomerid");

    private static ResultColumnMap MapFor(string sql) => new(SqlToFetchXml.Convert(sql, Metadata).Columns);

    [Fact]
    public void ColumnsFollowSelectOrderEvenBeforeAnyRow()
    {
        var map = MapFor("SELECT telephone1, name, primarycontactid FROM account");
        Assert.Equal(new[] { "telephone1", "name", "primarycontactid" }, map.Columns);
    }

    [Fact]
    public void LookupValueKeyMapsToTheSelectedColumn()
    {
        var map = MapFor("SELECT name, primarycontactid FROM account");
        Assert.Equal(1, map.IndexFor("_primarycontactid_value"));
        Assert.Equal(0, map.IndexFor("name"));
        Assert.Equal(2, map.Columns.Count);
    }

    [Fact]
    public void LinkEntityColumnsMapByAliasAndKeepSqlNames()
    {
        var map = MapFor("SELECT c.firstname, a.telephone1, a.primarycontactid FROM contact c JOIN account a ON c.parentcustomerid = a.accountid");
        Assert.Equal(new[] { "firstname", "telephone1", "primarycontactid" }, map.Columns);
        Assert.Equal(1, map.IndexFor("a.telephone1"));
        Assert.Equal(1, map.IndexFor("a_x002e_telephone1"));
        Assert.Equal(2, map.IndexFor("a._primarycontactid_value"));
    }

    [Fact]
    public void ExplicitAliasIsTheHeaderAndTheKey()
    {
        var map = MapFor("SELECT name AS AccountName FROM account");
        Assert.Equal(new[] { "AccountName" }, map.Columns);
        Assert.Equal(0, map.IndexFor("AccountName"));
    }

    [Fact]
    public void SameNameFromTwoTablesUsesQualifiedHeaders()
    {
        var map = MapFor("SELECT c.name, a.name FROM contact c JOIN account a ON c.parentcustomerid = a.accountid");
        Assert.Equal(new[] { "name", "a.name" }, map.Columns);
        Assert.Equal(0, map.IndexFor("name"));
        Assert.Equal(1, map.IndexFor("a.name"));
    }

    [Fact]
    public void AggregateColumnsMapByAlias()
    {
        var map = MapFor("SELECT name, COUNT(*) AS cnt FROM account GROUP BY name");
        Assert.Equal(new[] { "name", "cnt" }, map.Columns);
        Assert.Equal(1, map.IndexFor("cnt"));
    }

    [Fact]
    public void SelectStarAppendsColumnsAsTheyAppearWithReadableHeaders()
    {
        var map = MapFor("SELECT * FROM account");
        Assert.Empty(map.Columns);
        Assert.Equal(0, map.IndexFor("name"));
        Assert.Equal(1, map.IndexFor("_primarycontactid_value"));
        Assert.Equal(0, map.IndexFor("name"));
        Assert.Equal(new[] { "name", "primarycontactid" }, map.Columns);
    }

    [Fact]
    public void WithoutOutputColumnsBehavesLikePlainDiscovery()
    {
        var map = new ResultColumnMap();
        Assert.Equal(0, map.IndexFor("b"));
        Assert.Equal(1, map.IndexFor("a"));
        Assert.Equal(new[] { "b", "a" }, map.Columns);
    }
}
