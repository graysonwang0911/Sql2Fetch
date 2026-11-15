using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace Sql2Fetch.App;

/// <summary>
/// 极简的 xlsx 写入器:只支持文本单元格,首行作为表头(加粗、冻结、带筛选)。
/// 导出的内容很简单,不值得为此引入一个 Excel 库。
/// </summary>
public static class XlsxWriter
{
    public sealed record Sheet(string Name, IReadOnlyList<IReadOnlyList<string>> Rows);

    public static void Write(Stream output, IReadOnlyList<Sheet> sheets)
    {
        // ZipArchive 的写入模式在不可定位的流上有限制,先写进内存再整体拷出去
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                string.Concat(sheets.Select((_, i) =>
                    $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
                "</Types>");

            Add(zip, "_rels/.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");

            Add(zip, "xl/workbook.xml",
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                string.Concat(sheets.Select((s, i) => $"<sheet name=\"{Escape(SheetName(s.Name))}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) +
                "</sheets></workbook>");

            Add(zip, "xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(sheets.Select((_, i) =>
                    $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
                $"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "</Relationships>");

            // 样式 0 = 普通,1 = 表头(加粗 + 浅灰底)
            Add(zip, "xl/styles.xml",
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE5E7EB\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/></cellXfs>" +
                "</styleSheet>");

            for (var i = 0; i < sheets.Count; i++)
                Add(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i]));
        }

        buffer.Position = 0;
        buffer.CopyTo(output);
    }

    private static string SheetXml(Sheet sheet)
    {
        var columnCount = sheet.Rows.Count == 0 ? 0 : sheet.Rows.Max(r => r.Count);
        var sb = new StringBuilder();
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");

        if (columnCount > 0)
        {
            sb.Append("<cols>");
            for (var c = 0; c < columnCount; c++)
            {
                // 列宽按最长内容估算,中日韩文字算两个字符宽
                var width = sheet.Rows.Max(r => c < r.Count ? DisplayWidth(r[c]) : 0);
                sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{Math.Clamp(width + 3, 8, 60)}\" customWidth=\"1\"/>");
            }
            sb.Append("</cols>");
        }

        sb.Append("<sheetData>");
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < sheet.Rows[r].Count; c++)
            {
                var text = sheet.Rows[r][c];
                if (string.IsNullOrEmpty(text) && r > 0) continue;
                sb.Append($"<c r=\"{ColumnName(c)}{r + 1}\" t=\"inlineStr\"{(r == 0 ? " s=\"1\"" : "")}><is><t xml:space=\"preserve\">{Escape(text)}</t></is></c>");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");

        if (columnCount > 0 && sheet.Rows.Count > 0)
            sb.Append($"<autoFilter ref=\"A1:{ColumnName(columnCount - 1)}{sheet.Rows.Count}\"/>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static int DisplayWidth(string text) => text.Sum(ch => ch > 0x2E7F ? 2 : 1);

    private static string ColumnName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26)
            name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    /// <summary>工作表名最长 31 个字符,不能含 \ / ? * [ ] :。</summary>
    private static string SheetName(string name)
    {
        var cleaned = new string(name.Select(ch => "\\/?*[]:".Contains(ch) ? '_' : ch).ToArray());
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static string Escape(string text) =>
        SecurityElement.Escape(new string(text.Where(ch => ch >= 0x20 || ch is '\t' or '\n' or '\r').ToArray())) ?? "";

    private static void Add(ZipArchive zip, string path, string xml)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        writer.Write(xml);
    }
}
