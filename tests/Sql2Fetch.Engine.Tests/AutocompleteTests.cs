using Sql2Fetch.Engine.Autocomplete;
using Sql2Fetch.Engine.Metadata;
using Xunit;

namespace Sql2Fetch.Engine.Tests;

public class AutocompleteTests
{
    private static readonly DictionaryMetadataProvider Metadata = new DictionaryMetadataProvider()
        .Add("account", "accountid", "name", "telephone1", "statecode", "createdon", "primarycontactid")
        .Add("contact", "contactid", "firstname", "lastname", "parentcustomerid", "statecode", "createdon");

    private static IReadOnlyList<CompletionItem> Suggest(string sqlWithCaret)
    {
        var caret = sqlWithCaret.IndexOf('|');
        var sql = sqlWithCaret.Remove(caret, 1);
        return new SqlAutocomplete(Metadata).GetSuggestions(sql, caret);
    }

    private static string[] Texts(string sqlWithCaret) => Suggest(sqlWithCaret).Select(i => i.Text).ToArray();

    [Fact]
    public void AfterFromListsTables() =>
        Assert.Equal(new[] { "account", "contact" }, Texts("SELECT name FROM |"));

    [Fact]
    public void AfterFromFiltersByPrefix() =>
        Assert.Equal(new[] { "contact" }, Texts("SELECT name FROM co|"));

    [Fact]
    public void AfterJoinListsTables() =>
        Assert.Contains("contact", Texts("SELECT name FROM account a JOIN |"));

    [Fact]
    public void AliasDotListsThatTablesColumns()
    {
        var items = Texts("SELECT a.| FROM account a JOIN contact c ON a.primarycontactid = c.contactid");
        Assert.Contains("name", items);
        Assert.Contains("telephone1", items);
        Assert.DoesNotContain("firstname", items);
    }

    [Fact]
    public void AliasDotWithPrefix()
    {
        var items = Suggest("SELECT name FROM account a WHERE a.te|");
        Assert.Single(items);
        Assert.Equal("telephone1", items[0].Text);
        Assert.Equal(2, items[0].ReplaceLength);
    }

    [Fact]
    public void SingleTableColumnsInSelectBeforeFrom()
    {
        var items = Texts("SELECT na| FROM account");
        Assert.Equal(new[] { "name" }, items);
    }

    [Fact]
    public void WhereClauseSingleTable()
    {
        var items = Texts("SELECT name FROM account WHERE |");
        Assert.Contains("telephone1", items);
        Assert.Contains("statecode", items);
    }

    [Fact]
    public void MultiTableQualifiesDuplicateColumns()
    {
        var items = Texts("SELECT | FROM account a JOIN contact c ON a.primarycontactid = c.contactid");
        Assert.Contains("a", items);
        Assert.Contains("c", items);
        Assert.Contains("name", items);          // 只有 account 有
        Assert.Contains("a.statecode", items);   // 两张表都有 → 带别名
        Assert.Contains("c.statecode", items);
        Assert.DoesNotContain("statecode", items);
    }

    [Fact]
    public void DateColumnEqualsSuggestsOperators()
    {
        var provider = new DictionaryMetadataProvider();
        provider.Add("account", "accountid", "name");
        var meta = new DictionaryMetadataProvider();
        // 用自定义类型构造
        var entity = new EntityInfo("account", "accounts", "accountid", "name", new Dictionary<string, AttributeInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["accountid"] = new("accountid", "Uniqueidentifier", null, null),
            ["createdon"] = new("createdon", "DateTime", null, "作成日"),
            ["ownerid"] = new("ownerid", "Owner", null, null),
        });
        var typed = new TypedProvider(entity);
        var ac = new SqlAutocomplete(typed);

        var dateItems = ac.GetSuggestions("SELECT name FROM account WHERE createdon = ", "SELECT name FROM account WHERE createdon = ".Length);
        Assert.Contains(dateItems, i => i.Text == "lastxdays(7)");

        var ownerItems = ac.GetSuggestions("SELECT name FROM account WHERE ownerid = ", "SELECT name FROM account WHERE ownerid = ".Length);
        Assert.Contains(ownerItems, i => i.Text == "eq_userid()");
        Assert.DoesNotContain(ownerItems, i => i.Text == "lastxdays(7)");
    }

    [Fact]
    public void OptionSetColumnShowsOptionsAndValues()
    {
        var entity = new EntityInfo("account", "accounts", "accountid", "name", new Dictionary<string, AttributeInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["accountid"] = new("accountid", "Uniqueidentifier", null, null),
            ["statecode"] = new("statecode", "State", null, "状态", new[] { new OptionInfo(0, "有効"), new OptionInfo(1, "無効") }),
            ["industrycode"] = new("industrycode", "Picklist", null, "业种", Enumerable.Range(1, 6).Select(i => new OptionInfo(i, $"业种{i}")).ToList()),
        });
        var ac = new SqlAutocomplete(new TypedProvider(entity));

        // 列提示的第二行直接列出选项
        var cols = ac.GetSuggestions("SELECT name FROM account WHERE ", "SELECT name FROM account WHERE ".Length);
        var state = cols.Single(c => c.Text == "statecode");
        Assert.Equal("状态 · 状态 [0=有効, 1=無効]", state.Description);
        var industry = cols.Single(c => c.Text == "industrycode");
        Assert.Contains("…共 6 项", industry.Description);

        // = 之后提示取值
        var sql = "SELECT name FROM account WHERE statecode = ";
        var values = ac.GetSuggestions(sql, sql.Length);
        Assert.Equal(new[] { "0", "1" }, values.Select(v => v.Text));
        Assert.Equal("無効", values[1].Description);
        Assert.All(values, v => Assert.Equal(CompletionKind.Value, v.Kind));

        // 按标签过滤
        sql = "SELECT name FROM account WHERE statecode = 無";
        values = ac.GetSuggestions(sql, sql.Length);
        Assert.Single(values);
        Assert.Equal("1", values[0].Text);

        // IN ( 之后也提示
        sql = "SELECT name FROM account WHERE statecode IN (";
        values = ac.GetSuggestions(sql, sql.Length);
        Assert.Equal(new[] { "0", "1" }, values.Select(v => v.Text));
    }

    [Theory]
    [InlineData("SELECT name FROM account WHERE name = 'ab|")]
    [InlineData("SELECT name -- from acc|")]
    [InlineData("SELECT name FROM account WHERE statecode = 1|")]
    public void NoSuggestionsInLiteralsCommentsOrNumbers(string sql) =>
        Assert.Empty(Suggest(sql));

    [Fact]
    public void AfterTableSuggestsKeywords() =>
        Assert.Contains("WHERE", Texts("SELECT name FROM account a |"));

    private sealed class TypedProvider : IMetadataProvider
    {
        private readonly EntityInfo _entity;
        public TypedProvider(EntityInfo entity) => _entity = entity;
        public EntityInfo? GetEntity(string logicalName) => logicalName.Equals(_entity.LogicalName, StringComparison.OrdinalIgnoreCase) ? _entity : null;
        public IEnumerable<EntityListItem> ListEntities() => new[] { new EntityListItem(_entity.LogicalName, null) };
    }
}
