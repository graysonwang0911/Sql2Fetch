using System.Xml.Linq;
using Sql2Fetch.Engine;
using Sql2Fetch.Engine.Metadata;
using Xunit;

namespace Sql2Fetch.Engine.Tests;

public class ConversionTests
{
    private static readonly DictionaryMetadataProvider Metadata = new DictionaryMetadataProvider()
        .Add("account", "accountid", "name", "telephone1", "statecode", "createdon", "primarycontactid", "revenue", "ownerid")
        .Add("contact", "contactid", "firstname", "lastname", "parentcustomerid", "emailaddress1", "statecode", "createdon")
        .Add("opportunity", "opportunityid", "name", "estimatedvalue", "parentaccountid", "statecode", "createdon");

    private static void AssertFetchXml(string sql, string expected, IMetadataProvider? metadata = null)
    {
        var result = SqlToFetchXml.Convert(sql, metadata ?? Metadata);
        var actual = XElement.Parse(result.FetchXml);
        var exp = XElement.Parse(expected);
        Assert.True(XNode.DeepEquals(Normalize(exp), Normalize(actual)),
            $"期望:\n{Normalize(exp)}\n实际:\n{Normalize(actual)}");
    }

    private static XElement Normalize(XElement e)
    {
        var copy = new XElement(e);
        foreach (var el in copy.DescendantsAndSelf())
        {
            var attrs = el.Attributes().OrderBy(a => a.Name.LocalName).ToList();
            el.RemoveAttributes();
            el.Add(attrs);
        }
        return copy;
    }

    [Fact]
    public void SimpleSelect() => AssertFetchXml(
        "SELECT accountid, name FROM account",
        @"<fetch><entity name='account'><attribute name='accountid'/><attribute name='name'/></entity></fetch>");

    [Fact]
    public void SelectStar() => AssertFetchXml(
        "SELECT * FROM account",
        @"<fetch><entity name='account'><all-attributes/></entity></fetch>");

    [Fact]
    public void Where() => AssertFetchXml(
        "SELECT name FROM account WHERE name = 'Data8' AND statecode = 0",
        @"<fetch><entity name='account'><attribute name='name'/>
            <filter type='and'>
              <condition attribute='name' operator='eq' value='Data8'/>
              <condition attribute='statecode' operator='eq' value='0'/>
            </filter></entity></fetch>");

    [Fact]
    public void WhereOr() => AssertFetchXml(
        "SELECT name FROM account WHERE name = 'a' OR (name LIKE 'b%' AND telephone1 IS NOT NULL)",
        @"<fetch><entity name='account'><attribute name='name'/>
            <filter type='or'>
              <condition attribute='name' operator='eq' value='a'/>
              <filter type='and'>
                <condition attribute='name' operator='like' value='b%'/>
                <condition attribute='telephone1' operator='not-null'/>
              </filter>
            </filter></entity></fetch>");

    [Fact]
    public void InBetweenNot() => AssertFetchXml(
        "SELECT name FROM account WHERE statecode IN (0, 1) AND revenue BETWEEN 100 AND 200 AND NOT (name = 'x')",
        @"<fetch><entity name='account'><attribute name='name'/>
            <filter type='and'>
              <condition attribute='statecode' operator='in'><value>0</value><value>1</value></condition>
              <condition attribute='revenue' operator='between'><value>100</value><value>200</value></condition>
              <condition attribute='name' operator='ne' value='x'/>
            </filter></entity></fetch>");

    [Fact]
    public void OperatorFunctions() => AssertFetchXml(
        "SELECT name FROM account WHERE createdon = lastxdays(7) AND ownerid = eq_userid()",
        @"<fetch><entity name='account'><attribute name='name'/>
            <filter type='and'>
              <condition attribute='createdon' operator='last-x-days' value='7'/>
              <condition attribute='ownerid' operator='eq-userid'/>
            </filter></entity></fetch>");

    [Fact]
    public void InnerJoin() => AssertFetchXml(
        @"SELECT c.firstname, c.lastname, a.telephone1
          FROM contact AS c INNER JOIN account AS a ON c.parentcustomerid = a.accountid
          WHERE c.firstname = 'Mark' AND a.statecode = 0
          ORDER BY c.createdon DESC",
        @"<fetch><entity name='contact'>
            <attribute name='firstname'/><attribute name='lastname'/>
            <order attribute='createdon' descending='true'/>
            <filter type='and'><condition attribute='firstname' operator='eq' value='Mark'/></filter>
            <link-entity name='account' from='accountid' to='parentcustomerid' link-type='inner' alias='a'>
              <attribute name='telephone1'/>
              <filter type='and'><condition attribute='statecode' operator='eq' value='0'/></filter>
            </link-entity>
          </entity></fetch>");

    [Fact]
    public void LeftJoinFilterGoesToRoot() => AssertFetchXml(
        @"SELECT a.name FROM account a LEFT JOIN contact c ON a.primarycontactid = c.contactid WHERE c.emailaddress1 IS NULL",
        @"<fetch><entity name='account'>
            <attribute name='name'/>
            <filter type='and'><condition entityname='c' attribute='emailaddress1' operator='null'/></filter>
            <link-entity name='contact' from='contactid' to='primarycontactid' link-type='outer' alias='c'/>
          </entity></fetch>");

    [Fact]
    public void RightJoinBecomesLeft() => AssertFetchXml(
        @"SELECT a.name FROM contact c RIGHT JOIN account a ON c.parentcustomerid = a.accountid",
        @"<fetch><entity name='account'>
            <attribute name='name'/>
            <link-entity name='contact' from='parentcustomerid' to='accountid' link-type='outer' alias='c'/>
          </entity></fetch>");

    [Fact]
    public void NestedJoin() => AssertFetchXml(
        @"SELECT o.name FROM account a
          JOIN contact c ON a.primarycontactid = c.contactid
          JOIN opportunity o ON c.contactid = o.parentaccountid",
        @"<fetch><entity name='account'>
            <link-entity name='contact' from='contactid' to='primarycontactid' link-type='inner' alias='c'>
              <link-entity name='opportunity' from='parentaccountid' to='contactid' link-type='inner' alias='o'>
                <attribute name='name'/>
              </link-entity>
            </link-entity>
          </entity></fetch>");

    [Fact]
    public void JoinExtraCriteria() => AssertFetchXml(
        @"SELECT a.name FROM account a JOIN contact c ON a.primarycontactid = c.contactid AND c.statecode = 0",
        @"<fetch><entity name='account'>
            <attribute name='name'/>
            <link-entity name='contact' from='contactid' to='primarycontactid' link-type='inner' alias='c'>
              <filter type='and'><condition attribute='statecode' operator='eq' value='0'/></filter>
            </link-entity>
          </entity></fetch>");

    [Fact]
    public void TopDistinctNoLock() => AssertFetchXml(
        "SELECT DISTINCT TOP 10 name FROM account WITH (NOLOCK)",
        @"<fetch top='10' distinct='true' no-lock='true'><entity name='account'><attribute name='name'/></entity></fetch>");

    [Fact]
    public void OffsetFetch() => AssertFetchXml(
        "SELECT name FROM account ORDER BY name OFFSET 100 ROWS FETCH NEXT 50 ROWS ONLY",
        @"<fetch count='50' page='3'><entity name='account'><attribute name='name'/><order attribute='name'/></entity></fetch>");

    [Fact]
    public void ColumnAlias() => AssertFetchXml(
        "SELECT name AS AccountName FROM account ORDER BY AccountName",
        @"<fetch><entity name='account'><attribute name='name' alias='AccountName'/><order attribute='name'/></entity></fetch>");

    [Fact]
    public void Aggregate() => AssertFetchXml(
        @"SELECT statecode, COUNT(*) AS cnt, SUM(revenue) AS total, COUNT(DISTINCT name) AS names
          FROM account GROUP BY statecode ORDER BY cnt DESC",
        @"<fetch aggregate='true'><entity name='account'>
            <attribute name='statecode' alias='statecode' groupby='true'/>
            <attribute name='accountid' alias='cnt' aggregate='count'/>
            <attribute name='revenue' alias='total' aggregate='sum'/>
            <attribute name='name' alias='names' aggregate='countcolumn' distinct='true'/>
            <order alias='cnt' descending='true'/>
          </entity></fetch>");

    [Fact]
    public void DateGrouping() => AssertFetchXml(
        "SELECT YEAR(createdon) AS yr, DATEPART(month, createdon) AS mth, COUNT(*) AS cnt FROM account GROUP BY YEAR(createdon), DATEPART(month, createdon)",
        @"<fetch aggregate='true'><entity name='account'>
            <attribute name='createdon' alias='yr' groupby='true' dategrouping='year'/>
            <attribute name='createdon' alias='mth' groupby='true' dategrouping='month'/>
            <attribute name='accountid' alias='cnt' aggregate='count'/>
          </entity></fetch>");

    [Fact]
    public void CountStarWithoutMetadataWarns()
    {
        var result = SqlToFetchXml.Convert("SELECT COUNT(*) FROM account");
        Assert.Contains("accountid", result.FetchXml);
        Assert.Contains(result.Warnings, w => w.Contains("COUNT(*)"));
    }

    [Fact]
    public void OutputColumns()
    {
        var result = SqlToFetchXml.Convert(
            "SELECT c.firstname, a.name AS AccountName, a.telephone1 FROM contact c JOIN account a ON c.parentcustomerid = a.accountid", Metadata);
        Assert.Equal(new[] { "firstname", "AccountName", "a.telephone1" }, result.Columns.Select(c => c.FetchKey));
        Assert.Equal("contact", result.EntityLogicalName);
    }

    [Theory]
    [InlineData("SELECT name FROM account WHERE name = NULL", "IS NULL")]
    [InlineData("SELECT name FROM account WHERE LEN(name) > 3", "运算")]
    [InlineData("SELECT name FROM account WHERE accountid IN (SELECT parentaccountid FROM opportunity)", "子查询")]
    [InlineData("SELECT name + 'x' FROM account", "表达式")]
    [InlineData("SELECT name FROM account UNION SELECT firstname FROM contact", "UNION")]
    [InlineData("UPDATE account SET name = 'x'", "SELECT")]
    [InlineData("SELECT name FROM account a FULL OUTER JOIN contact c ON a.accountid = c.parentcustomerid", "FULL OUTER")]
    [InlineData("SELECT nam FROM account", "name")]
    [InlineData("SELECT name FROM accounts", "不存在")]
    [InlineData("SELECT statecode FROM account a JOIN contact c ON a.accountid = c.parentcustomerid", "歧义")]
    public void Unsupported(string sql, string messagePart)
    {
        var ex = Assert.Throws<SqlTranslationException>(() => SqlToFetchXml.Convert(sql, Metadata));
        Assert.Contains(messagePart, ex.Message + " " + ex.Suggestion);
    }

    [Fact]
    public void ParseErrorHasPosition()
    {
        var ex = Assert.Throws<SqlTranslationException>(() => SqlToFetchXml.Convert("SELECT FROM WHERE", Metadata));
        Assert.True(ex.Line >= 1);
    }

    [Fact]
    public void OfflineModeAcceptsUnknownTables()
    {
        var result = SqlToFetchXml.Convert("SELECT mda_name FROM mda_visitplan WHERE mda_date = thismonth()");
        Assert.Contains("mda_visitplan", result.FetchXml);
        Assert.Contains("this-month", result.FetchXml);
    }
}
