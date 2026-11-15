using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Linq;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Highlighting;
using Sql2Fetch.App.ViewModels;

namespace Sql2Fetch.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        SqlEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("TSQL");
        FetchEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("XML");

        TranslateButton.Click += async (_, _) => await TranslateAsync();
        RunButton.Click += async (_, _) => await RunAsync();
        CopyButton.Click += async (_, _) =>
        {
            if (Clipboard is not null && !string.IsNullOrEmpty(FetchEditor.Text))
                await Clipboard.SetTextAsync(FetchEditor.Text);
        };

        // 双击表名:插入到 SQL 光标处;编辑器为空则生成一条示例查询
        TableList.DoubleTapped += (_, _) =>
        {
            if (TableList.SelectedItem is not MainViewModel.TableItem table) return;
            if (string.IsNullOrWhiteSpace(SqlEditor.Text))
                SqlEditor.Text = $"SELECT TOP 50 *\nFROM {table.LogicalName}";
            else
                SqlEditor.Document.Insert(SqlEditor.CaretOffset, table.LogicalName);
            SqlEditor.Focus();
        };

        CopyCodeButton.Click += async (_, _) =>
        {
            if (Clipboard is not null && _vm is not null && !string.IsNullOrEmpty(_vm.DeviceCode))
                await Clipboard.SetTextAsync(_vm.DeviceCode);
        };

        ErExportPngItem.Click += async (_, _) => await ExportErPngAsync();
        ErExportExcelItem.Click += async (_, _) => await ExportErExcelAsync();
        ErExportMermaidItem.Click += async (_, _) => await ExportErMermaidAsync();
        ErCopyMermaidItem.Click += async (_, _) =>
        {
            if (_vm?.BuildErMermaid() is not { } mermaid || Clipboard is null) return;
            await Clipboard.SetTextAsync(mermaid);
            _vm.IsError = false;
            _vm.Status = "已把 ER 图的 Mermaid 文本复制到剪贴板";
        };

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as MainViewModel;
            if (_vm is null) return;

            _vm.CopyToClipboard = text => Clipboard is null ? System.Threading.Tasks.Task.CompletedTask : Clipboard.SetTextAsync(text);

            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.ResultColumnsChanged += RebuildColumns;
            _vm.ErChanged += RebuildErDiagram;
            SqlEditor.Text = _vm.Sql;
            FetchEditor.Text = _vm.FetchXml;
            ConnectButton.Content = _vm.IsConnected ? "断开" : "连接";
        };

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // 自动补全:输入字母/下划线/点时弹出;Ctrl+Space 强制弹出
        SqlEditor.TextArea.TextEntered += OnSqlTextEntered;
        SqlEditor.TextArea.TextEntering += OnSqlTextEntering;
        Closing += (_, _) => { if (_vm is not null) { _vm.Sql = SqlEditor.Text; _vm.SaveSettings(); } };
    }

    private CompletionWindow? _completionWindow;
    private int _completionRequestId;

    private void OnSqlTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_completionWindow is null || string.IsNullOrEmpty(e.Text)) return;
        var c = e.Text[0];
        // 输入标识符以外的字符(空格、逗号、点、括号……)时,先把当前选中项插入
        if (!char.IsLetterOrDigit(c) && c != '_')
            _completionWindow.CompletionList.RequestInsertion(e);
    }

    private void OnSqlTextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        var c = e.Text[0];
        if (char.IsLetter(c) || c == '_' || c == '.' || c == ' ')
            _ = ShowCompletionAsync(force: false);
    }

    private async System.Threading.Tasks.Task ShowCompletionAsync(bool force)
    {
        if (_vm is null) return;
        var metadata = _vm.Metadata;
        if (metadata is null) return; // 离线没有元数据,无从提示

        var text = SqlEditor.Text;
        var caret = SqlEditor.CaretOffset;
        var requestId = ++_completionRequestId;

        var autocomplete = new Sql2Fetch.Engine.Autocomplete.SqlAutocomplete(metadata);
        System.Collections.Generic.IReadOnlyList<Sql2Fetch.Engine.Autocomplete.CompletionItem> items;
        try
        {
            // 元数据可能要走 HTTP,放到后台线程
            items = await System.Threading.Tasks.Task.Run(() => autocomplete.GetSuggestions(text, caret));
            _vm.Prefetch(autocomplete.LastTables.Values);
        }
        catch
        {
            return;
        }

        // 期间用户又打了字,这次结果作废
        if (requestId != _completionRequestId || SqlEditor.CaretOffset != caret) return;

        if (items.Count == 0)
        {
            _completionWindow?.Close();
            return;
        }

        // 空格触发时只在有"关键字"类提示时弹(避免每个空格都弹列名)
        if (!force && caret > 0 && text[caret - 1] == ' ' && items.All(i => i.Kind != Sql2Fetch.Engine.Autocomplete.CompletionKind.Keyword)
            && !(caret >= 2 && IsAfterTrigger(text, caret)))
            return;

        var replaceLength = items[0].ReplaceLength;

        _completionWindow?.Close();
        _completionWindow = new CompletionWindow(SqlEditor.TextArea)
        {
            StartOffset = caret - replaceLength,
            EndOffset = caret,
            CloseWhenCaretAtBeginning = true,
            MaxHeight = 380,
            Width = 560,
        };
        _completionWindow.CompletionList.IsFiltering = true;
        foreach (var item in items)
            _completionWindow.CompletionList.CompletionData.Add(new CompletionItemData(item));
        _completionWindow.Closed += (_, _) => _completionWindow = null;
        _completionWindow.Show();
    }

    /// <summary>光标前是 "FROM "、"JOIN "、"WHERE "、"= " 这类之后,空格也值得弹。</summary>
    private static bool IsAfterTrigger(string text, int caret)
    {
        var before = text[..(caret - 1)].TrimEnd();
        var lastWord = before.Length == 0 ? "" : before[(before.LastIndexOfAny(new[] { ' ', '\n', '\t', ',', '(' }) + 1)..];
        return lastWord.Equals("from", StringComparison.OrdinalIgnoreCase) || lastWord.Equals("join", StringComparison.OrdinalIgnoreCase)
            || lastWord.Equals("where", StringComparison.OrdinalIgnoreCase) || lastWord.Equals("select", StringComparison.OrdinalIgnoreCase)
            || lastWord.Equals("on", StringComparison.OrdinalIgnoreCase) || lastWord.Equals("by", StringComparison.OrdinalIgnoreCase)
            || lastWord.Equals("and", StringComparison.OrdinalIgnoreCase) || lastWord.Equals("or", StringComparison.OrdinalIgnoreCase)
            || lastWord.EndsWith('=') || lastWord.EndsWith(',');
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            await ShowCompletionAsync(force: true);
            return;
        }

        if (e.Key == Key.F5)
        {
            e.Handled = true;
            await RunAsync();
        }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            await TranslateAsync();
        }
    }

    private async System.Threading.Tasks.Task TranslateAsync()
    {
        if (_vm is null) return;
        _vm.Sql = SqlEditor.Text;
        await _vm.TranslateCommand.ExecuteAsync(null);
        HighlightError();
    }

    private async System.Threading.Tasks.Task RunAsync()
    {
        if (_vm is null) return;
        _vm.Sql = SqlEditor.Text;
        await _vm.RunCommand.ExecuteAsync(null);
        HighlightError();
    }

    private void HighlightError()
    {
        if (_vm is null || _vm.ErrorOffset < 0 || _vm.ErrorOffset >= SqlEditor.Document.TextLength) return;
        var length = Math.Min(_vm.ErrorLength, SqlEditor.Document.TextLength - _vm.ErrorOffset);
        SqlEditor.Select(_vm.ErrorOffset, length);
        SqlEditor.Focus();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.FetchXml):
                FetchEditor.Text = _vm.FetchXml;
                break;
            case nameof(MainViewModel.IsError):
                StatusText.Foreground = _vm.IsError ? Brushes.IndianRed : null;
                break;
            case nameof(MainViewModel.ErFilter):
                RebuildErDiagram();
                break;
            case nameof(MainViewModel.SelectedTable):
                if (_vm.SelectedTable is not null) TableList.ScrollIntoView(_vm.SelectedTable);
                break;
            case nameof(MainViewModel.IsConnected):
                ConnectButton.Content = _vm.IsConnected ? "断开" : "连接";
                break;
        }
    }

    // ---------- ER 图 ----------
    // 三栏布局:左 = 本表引用的表 (N:1),中 = 当前表,右 = 引用本表的表 (1:N) 和多对多 (N:N)。

    private const double ErBoxWidth = 230;
    private const double ErGapX = 110;
    private const double ErGapY = 10;
    private const double ErMargin = 16;
    private const double ErHeaderHeight = 24;
    private const int ErMaxLines = 5;
    private static readonly IBrush ErLineBrush = new SolidColorBrush(Color.Parse("#8A8A8A"));

    private void RebuildErDiagram()
    {
        ErCanvas.Children.Clear();
        var er = _vm?.Er;
        if (er is null)
        {
            ErCanvas.Width = 0;
            ErCanvas.Height = 0;
            return;
        }

        var (parents, children) = _vm!.FilteredErNodes();

        var leftX = ErMargin;
        var centerX = leftX + ErBoxWidth + ErGapX;
        var rightX = centerX + ErBoxWidth + ErGapX;
        var top = ErMargin + ErHeaderHeight;

        AddErHeader($"引用的表 (N:1) · {parents.Count}", leftX);
        AddErHeader("当前表", centerX);
        AddErHeader($"被引用 (1:N) / 多对多 (N:N) · {children.Count}", rightX);

        // 中心表
        var centerLines = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(er.PrimaryId)) centerLines.Add("主键: " + er.PrimaryId);
        centerLines.AddRange(er.SelfLines);
        var (centerBox, centerHeight) = MakeErBox(er.LogicalName, er.DisplayName, null, er.Category, centerLines, int.MaxValue, isCenter: true);
        var centerMidY = top + centerHeight / 2;

        // 先画线再放框,线压在框下面
        var boxes = new System.Collections.Generic.List<(Control Box, double X, double Y)>();

        var y = top;
        foreach (var node in parents)
        {
            var (box, height) = MakeErNodeBox(node, "本表的列");
            boxes.Add((box, leftX, y));
            AddErLine(new Avalonia.Point(leftX + ErBoxWidth, y + height / 2), new Avalonia.Point(centerX, centerMidY));
            y += height + ErGapY;
        }
        var bottom = Math.Max(y, top + centerHeight + ErGapY);

        y = top;
        foreach (var node in children)
        {
            var (box, height) = MakeErNodeBox(node, node.Kind == "N:N" ? "中间表" : "对方表的列");
            boxes.Add((box, rightX, y));
            AddErLine(new Avalonia.Point(centerX + ErBoxWidth, centerMidY), new Avalonia.Point(rightX, y + height / 2));
            y += height + ErGapY;
        }
        bottom = Math.Max(bottom, y);

        boxes.Add((centerBox, centerX, top));
        foreach (var (box, x, boxY) in boxes)
        {
            Canvas.SetLeft(box, x);
            Canvas.SetTop(box, boxY);
            ErCanvas.Children.Add(box);
        }

        ErCanvas.Width = rightX + ErBoxWidth + ErMargin;
        ErCanvas.Height = bottom + ErMargin;
    }

    private void AddErHeader(string text, double x)
    {
        var header = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.6, Width = ErBoxWidth };
        Canvas.SetLeft(header, x);
        Canvas.SetTop(header, ErMargin);
        ErCanvas.Children.Add(header);
    }

    private void AddErLine(Avalonia.Point from, Avalonia.Point to)
    {
        var midX = (from.X + to.X) / 2;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(from, false);
            ctx.CubicBezierTo(new Avalonia.Point(midX, from.Y), new Avalonia.Point(midX, to.Y), to);
            ctx.EndFigure(false);
        }
        ErCanvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = geometry,
            Stroke = ErLineBrush,
            StrokeThickness = 1.2,
            IsHitTestVisible = false,
        });
    }

    private (Control Box, double Height) MakeErNodeBox(MainViewModel.ErNode node, string columnsCaption)
    {
        var (box, height) = MakeErBox(node.LogicalName, node.DisplayName, node.Kind, node.Category, node.Columns, ErMaxLines, isCenter: false);
        var type = MainViewModel.CategoryName(node.Category);
        ToolTip.SetTip(box, $"{node.LogicalName} ({node.Kind}){(type.Length == 0 ? "" : " · " + type)}\n{columnsCaption}: {string.Join(", ", node.Columns)}\n点击查看这张表");
        var target = node.LogicalName;
        box.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _vm?.NavigateToTable(target);
        };
        return (box, height);
    }

    /// <summary>画一个表框。高度按行数算好并显式指定,这样不用等布局就能排版和连线。</summary>
    private static (Border Box, double Height) MakeErBox(string title, string? subtitle, string? badge,
        Sql2Fetch.Dataverse.DataverseMetadataProvider.TableCategory category,
        System.Collections.Generic.IReadOnlyList<string> lines, int maxLines, bool isCenter)
    {
        const double titleHeight = 19, subtitleHeight = 15, lineHeight = 15, padding = 7;
        var panel = new StackPanel();
        var height = padding * 2;

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Height = titleHeight };
        titleRow.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (badge is not null)
        {
            var badgeText = new TextBlock { Text = badge, FontSize = 10, Opacity = 0.6, Margin = new Avalonia.Thickness(6, 2, 0, 0) };
            Grid.SetColumn(badgeText, 2);
            titleRow.Children.Add(badgeText);
        }
        // 表类型标签:业务表 / 配置表 / 系统表 / 中间表(白字配实色底,深浅主题下都看得清)
        var categoryName = MainViewModel.CategoryName(category);
        if (categoryName.Length > 0)
        {
            var pill = new Border
            {
                Background = ErCategoryBrush(category),
                CornerRadius = new Avalonia.CornerRadius(3),
                Padding = new Avalonia.Thickness(5, 0),
                Margin = new Avalonia.Thickness(6, 1, 0, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = categoryName, FontSize = 10, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
            };
            Grid.SetColumn(pill, 1);
            titleRow.Children.Add(pill);
        }
        panel.Children.Add(titleRow);
        height += titleHeight;

        if (!string.IsNullOrEmpty(subtitle) && subtitle != title)
        {
            panel.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Opacity = 0.6, Height = subtitleHeight, TextTrimming = TextTrimming.CharacterEllipsis });
            height += subtitleHeight;
        }

        var shown = lines.Count > maxLines ? maxLines - 1 : lines.Count;
        for (var i = 0; i < shown; i++)
        {
            panel.Children.Add(new TextBlock
            {
                Text = lines[i], FontSize = 11, Height = lineHeight, FontFamily = new FontFamily("Menlo,Consolas,monospace"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            height += lineHeight;
        }
        if (shown < lines.Count)
        {
            panel.Children.Add(new TextBlock { Text = $"… 共 {lines.Count} 项", FontSize = 11, Opacity = 0.6, Height = lineHeight });
            height += lineHeight;
        }

        var box = new Border
        {
            Width = ErBoxWidth,
            Height = height,
            Padding = new Avalonia.Thickness(9, padding),
            ClipToBounds = true,
            Child = panel,
        };
        box.Classes.Add(isCenter ? "er-center" : "er-box");
        return (box, height);
    }

    private static readonly IBrush ErBusinessBrush = new SolidColorBrush(Color.Parse("#2563EB"));
    private static readonly IBrush ErConfigBrush = new SolidColorBrush(Color.Parse("#047857"));
    private static readonly IBrush ErSystemBrush = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush ErIntersectBrush = new SolidColorBrush(Color.Parse("#B45309"));

    private static IBrush ErCategoryBrush(Sql2Fetch.Dataverse.DataverseMetadataProvider.TableCategory category) => category switch
    {
        Sql2Fetch.Dataverse.DataverseMetadataProvider.TableCategory.Business => ErBusinessBrush,
        Sql2Fetch.Dataverse.DataverseMetadataProvider.TableCategory.Config => ErConfigBrush,
        Sql2Fetch.Dataverse.DataverseMetadataProvider.TableCategory.Intersect => ErIntersectBrush,
        _ => ErSystemBrush,
    };

    // ---------- ER 图导出 ----------

    private async System.Threading.Tasks.Task<Avalonia.Platform.Storage.IStorageFile?> PickErExportFileAsync(string typeName, string extension)
    {
        if (_vm?.Er is not { } er) return null;
        return await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "导出 ER 图",
            SuggestedFileName = $"{er.LogicalName}-er.{extension}",
            DefaultExtension = extension,
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType(typeName) { Patterns = new[] { "*." + extension } } },
        });
    }

    private void ReportErExport(Avalonia.Platform.Storage.IStorageFile file)
    {
        if (_vm is null) return;
        _vm.IsError = false;
        _vm.Status = "ER 图已导出到 " + (file.TryGetLocalPath() ?? file.Name);
    }

    private void ReportErExportError(Exception ex)
    {
        if (_vm is null) return;
        _vm.IsError = true;
        _vm.Status = "导出 ER 图失败: " + ex.Message;
    }

    /// <summary>把整张 ER 图(包括滚动区域外的部分)渲染成 2 倍分辨率的 PNG。</summary>
    private async System.Threading.Tasks.Task ExportErPngAsync()
    {
        if (_vm?.Er is null || ErCanvas.Width <= 0 || ErCanvas.Height <= 0) return;
        try
        {
            var file = await PickErExportFileAsync("PNG 图片", "png");
            if (file is null) return;

            const double scale = 2;
            var pixelSize = new Avalonia.PixelSize((int)Math.Ceiling(ErCanvas.Width * scale), (int)Math.Ceiling(ErCanvas.Height * scale));
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(pixelSize, new Avalonia.Vector(96 * scale, 96 * scale));

            // 画布本身是透明的,导出时临时铺上窗口背景色,否则深色主题下浅色文字落在透明底上看不清
            var oldBackground = ErCanvas.Background;
            ErCanvas.Background = Background
                ?? (ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? new SolidColorBrush(Color.Parse("#1E1E1E")) : Brushes.White);
            try { bitmap.Render(ErCanvas); }
            finally { ErCanvas.Background = oldBackground; }

            await using (var stream = await file.OpenWriteAsync())
                bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            ReportErExport(file);
        }
        catch (Exception ex)
        {
            ReportErExportError(ex);
        }
    }

    private async System.Threading.Tasks.Task ExportErExcelAsync()
    {
        if (_vm?.BuildErSheets() is not { } sheets) return;
        try
        {
            var file = await PickErExportFileAsync("Excel 工作簿", "xlsx");
            if (file is null) return;
            await using (var stream = await file.OpenWriteAsync())
                Sql2Fetch.App.XlsxWriter.Write(stream, sheets);
            ReportErExport(file);
        }
        catch (Exception ex)
        {
            ReportErExportError(ex);
        }
    }

    private async System.Threading.Tasks.Task ExportErMermaidAsync()
    {
        if (_vm?.BuildErMermaid() is not { } mermaid) return;
        try
        {
            var file = await PickErExportFileAsync("Mermaid 图", "mmd");
            if (file is null) return;
            await using (var stream = await file.OpenWriteAsync())
            await using (var writer = new System.IO.StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                await writer.WriteAsync(mermaid);
            ReportErExport(file);
        }
        catch (Exception ex)
        {
            ReportErExportError(ex);
        }
    }

    private void RebuildColumns(string[] columns)
    {
        ResultsGrid.Columns.Clear();
        for (var i = 0; i < columns.Length; i++)
        {
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = columns[i],
                Binding = new Binding($"[{i}]"),
                Width = DataGridLength.Auto,
            });
        }
    }
}
