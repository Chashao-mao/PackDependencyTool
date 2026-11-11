namespace PackDepTool.App.Services;

public sealed record ModImportQuery(string Raw, string Value, string Platform, bool IsUrl);

/// <summary>Parses a human-written mod list line into a platform + project identity.</summary>
public static class ModImportParser
{
    public static ModImportQuery Parse(string line)
    {
        var raw = line.Trim();
        var value = raw.Split([',', ';'], StringSplitOptions.TrimEntries)[0].Trim();
        if (value.Length == 0) return new ModImportQuery(raw, string.Empty, "空行", false);

        foreach (var (prefix, platform) in new[]
                 {
                     ("modrinth:", "Modrinth"),
                     ("curseforge:", "CurseForge"),
                 })
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            return new ModImportQuery(raw, value[prefix.Length..].Trim(), platform, false);
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            var platform = host.Contains("curseforge", StringComparison.OrdinalIgnoreCase)
                ? "CurseForge"
                : host.Contains("modrinth", StringComparison.OrdinalIgnoreCase)
                    ? "Modrinth"
                    : "链接";
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var marker = Array.FindIndex(segments, segment =>
                segment.Equals("mod", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("mc-mods", StringComparison.OrdinalIgnoreCase));
            var slug = marker >= 0 && marker + 1 < segments.Length
                ? segments[marker + 1]
                : segments.LastOrDefault() ?? value;
            return new ModImportQuery(raw, slug, platform, true);
        }

        return new ModImportQuery(raw, value, "搜索", false);
    }

    public static IReadOnlyList<ModImportQuery> ParseLines(string text)
    {
        return text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(Parse)
            .Where(query => query.Value.Length > 0)
            .ToList();
    }
}
