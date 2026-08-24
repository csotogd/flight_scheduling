using System.IO.Compression;
using System.Text;

namespace Acsp.Web;

/// <summary>
/// Minimal .xlsx writer (OpenXML spreadsheet with inline strings) — no external dependencies.
/// Cells may be string, double/int (numeric) or null.
/// </summary>
public static class XlsxWriter
{
    public static byte[] Build(params (string Name, IEnumerable<object?[]> Rows)[] sheets)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", ContentTypes(sheets.Length));
            Add(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Add(zip, "xl/workbook.xml", Workbook(sheets));
            Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Length));
            for (int i = 0; i < sheets.Length; i++)
                Add(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(sheets[i].Rows));
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content.TrimStart());
    }

    private static string ContentTypes(int n)
    {
        var sb = new StringBuilder("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            <Default Extension="xml" ContentType="application/xml"/>
            <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
            """);
        for (int i = 1; i <= n; i++)
            sb.Append($"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        return sb.Append("</Types>").ToString();
    }

    private static string Workbook((string Name, IEnumerable<object?[]> Rows)[] sheets)
    {
        var sb = new StringBuilder("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>
            """);
        for (int i = 0; i < sheets.Length; i++)
            sb.Append($"""<sheet name="{Esc(sheets[i].Name)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
        return sb.Append("</sheets></workbook>").ToString();
    }

    private static string WorkbookRels(int n)
    {
        var sb = new StringBuilder("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            """);
        for (int i = 1; i <= n; i++)
            sb.Append($"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>""");
        return sb.Append("</Relationships>").ToString();
    }

    private static string Sheet(IEnumerable<object?[]> rows)
    {
        var sb = new StringBuilder("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
            """);
        int r = 1;
        foreach (var row in rows)
        {
            sb.Append($"<row r=\"{r}\">");
            for (int c = 0; c < row.Length; c++)
            {
                var v = row[c];
                if (v is null) continue;
                string cell = Col(c) + r;
                if (v is double or float or int or long or decimal)
                    sb.Append($"""<c r="{cell}"><v>{Convert.ToDouble(v).ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>""");
                else
                    sb.Append($"""<c r="{cell}" t="inlineStr"><is><t xml:space="preserve">{Esc(v.ToString()!)}</t></is></c>""");
            }
            sb.Append("</row>");
            r++;
        }
        return sb.Append("</sheetData></worksheet>").ToString();
    }

    private static string Col(int i)
    {
        string s = "";
        i++;
        while (i > 0) { i--; s = (char)('A' + i % 26) + s; i /= 26; }
        return s;
    }

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
