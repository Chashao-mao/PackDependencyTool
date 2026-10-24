namespace PackDepTool.Core.Scanning;

/// <summary>Reads the handful of version fields used by Forge/NeoForge placeholders.</summary>
public static class JarManifest
{
    private static readonly string[] VersionKeys =
    [
        "Implementation-Version",
        "Specification-Version",
        "Bundle-Version",
        "Mod-Version",
    ];

    public static bool IsLibrary(string? manifestText)
    {
        if (string.IsNullOrWhiteSpace(manifestText)) return false;
        var attributes = ParseAttributes(manifestText);
        return attributes.TryGetValue("FMLModType", out var value) &&
               value.Trim().Equals("LIBRARY", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ReadVersion(string? manifestText)
    {
        if (string.IsNullOrWhiteSpace(manifestText)) return null;

        var attributes = ParseAttributes(manifestText);
        foreach (var key in VersionKeys)
        {
            if (attributes.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim().Trim('"');
        }

        return null;
    }

    private static Dictionary<string, string> ParseAttributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;
        var currentValue = new System.Text.StringBuilder();

        void Flush()
        {
            if (currentKey is null) return;
            result[currentKey] = currentValue.ToString();
            currentKey = null;
            currentValue.Clear();
        }

        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (rawLine.Length == 0)
            {
                Flush();
                continue;
            }

            if (rawLine[0] == ' ')
            {
                currentValue.Append(rawLine[1..]);
                continue;
            }

            Flush();
            var separator = rawLine.IndexOf(':');
            if (separator <= 0) continue;
            currentKey = rawLine[..separator].Trim();
            currentValue.Append(rawLine[(separator + 1)..].TrimStart());
        }

        Flush();
        return result;
    }
}
