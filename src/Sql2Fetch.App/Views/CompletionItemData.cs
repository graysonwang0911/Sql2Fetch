using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Sql2Fetch.Engine.Autocomplete;

namespace Sql2Fetch.App.Views;

/// <summary>把引擎的 CompletionItem 包成 AvaloniaEdit 能显示的条目:两行,左对齐。</summary>
public sealed class CompletionItemData : ICompletionData
{
    private readonly CompletionItem _item;

    public CompletionItemData(CompletionItem item)
    {
        _item = item;
    }

    public IImage? Image => null;

    public string Text => _item.Text;

    public object Content
    {
        get
        {
            var (tag, color) = _item.Kind switch
            {
                CompletionKind.Table => ("表", Brushes.SteelBlue),
                CompletionKind.Alias => ("别名", Brushes.SteelBlue),
                CompletionKind.Column => ("列", Brushes.SeaGreen),
                CompletionKind.Function => ("函数", Brushes.DarkMagenta),
                CompletionKind.Value => ("值", Brushes.DarkOrange),
                _ => ("关键字", Brushes.Gray),
            };

            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(2, 3),
                Spacing = 1,
            };

            // 第一行:左侧小标签 + 名称
            var firstLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
            firstLine.Children.Add(new Border
            {
                Background = color,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = tag, Foreground = Brushes.White, FontSize = 10 },
            });
            firstLine.Children.Add(new TextBlock
            {
                Text = _item.Text,
                FontFamily = new FontFamily("Menlo,Consolas,monospace"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(firstLine);

            // 第二行:显示名 · 类型 · 表
            if (!string.IsNullOrEmpty(_item.Description))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = _item.Description,
                    Opacity = 0.6,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            return panel;
        }
    }

    public object? Description => null;

    public double Priority => _item.Kind switch
    {
        CompletionKind.Value => 4,
        CompletionKind.Column => 3,
        CompletionKind.Alias => 2,
        CompletionKind.Table => 2,
        CompletionKind.Function => 1,
        _ => 0,
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, _item.Text);

        // 插入形如 lastxdays(7) 的模板时,选中括号里的参数方便直接改
        var open = _item.Text.IndexOf('(');
        var close = _item.Text.LastIndexOf(')');
        if (open >= 0 && close > open + 1)
        {
            var start = completionSegment.Offset + open + 1;
            textArea.Selection = Selection.Create(textArea, start, start + (close - open - 1));
            textArea.Caret.Offset = start + (close - open - 1);
        }
    }
}
