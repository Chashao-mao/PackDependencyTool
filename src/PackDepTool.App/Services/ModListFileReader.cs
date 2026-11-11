using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace PackDepTool.App.Services;

/// <summary>Reads the first human-editable column from CSV/TSV/XLSX mod lists.</summary>
public static class ModListFileReader
{
    public static async Task<List<string>> ReadFirstColumnAsync(string path, CancellationToken cancellationToken = default)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xlsx" => await ReadXlsxAsync(path, cancellationToken),
            ".tsv" => await ReadDelimitedAsync(path, '\t', cancellationToken),
            ".csv" => await ReadDelimitedAsync(path, ',', cancellationToken),
            _ => await ReadDelimitedAsync(path, ',', cancellationToken),
        };
    }

    private static async Task<List<string>> ReadDelimitedAsync(
        string path, char delimiter, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        var result = new List<string>();
        foreach (var line in lines)
        {
            var cell = FirstCell(line, delimiter).Trim();
            if (cell.Length > 0) result.Add(cell);
        }
        return result;
    }

    private static string FirstCell(string line, char delimiter)
    {
        if (line.Length == 0) return string.Empty;
        if (line[0] == '"')
        {
            var builder = new StringBuilder();
            for (var i = 1; i < line.Length; i++)
            {
                if (line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    builder.Append('"');
                    i++;
                }
                else if (line[i] == '"') break;
                else builder.Append(line[i]);
            }
            return builder.ToString();
        }
        var index = line.IndexOf(delimiter);
        return index < 0 ? line : line[..index];
    }

    private static async Task<List<string>> ReadXlsxAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var shared = await ReadSharedStringsAsync(archive, cancellationToken);
        var sheet = archive.Entries.FirstOrDefault(entry =>
                        entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
                        entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    ?? archive.Entries.FirstOrDefault(entry =>
                        entry.FullName.Equals("xl/worksheets/sheet1.xml", StringComparison.OrdinalIgnoreCase));
        if (sheet is null) return [];

        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        await using var sheetStream = sheet.Open();
        var document = XDocument.Load(sheetStream);
        var result = new List<string>();
        foreach (var row in document.Descendants(ns + "row"))
        {
            var cell = row.Elements(ns + "c").FirstOrDefault();
            if (cell is null) continue;
            var value = cell.Attribute("t")?.Value switch
            {
                "s" => shared.ElementAtOrDefault(ParseInt(cell.Element(ns + "v")?.Value)),
                "inlineStr" => cell.Element(ns + "is")?.Value,
                _ => cell.Element(ns + "v")?.Value,
            };
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value.Trim());
        }
        return result;
    }

    private static int ParseInt(string? value) => int.TryParse(value, out var index) ? index : -1;

    private static async Task<List<string>> ReadSharedStringsAsync(
        ZipArchive archive, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        await using var stream = entry.Open();
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document.Descendants(ns + "si")
            .Select(item => string.Concat(item.Descendants(ns + "t").Select(text => text.Value)))
            .ToList();
    }
}
