using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sql2Fetch.Dataverse;
using Sql2Fetch.Engine;

namespace Sql2Fetch.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sql2fetch", "settings.json");

    private DataverseConnection? _connection;
    private DataverseMetadataProvider? _metadata;
    private ConversionResult? _lastResult;
    private string? _lastTranslatedSql;
    private CancellationTokenSource? _connectCts;

    public MainViewModel()
    {
        LoadSettings();
    }

    [ObservableProperty] public partial string EnvironmentUrl { get; set; } = "";
    /// <summary>登录方式:0 = 浏览器登录(OAuth/MFA),1 = 用户名密码,2 = 设备码。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPasswordMode))]
    public partial int AuthModeIndex { get; set; }
    public bool IsPasswordMode => AuthModeIndex == (int)AuthMode.UsernamePassword;
    [ObservableProperty] public partial string UserName { get; set; } = "";
    /// <summary>只在内存里,不落盘;连接成功后清空。</summary>
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string Sql { get; set; } = "SELECT TOP 10 name, telephone1, createdon\nFROM account\nWHERE statecode = 0\nORDER BY createdon DESC";
    [ObservableProperty] public partial string FetchXml { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "离线模式:可以翻译,不校验表名列名;连接后可校验并执行";
    [ObservableProperty] public partial string Warnings { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRefreshTables))]
    public partial bool IsConnected { get; set; }

    /// <summary>正在手动刷新表清单。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRefreshTables))]
    public partial bool IsRefreshingTables { get; set; }

    public bool CanRefreshTables => IsConnected && !IsRefreshingTables;

    /// <summary>
    /// 手动刷新:重新读取环境里的表清单(新建/删除的表、显示名、类型)和各表记录数。
    /// 表清单只在连接时和点这个按钮时加载,平时不会自动更新。
    /// </summary>
    [RelayCommand]
    private async Task RefreshTablesAsync()
    {
        var metadata = _metadata;
        if (metadata is null || IsRefreshingTables) return;

        IsRefreshingTables = true;
        IsError = false;
        Status = "正在刷新表清单...";
        try
        {
            // 失败时 metadata 里的旧清单保持不变
            await metadata.LoadEntitiesAsync();
            if (!ReferenceEquals(metadata, _metadata)) return; // 期间断开了连接

            var selectedName = SelectedTable?.LogicalName;
            var before = _allTables.Count;
            _allTables.Clear();
            _allTables.AddRange(metadata.Entities
                .OrderBy(e => e.LogicalName, StringComparer.OrdinalIgnoreCase)
                .Select(e => new TableItem(e.LogicalName, e.DisplayName, e.EntitySetName, e.Category, e.CategoryDetail)));
            ApplyTableFilter();

            // 清单里的项都是新对象,按表名把选中项找回来;下方已显示的表结构和 ER 图不动
            if (selectedName is not null
                && Tables.FirstOrDefault(t => string.Equals(t.LogicalName, selectedName, StringComparison.OrdinalIgnoreCase)) is { } again)
            {
                _restoringSelection = true;
                try { SelectedTable = again; }
                finally { _restoringSelection = false; }
            }

            var diff = _allTables.Count - before;
            Status = $"表清单已刷新,共 {_allTables.Count} 张表"
                     + (diff == 0 ? "" : diff > 0 ? $"(比刷新前多 {diff} 张)" : $"(比刷新前少 {-diff} 张)")
                     + ",正在重新加载记录数";
            _ = LoadRecordCountsAsync(metadata);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(metadata, _metadata)) SetError("刷新表清单失败(保留原来的清单): " + ex.Message);
        }
        finally
        {
            IsRefreshingTables = false;
        }
    }
    [ObservableProperty] public partial string ConnectionLabel { get; set; } = "未连接";
    [ObservableProperty] public partial bool IsError { get; set; }

    /// <summary>翻译失败时出错位置(0-based 偏移和长度),界面用来高亮;-1 表示无。</summary>
    public int ErrorOffset { get; private set; } = -1;
    public int ErrorLength { get; private set; }

    public ObservableCollection<string?[]> Rows { get; } = new();

    /// <summary>结果列变化时通知视图重建 DataGrid 的列。</summary>
    public event Action<string[]>? ResultColumnsChanged;

    /// <summary>当前连接的元数据(自动补全用);离线时为 null。</summary>
    public Sql2Fetch.Engine.Metadata.IMetadataProvider? Metadata => _metadata;

    public void Prefetch(System.Collections.Generic.IEnumerable<string> tables) => _metadata?.Prefetch(tables);

    /// <summary>由视图提供:把文本放进剪贴板。</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    [ObservableProperty] public partial string DeviceCode { get; set; } = "";

    public sealed class TableItem : ObservableObject
    {
        private long? _recordCount;

        public TableItem(string logicalName, string? displayName, string entitySetName,
            DataverseMetadataProvider.TableCategory category = DataverseMetadataProvider.TableCategory.Unknown, string categoryDetail = "")
        {
            LogicalName = logicalName;
            DisplayName = displayName;
            EntitySetName = entitySetName;
            Category = category;
            CategoryDetail = categoryDetail;
        }

        /// <summary>表类型(启发式分类):业务 / 配置 / 系统 / 中间表。</summary>
        public DataverseMetadataProvider.TableCategory Category { get; }
        public string CategoryDetail { get; }

        public string CategoryLabel => Category switch
        {
            DataverseMetadataProvider.TableCategory.Business => "业务",
            DataverseMetadataProvider.TableCategory.Config => "配置",
            DataverseMetadataProvider.TableCategory.System => "系统",
            DataverseMetadataProvider.TableCategory.Intersect => "中间表",
            _ => "",
        };

        public string CategoryTip => Category switch
        {
            DataverseMetadataProvider.TableCategory.Business => "业务表:记录归用户或团队所有,通常由用户维护",
            DataverseMetadataProvider.TableCategory.Config => "配置表:组织级的主数据或设置,通常由管理员维护",
            DataverseMetadataProvider.TableCategory.System => "系统表:数据主要由平台自动生成",
            DataverseMetadataProvider.TableCategory.Intersect => "中间表:N:N 关系自动生成",
            _ => "未能取得分类所需的元数据",
        } + (CategoryDetail.Length == 0 ? "" : "\n" + CategoryDetail) + "\n(按元数据推断,仅供参考)";

        public string LogicalName { get; }
        public string? DisplayName { get; }
        public string EntitySetName { get; }

        public string Title => string.IsNullOrEmpty(DisplayName) ? LogicalName : DisplayName;
        public string Subtitle => string.IsNullOrEmpty(DisplayName) || DisplayName == LogicalName ? "" : LogicalName;

        /// <summary>记录数(Dataverse 的快照值);还没取到或取不到时为 null。</summary>
        public long? RecordCount
        {
            get => _recordCount;
            set
            {
                if (SetProperty(ref _recordCount, value)) OnPropertyChanged(nameof(RecordCountText));
            }
        }

        public string RecordCountText => _recordCount is { } n ? n.ToString("N0") : "";
    }

    private CancellationTokenSource? _recordCountCts;
    private string _recordCountNote = "";

    /// <summary>后台加载各表记录数,取到一批就更新一批,不阻塞连接。</summary>
    private async Task LoadRecordCountsAsync(DataverseMetadataProvider metadata)
    {
        _recordCountCts?.Cancel();
        var cts = _recordCountCts = new CancellationTokenSource();
        var items = _allTables.ToDictionary(t => t.LogicalName, StringComparer.OrdinalIgnoreCase);
        var loaded = 0;

        _recordCountNote = " · 正在加载记录数...";
        ApplyTableCountLabel();

        try
        {
            await metadata.LoadRecordCountsAsync(items.Keys.ToList(), counts =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    foreach (var (name, count) in counts)
                        if (items.TryGetValue(name, out var item)) { item.RecordCount = count; loaded++; }
                }), cts.Token);

            // 等前面 Post 的更新都执行完再读 loaded
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            if (cts.IsCancellationRequested) return;
            _recordCountNote = loaded == 0 ? " · 记录数加载失败" : "";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            _recordCountNote = " · 记录数加载失败: " + ex.Message;
        }
        // 记录数到齐后,按记录数排序的清单要重排一次
        if (TableSortIndex >= 2) ApplyTableFilter();
        else ApplyTableCountLabel();
    }

    private int _matchedTableCount;

    private void ApplyTableCountLabel()
    {
        TableCountLabel = _allTables.Count == 0 ? "连接后显示表清单"
            : (_matchedTableCount == _allTables.Count ? $"{_allTables.Count} 张表" : $"{_matchedTableCount} / {_allTables.Count} 张表")
              + _recordCountNote;
    }

    private readonly System.Collections.Generic.List<TableItem> _allTables = new();

    public ObservableCollection<TableItem> Tables { get; } = new();

    [ObservableProperty] public partial string TableFilter { get; set; } = "";
    [ObservableProperty] public partial string TableCountLabel { get; set; } = "连接后显示表清单";

    /// <summary>表结构里的一列。</summary>
    public sealed record ColumnItem(string LogicalName, string? DisplayName, string Type, string Notes);

    /// <summary>左侧选中的表;选中后自动加载它的表结构。</summary>
    [ObservableProperty] public partial TableItem? SelectedTable { get; set; }

    public ObservableCollection<ColumnItem> SchemaColumns { get; } = new();

    private const string SchemaHint = "点击左侧的表查看表结构";
    [ObservableProperty] public partial string SchemaTitle { get; set; } = SchemaHint;

    /// <summary>底部标签页:0 = 结果,1 = 表结构,2 = ER 图。</summary>
    [ObservableProperty] public partial int BottomTabIndex { get; set; }

    private int _schemaRequestId;

    private readonly System.Collections.Generic.List<ColumnItem> _allSchemaColumns = new();
    private string _schemaTitleBase = "";

    /// <summary>表结构的列搜索框:按列名或显示名过滤。</summary>
    [ObservableProperty] public partial string SchemaFilter { get; set; } = "";

    partial void OnSchemaFilterChanged(string value) => ApplySchemaFilter();

    private void ApplySchemaFilter()
    {
        SchemaColumns.Clear();
        var filter = SchemaFilter.Trim();
        var matched = string.IsNullOrEmpty(filter)
            ? _allSchemaColumns
            : _allSchemaColumns.Where(c => c.LogicalName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                        || (c.DisplayName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        foreach (var c in matched) SchemaColumns.Add(c);

        if (_allSchemaColumns.Count == 0) return; // 没有加载表结构时,标题由调用方负责
        var count = matched.Count == _allSchemaColumns.Count ? $"{_allSchemaColumns.Count} 列" : $"{matched.Count} / {_allSchemaColumns.Count} 列";
        SchemaTitle = $"{_schemaTitleBase} · {count}";
    }

    private void ClearSchema(string title)
    {
        _allSchemaColumns.Clear();
        SchemaColumns.Clear();
        SchemaTitle = title;
    }

    partial void OnSelectedTableChanged(TableItem? value)
    {
        // 过滤表清单会清空选中项,此时保留已显示的表结构
        if (value is not null && !_restoringSelection) ShowTable(value);
    }

    private void ShowTable(TableItem table)
    {
        _ = LoadSchemaAsync(table);
        _ = LoadErAsync(table);
    }

    /// <summary>ER 图里点了某张关联表:跳到那张表(清单里能看到就同时选中它)。</summary>
    public void NavigateToTable(string logicalName)
    {
        var item = _allTables.FirstOrDefault(t => string.Equals(t.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        if (Tables.Contains(item) && !ReferenceEquals(SelectedTable, item)) SelectedTable = item;
        else ShowTable(item);
    }

    /// <summary>ER 图里的一张关联表。Kind 是 "N:1" / "1:N" / "N:N";Columns 是参与关系的查找列(N:N 时是中间表)。</summary>
    public sealed record ErNode(string LogicalName, string? DisplayName, string Kind, System.Collections.Generic.IReadOnlyList<string> Columns,
        DataverseMetadataProvider.TableCategory Category = DataverseMetadataProvider.TableCategory.Unknown);

    /// <summary>中心表的一条自关联。ManyToMany 时 Column 是中间表名,否则是查找列。</summary>
    public sealed record ErSelfRelation(bool ManyToMany, string Column);

    /// <summary>以一张表为中心的 ER 图:Parents 是本表引用的表,Children 是引用本表的表和多对多的表。</summary>
    public sealed record ErModel(string LogicalName, string? DisplayName, string? PrimaryId,
        System.Collections.Generic.IReadOnlyList<string> SelfLines,
        System.Collections.Generic.IReadOnlyList<ErNode> Parents,
        System.Collections.Generic.IReadOnlyList<ErNode> Children,
        DataverseMetadataProvider.TableCategory Category = DataverseMetadataProvider.TableCategory.Unknown,
        System.Collections.Generic.IReadOnlyList<ErSelfRelation>? SelfRelations = null);

    /// <summary>ER 图上标注的表类型名;未知时为空。</summary>
    public static string CategoryName(DataverseMetadataProvider.TableCategory category) => category switch
    {
        DataverseMetadataProvider.TableCategory.Business => "业务表",
        DataverseMetadataProvider.TableCategory.Config => "配置表",
        DataverseMetadataProvider.TableCategory.System => "系统表",
        DataverseMetadataProvider.TableCategory.Intersect => "中间表",
        _ => "",
    };

    public bool HasEr => Er is not null;

    /// <summary>按 ER 过滤框筛选后的关联表(界面和导出共用,导出的就是看到的)。</summary>
    public (System.Collections.Generic.List<ErNode> Parents, System.Collections.Generic.List<ErNode> Children) FilteredErNodes()
    {
        var er = Er;
        if (er is null) return (new(), new());
        var filter = (ErFilter ?? "").Trim();
        bool Match(ErNode n) => filter.Length == 0
            || n.LogicalName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (n.DisplayName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
            || n.Columns.Any(c => c.Contains(filter, StringComparison.OrdinalIgnoreCase));
        return (er.Parents.Where(Match).ToList(), er.Children.Where(Match).ToList());
    }

    /// <summary>
    /// 把当前 ER 图(过滤后)整理成两张工作表:「表」列出图里的每张表和它的类型,「关系」每条关系一行。
    /// 没有 ER 图时返回 null。
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<XlsxWriter.Sheet>? BuildErSheets()
    {
        var er = Er;
        if (er is null) return null;
        var (parents, children) = FilteredErNodes();
        var cmp = StringComparer.OrdinalIgnoreCase;

        // 表:当前表在最前,其余按表名;同一张表可能既被引用又引用本表,关系合并写
        var tables = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyList<string>>
        {
            new[] { "表名", "显示名", "表类型", "与当前表的关系" },
            new[] { er.LogicalName, er.DisplayName ?? "", CategoryName(er.Category), "当前表" },
        };
        foreach (var g in parents.Concat(children).GroupBy(n => n.LogicalName, cmp).OrderBy(g => g.Key, cmp))
            tables.Add(new[] { g.Key, g.First().DisplayName ?? "", CategoryName(g.First().Category), string.Join(" / ", g.Select(n => n.Kind).Distinct()) });

        var relations = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyList<string>>
        {
            new[] { "关系", "当前表", "关联表", "关联表显示名", "关联表类型", "查找列所在的表", "查找列", "中间表" },
        };
        void Add(string kind, string related, string? display, DataverseMetadataProvider.TableCategory category, string lookupTable, string lookup, string intersect) =>
            relations.Add(new[] { kind, er.LogicalName, related, display ?? "", CategoryName(category), lookupTable, lookup, intersect });

        foreach (var n in parents)
            foreach (var column in n.Columns.DefaultIfEmpty(""))
                Add("N:1", n.LogicalName, n.DisplayName, n.Category, er.LogicalName, column, "");
        foreach (var n in children)
            foreach (var column in n.Columns.DefaultIfEmpty(""))
            {
                if (n.Kind == "N:N") Add("N:N", n.LogicalName, n.DisplayName, n.Category, "", "", column);
                else Add("1:N", n.LogicalName, n.DisplayName, n.Category, n.LogicalName, column, "");
            }
        foreach (var self in er.SelfRelations ?? Array.Empty<ErSelfRelation>())
        {
            if (self.ManyToMany) Add("N:N 自关联", er.LogicalName, er.DisplayName, er.Category, "", "", self.Column);
            else Add("自关联", er.LogicalName, er.DisplayName, er.Category, er.LogicalName, self.Column, "");
        }

        return new[] { new XlsxWriter.Sheet("表", tables), new XlsxWriter.Sheet("关系", relations) };
    }

    /// <summary>把当前 ER 图(过滤后)导出成 Mermaid erDiagram 文本;没有 ER 图时返回 null。</summary>
    public string? BuildErMermaid()
    {
        var er = Er;
        if (er is null) return null;
        var (parents, children) = FilteredErNodes();

        static string Quote(string text) => "\"" + text.Replace("\"", "'") + "\"";
        static string Label(string logicalName, string? displayName, DataverseMetadataProvider.TableCategory category)
        {
            var label = logicalName;
            if (!string.IsNullOrEmpty(displayName) && displayName != logicalName) label += " · " + displayName;
            var type = CategoryName(category);
            return Quote(type.Length == 0 ? label : $"{label} [{type}]");
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("erDiagram");

        // 实体定义:别名里带显示名和表类型
        sb.Append("    ").Append(er.LogicalName).Append('[').Append(Label(er.LogicalName, er.DisplayName, er.Category)).Append(']');
        if (!string.IsNullOrEmpty(er.PrimaryId)) sb.AppendLine(" {").Append("        uniqueidentifier ").Append(er.PrimaryId).AppendLine(" PK").AppendLine("    }");
        else sb.AppendLine();

        var declared = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { er.LogicalName };
        foreach (var n in parents.Concat(children))
            if (declared.Add(n.LogicalName))
                sb.Append("    ").Append(n.LogicalName).Append('[').Append(Label(n.LogicalName, n.DisplayName, n.Category)).AppendLine("]");

        sb.AppendLine();
        // 关系:"一"的一方写在左边
        foreach (var n in parents)
            sb.AppendLine($"    {n.LogicalName} ||--o{{ {er.LogicalName} : {Quote(string.Join(", ", n.Columns))}");
        foreach (var n in children)
            sb.AppendLine(n.Kind == "N:N"
                ? $"    {er.LogicalName} }}o--o{{ {n.LogicalName} : {Quote(string.Join(", ", n.Columns))}"
                : $"    {er.LogicalName} ||--o{{ {n.LogicalName} : {Quote(string.Join(", ", n.Columns))}");
        foreach (var self in er.SelfRelations ?? Array.Empty<ErSelfRelation>())
            sb.AppendLine(self.ManyToMany
                ? $"    {er.LogicalName} }}o--o{{ {er.LogicalName} : {Quote(self.Column)}"
                : $"    {er.LogicalName} ||--o{{ {er.LogicalName} : {Quote(self.Column)}");

        return sb.ToString();
    }

    private const string ErHint = "点击左侧的表查看 ER 图";

    public ErModel? Er { get; private set; }

    /// <summary>ER 图数据变化时通知视图重画。</summary>
    public event Action? ErChanged;

    [ObservableProperty] public partial string ErTitle { get; set; } = ErHint;
    [ObservableProperty] public partial string ErFilter { get; set; } = "";

    private int _erRequestId;

    private void ClearEr(string title)
    {
        Er = null;
        OnPropertyChanged(nameof(HasEr));
        ErTitle = title;
        ErChanged?.Invoke();
    }

    private async Task LoadErAsync(TableItem table)
    {
        var metadata = _metadata;
        if (metadata is null) return;

        var requestId = ++_erRequestId;
        ErTitle = $"正在加载 {table.LogicalName} 的关系...";

        try
        {
            var relations = await metadata.GetRelationshipsAsync(table.LogicalName);
            if (requestId != _erRequestId) return;

            var cmp = StringComparer.OrdinalIgnoreCase;
            var displayNames = new System.Collections.Generic.Dictionary<string, string?>(cmp);
            var categories = new System.Collections.Generic.Dictionary<string, DataverseMetadataProvider.TableCategory>(cmp);
            foreach (var t in _allTables) { displayNames[t.LogicalName] = t.DisplayName; categories[t.LogicalName] = t.Category; }

            bool IsSelf(DataverseMetadataProvider.RelationshipInfo r) => cmp.Equals(r.RelatedEntity, table.LogicalName);

            ErNode[] Group(DataverseMetadataProvider.RelationshipKind kind, string label) => relations
                .Where(r => r.Kind == kind && !IsSelf(r))
                .GroupBy(r => r.RelatedEntity, cmp)
                .OrderBy(g => g.Key, cmp)
                .Select(g => new ErNode(g.Key, displayNames.TryGetValue(g.Key, out var display) ? display : null, label,
                    g.Select(r => r.Column).Where(c => c.Length > 0).Distinct(cmp).OrderBy(c => c, cmp).ToList(),
                    categories.TryGetValue(g.Key, out var category) ? category : DataverseMetadataProvider.TableCategory.Unknown))
                .ToArray();

            var parents = Group(DataverseMetadataProvider.RelationshipKind.ManyToOne, "N:1");
            var oneToMany = Group(DataverseMetadataProvider.RelationshipKind.OneToMany, "1:N");
            var manyToMany = Group(DataverseMetadataProvider.RelationshipKind.ManyToMany, "N:N");

            // 自关联在 N:1 和 1:N 里各出现一次,去重后写在中心表里
            var selfLines = relations.Where(IsSelf)
                .Select(r => r.Kind == DataverseMetadataProvider.RelationshipKind.ManyToMany ? $"N:N 自关联 ({r.Column})" : $"自关联: {r.Column}")
                .Distinct().OrderBy(s => s).ToList();
            var selfRelations = relations.Where(IsSelf)
                .Select(r => new ErSelfRelation(r.Kind == DataverseMetadataProvider.RelationshipKind.ManyToMany, r.Column))
                .Distinct().OrderBy(r => r.Column, cmp).ToList();

            var primaryId = metadata.Entities.FirstOrDefault(e => cmp.Equals(e.LogicalName, table.LogicalName))?.PrimaryIdAttribute;

            Er = new ErModel(table.LogicalName, table.DisplayName, primaryId, selfLines, parents, oneToMany.Concat(manyToMany).ToArray(),
                table.Category, selfRelations);
            OnPropertyChanged(nameof(HasEr));
            ErTitle = $"{table.LogicalName} · 引用 {parents.Length} 张表 (N:1) · 被 {oneToMany.Length} 张表引用 (1:N) · 多对多 {manyToMany.Length} 张 (N:N)";
            ErChanged?.Invoke();
        }
        catch (Exception ex)
        {
            if (requestId != _erRequestId) return;
            ClearEr($"加载 {table.LogicalName} 的关系失败: {ex.Message}");
        }
    }

    private async Task LoadSchemaAsync(TableItem table)
    {
        var metadata = _metadata;
        if (metadata is null) return;

        var requestId = ++_schemaRequestId;
        // 正在看结果时切到表结构;已经在表结构或 ER 图上就留在原处
        if (BottomTabIndex == 0) BottomTabIndex = 1;
        SchemaTitle = $"正在加载 {table.LogicalName} 的表结构...";

        try
        {
            var entity = await metadata.GetEntityAsync(table.LogicalName);
            // 期间用户又点了别的表(或断开了连接),这次结果作废
            if (requestId != _schemaRequestId) return;

            if (entity is null)
            {
                ClearSchema($"找不到表 {table.LogicalName} 的元数据");
                return;
            }
            _allSchemaColumns.Clear();

            var columns = entity.Attributes.Values
                .OrderBy(a => a.LogicalName == entity.PrimaryIdAttribute ? 0 : a.LogicalName == entity.PrimaryNameAttribute ? 1 : 2)
                .ThenBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase);

            foreach (var a in columns)
            {
                var notes = new System.Collections.Generic.List<string>();
                if (a.LogicalName == entity.PrimaryIdAttribute) notes.Add("主键");
                if (a.LogicalName == entity.PrimaryNameAttribute) notes.Add("主名称");
                if (!string.IsNullOrEmpty(a.AttributeOf)) notes.Add($"{a.AttributeOf} 的附属列");
                if (a.HasOptions) notes.Add(string.Join("; ", a.Options!.Select(o => $"{o.Value}={o.Label}")));
                _allSchemaColumns.Add(new ColumnItem(a.LogicalName, a.DisplayName, a.AttributeType, string.Join(" · ", notes)));
            }

            var display = string.IsNullOrEmpty(table.DisplayName) || table.DisplayName == table.LogicalName ? "" : $"({table.DisplayName})";
            _schemaTitleBase = $"{table.LogicalName}{display} · 集合名 {entity.EntitySetName}";
            if (_allSchemaColumns.Count == 0) ClearSchema($"{_schemaTitleBase} · 0 列");
            else ApplySchemaFilter();
        }
        catch (Exception ex)
        {
            if (requestId != _schemaRequestId) return;
            ClearSchema($"加载 {table.LogicalName} 的表结构失败: {ex.Message}");
        }
    }

    partial void OnTableFilterChanged(string value) => ApplyTableFilter();

    private void ApplyTableFilter()
    {
        // 重建清单会清空 ListBox 的选中项,先记下来,重建后恢复
        var selected = SelectedTable;
        Tables.Clear();
        var filter = TableFilter.Trim();
        var matched = string.IsNullOrEmpty(filter)
            ? _allTables
            : _allTables.Where(t => t.LogicalName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                 || (t.DisplayName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        // 类型筛选:0 = 全部,1 = 业务,2 = 配置,3 = 系统,4 = 中间表
        if (TableTypeIndex > 0)
            matched = matched.Where(t => (int)t.Category == TableTypeIndex - 1).ToList();
        foreach (var t in SortTables(matched)) Tables.Add(t);
        _matchedTableCount = matched.Count;
        ApplyTableCountLabel();

        if (selected is not null && matched.Contains(selected))
        {
            _restoringSelection = true;
            try { SelectedTable = selected; }
            finally { _restoringSelection = false; }
        }
    }

    /// <summary>表清单的类型筛选:0 = 全部,1 = 业务,2 = 配置,3 = 系统,4 = 中间表。</summary>
    [ObservableProperty] public partial int TableTypeIndex { get; set; }

    partial void OnTableTypeIndexChanged(int value)
    {
        ApplyTableFilter();
        SaveSettings();
    }

    /// <summary>表清单排序:0 = 表名升序,1 = 表名降序,2 = 记录数从多到少,3 = 记录数从少到多,4 = 类型升序,5 = 类型降序。</summary>
    [ObservableProperty] public partial int TableSortIndex { get; set; }

    private bool _restoringSelection;

    partial void OnTableSortIndexChanged(int value)
    {
        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(CountHeader));
        OnPropertyChanged(nameof(TypeHeader));
        ApplyTableFilter();
        SaveSettings();
    }

    /// <summary>表头文字,带当前排序方向的箭头。</summary>
    public string NameHeader => "表名" + (TableSortIndex == 0 ? " ▲" : TableSortIndex == 1 ? " ▼" : "");
    public string CountHeader => "件数" + (TableSortIndex == 3 ? " ▲" : TableSortIndex == 2 ? " ▼" : "");

    public string TypeHeader => "类型" + (TableSortIndex == 4 ? " ▲" : TableSortIndex == 5 ? " ▼" : "");

    /// <summary>点「类型」表头:按 业务 → 配置 → 系统 → 中间表 排,再点反过来。</summary>
    [RelayCommand]
    private void SortByType() => TableSortIndex = TableSortIndex == 4 ? 5 : 4;

    /// <summary>点「表名」表头:先升序,再点切换为降序。</summary>
    [RelayCommand]
    private void SortByName() => TableSortIndex = TableSortIndex == 0 ? 1 : 0;

    /// <summary>点「件数」表头:先从多到少,再点切换为从少到多。</summary>
    [RelayCommand]
    private void SortByCount() => TableSortIndex = TableSortIndex == 2 ? 3 : 2;

    private System.Collections.Generic.IEnumerable<TableItem> SortTables(System.Collections.Generic.IEnumerable<TableItem> tables)
    {
        var byName = StringComparer.OrdinalIgnoreCase;
        return TableSortIndex switch
        {
            1 => tables.OrderByDescending(t => t.LogicalName, byName),
            // 没有记录数的表不论升降序都排在最后
            2 => tables.OrderBy(t => t.RecordCount is null).ThenByDescending(t => t.RecordCount).ThenBy(t => t.LogicalName, byName),
            3 => tables.OrderBy(t => t.RecordCount is null).ThenBy(t => t.RecordCount).ThenBy(t => t.LogicalName, byName),
            4 => tables.OrderBy(t => t.Category).ThenBy(t => t.LogicalName, byName),
            5 => tables.OrderByDescending(t => t.Category).ThenBy(t => t.LogicalName, byName),
            _ => tables.OrderBy(t => t.LogicalName, byName),
        };
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(EnvironmentUrl) || !Uri.TryCreate(EnvironmentUrl, UriKind.Absolute, out _))
        {
            SetError("请输入有效的环境地址,例如 https://org12345.crm7.dynamics.com/");
            return;
        }

        var mode = Enum.IsDefined(typeof(AuthMode), AuthModeIndex) ? (AuthMode)AuthModeIndex : AuthMode.Interactive;
        if (mode == AuthMode.UsernamePassword && (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrEmpty(Password)))
        {
            SetError("用户名密码登录需要同时填写用户名和密码");
            return;
        }

        IsBusy = true;
        IsError = false;
        _connectCts = new CancellationTokenSource();

        try
        {
            Status = mode == AuthMode.Interactive
                ? "正在连接...(如需登录会自动打开浏览器,请在浏览器中完成登录)"
                : "正在连接...";
            var connection = await DataverseConnection.CreateAsync(EnvironmentUrl);

            await connection.ConnectAsync(mode, UserName, Password, async (url, code) =>
            {
                DeviceCode = code;
                var copied = false;
                if (CopyToClipboard is not null)
                {
                    try { await CopyToClipboard(code); copied = true; } catch { }
                }
                Status = $"请在浏览器打开 {url} 并输入代码 {code}" + (copied ? "(已复制到剪贴板,浏览器已尝试自动打开)" : "");
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            }, _connectCts.Token);

            DeviceCode = "";
            Password = "";
            if (!string.IsNullOrEmpty(connection.UserName)) UserName = connection.UserName;

            Status = $"已登录 {connection.UserName},正在加载表清单...";
            var metadata = new DataverseMetadataProvider(connection);
            await metadata.LoadEntitiesAsync(_connectCts.Token);

            _connection = connection;
            _metadata = metadata;
            _allTables.Clear();
            _allTables.AddRange(metadata.Entities
                .OrderBy(e => e.LogicalName, StringComparer.OrdinalIgnoreCase)
                .Select(e => new TableItem(e.LogicalName, e.DisplayName, e.EntitySetName, e.Category, e.CategoryDetail)));
            ApplyTableFilter();
            _ = LoadRecordCountsAsync(metadata);
            IsConnected = true;
            ConnectionLabel = $"{connection.UserName} @ {connection.Url.Host}";
            Status = $"已连接,共 {metadata.Entities.Count} 张表。翻译时会校验表名列名,F5 可执行查询";
            SaveSettings();
        }
        catch (OperationCanceledException)
        {
            Status = "已取消连接";
        }
        catch (Microsoft.Identity.Client.MsalUiRequiredException ex) when (mode == AuthMode.UsernamePassword)
        {
            SetError("用户名密码登录被拒绝:该账号需要 MFA 或交互式验证,请改用「浏览器登录」。详情: " + ex.Message);
        }
        catch (Microsoft.Identity.Client.MsalException ex) when (ex.ErrorCode == "authentication_canceled")
        {
            Status = "已取消登录";
        }
        catch (Exception ex)
        {
            SetError("连接失败: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            DeviceCode = "";
            _connectCts = null;
        }
    }

    [RelayCommand]
    private void CancelConnect()
    {
        // 「取消」按钮在连接和执行查询时都显示,两种情况都要能取消
        _connectCts?.Cancel();
        _runCts?.Cancel();
    }

    private CancellationTokenSource? _runCts;

    private async Task DisconnectAsync()
    {
        if (_connection is not null)
        {
            try { await _connection.SignOutAsync(); } catch { }
            _connection.Dispose();
        }
        _connection = null;
        _metadata = null;
        _recordCountCts?.Cancel();
        _recordCountNote = "";
        _allTables.Clear();
        ApplyTableFilter();
        _schemaRequestId++;
        ClearSchema(SchemaHint);
        _erRequestId++;
        ClearEr(ErHint);
        IsConnected = false;
        ConnectionLabel = "未连接";
        Status = "已断开,回到离线模式";
    }

    [RelayCommand]
    private async Task TranslateAsync()
    {
        IsBusy = true;
        IsError = false;
        ErrorOffset = -1;
        Warnings = "";

        try
        {
            var sql = Sql;
            var metadata = _metadata;
            var result = await Task.Run(() => SqlToFetchXml.Convert(sql, metadata));

            _lastResult = result;
            _lastTranslatedSql = sql;
            FetchXml = result.FetchXml;
            Warnings = result.Warnings.Count == 0 ? "" : "⚠ " + string.Join("\n⚠ ", result.Warnings);
            Status = $"翻译成功:主表 {result.EntityLogicalName}" + (result.IsAggregate ? "(聚合查询)" : "")
                     + (IsConnected ? ",按 F5 执行" : "");
            SaveSettings();
        }
        catch (SqlTranslationException ex)
        {
            _lastResult = null;
            ErrorOffset = ex.Offset;
            ErrorLength = Math.Max(ex.Length, 1);
            var position = ex.Line > 0 ? $"(第 {ex.Line} 行, 第 {ex.Column} 列)" : "";
            SetError($"{ex.Message} {position}" + (ex.Suggestion is null ? "" : $"\n提示: {ex.Suggestion}"));
        }
        catch (Exception ex)
        {
            _lastResult = null;
            SetError("翻译出错: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (!IsConnected || _connection is null || _metadata is null)
        {
            SetError("请先连接 Dataverse 环境");
            return;
        }

        if (_lastResult is null || _lastTranslatedSql != Sql)
        {
            await TranslateAsync();
            if (_lastResult is null) return;
        }

        IsBusy = true;
        IsError = false;
        var cts = _runCts = new CancellationTokenSource();

        try
        {
            var entitySet = _lastResult.EntitySetName
                ?? _metadata.GetEntity(_lastResult.EntityLogicalName)?.EntitySetName
                ?? _lastResult.EntityLogicalName + "s";

            Status = "正在执行...";
            var executor = new FetchXmlExecutor(_connection);
            var result = await executor.ExecuteAsync(_lastResult.FetchXml, entitySet, ct: cts.Token,
                localFilter: _lastResult.LocalFilter, hiddenColumns: _lastResult.HiddenColumns,
                outputColumns: _lastResult.Columns,
                localTop: _lastResult.LocalTop, localOffset: _lastResult.LocalOffset);

            Rows.Clear();
            // 列以引擎的输出列为准(执行器已处理),空结果也有表头
            var resultColumns = result.Columns.ToArray();
            ResultColumnsChanged?.Invoke(resultColumns);
            foreach (var row in result.Rows) Rows.Add(row);
            BottomTabIndex = 0;

            Status = $"返回 {result.Rows.Count} 行,{resultColumns.Length} 列,{result.PagesRetrieved} 页,耗时 {result.Elapsed.TotalSeconds:F1} 秒"
                     + (result.MoreRecords ? "(还有更多数据,已达翻页上限)" : "")
                     + (_lastResult.LocalFilter is not null ? $";HAVING 在本地过滤掉 {result.FilteredOut} 行" : "");
        }
        catch (OperationCanceledException)
        {
            Status = "已取消执行(之前的结果保持不变)";
        }
        catch (DataverseException ex)
        {
            SetError($"Dataverse 返回错误 (HTTP {ex.StatusCode}): {ex.Message}");
        }
        catch (Exception ex)
        {
            SetError("执行出错: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            _runCts = null;
        }
    }

    private void SetError(string message)
    {
        IsError = true;
        Status = message;
    }

    private sealed record Settings(string? EnvironmentUrl, string? Sql, int? AuthMode = null, string? UserName = null, int? TableSort = null, int? TableType = null);

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (!string.IsNullOrWhiteSpace(s?.EnvironmentUrl)) EnvironmentUrl = s.EnvironmentUrl;
            if (!string.IsNullOrWhiteSpace(s?.Sql)) Sql = s.Sql;
            if (s?.AuthMode is { } m && Enum.IsDefined(typeof(AuthMode), m)) AuthModeIndex = m;
            if (!string.IsNullOrWhiteSpace(s?.UserName)) UserName = s.UserName;
            if (s?.TableSort is >= 0 and <= 5) TableSortIndex = s.TableSort.Value;
            if (s?.TableType is >= 0 and <= 4) TableTypeIndex = s.TableType.Value;
        }
        catch { }
    }

    public void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(EnvironmentUrl, Sql, AuthModeIndex, UserName, TableSortIndex, TableTypeIndex)));
        }
        catch { }
    }
}
