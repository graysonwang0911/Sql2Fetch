using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Sql2Fetch.Engine;

/// <summary>
/// 翻译失败。带上出错的 SQL 片段位置,界面可以高亮。
/// </summary>
public sealed class SqlTranslationException : Exception
{
    public int Line { get; }
    public int Column { get; }
    public int Offset { get; }
    public int Length { get; }
    public string? Suggestion { get; }

    public SqlTranslationException(string message, TSqlFragment? fragment = null, string? suggestion = null)
        : base(message)
    {
        Suggestion = suggestion;
        if (fragment is not null && fragment.StartLine > 0)
        {
            Line = fragment.StartLine;
            Column = fragment.StartColumn;
            Offset = fragment.StartOffset;
            Length = fragment.FragmentLength;
        }
    }

    public SqlTranslationException(ParseError error)
        : base(error.Message)
    {
        Line = error.Line;
        Column = error.Column;
        Offset = error.Offset;
        Length = 1;
    }

    public static SqlTranslationException NotSupported(TSqlFragment fragment, string what, string? suggestion = null) =>
        new($"不支持的语法: {what}", fragment, suggestion);
}
