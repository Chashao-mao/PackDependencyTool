using System.IO.Compression;
using System.Text.Json;

namespace PackDepTool.Core.Scanning;

/// <summary>
/// Reads Chinese names from CFPA I18nUpdateMod resource packs
/// (Minecraft-Mod-Language-Modpack-Converted-*.zip). These are lower priority
/// than a name embedded directly in the mod JAR.
/// </summary>
public static class I18nPackService
{
    private const int MaxPacks = 24;
    private const long MaxLangEntryBytes = 2 * 1024 * 1024;

    public static Dictionary<string, string> LoadNames(string instanceRoot)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in CandidateDirectories(instanceRoot))
        {
            if (!Directory.Exists(directory)) continue;
            IEnumerable<string> packs;
            try
            {
                packs = Directory.EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly)
                    .Where(path => Path.GetFileName(path).Contains("Language", StringComparison.OrdinalIgnoreCase) ||
                                   Path.GetFileName(path).Contains("I18n", StringComparison.OrdinalIgnoreCase) ||
                                   Path.GetFileName(path).Contains("i18n", StringComparison.Ordinal))
                    .Take(MaxPacks)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var pack in packs)
            {
                try
                {
                    using var archive = ZipFile.OpenRead(pack);
                    foreach (var entry in archive.Entries.Where(entry =>
                                 entry.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) &&
                                 entry.FullName.EndsWith("/lang/zh_cn.json", StringComparison.OrdinalIgnoreCase) &&
                                 entry.Length is > 0 and <= MaxLangEntryBytes))
                    {
                        using var stream = entry.Open();
                        using var document = JsonDocument.Parse(stream);
                        if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                        foreach (var property in document.RootElement.EnumerateObject())
                        {
                            if (property.Value.ValueKind != JsonValueKind.String) continue;
                            var value = property.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                                names[property.Name] = value.Trim();
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
                {
                    // Ignore a malformed/unfinished I18n pack and keep other candidates.
                }
            }
        }

        return names;
    }

    public static string? ResolveName(string modId, IReadOnlyDictionary<string, string> names)
    {
        if (string.IsNullOrWhiteSpace(modId) || names.Count == 0) return null;
        foreach (var key in new[]
                 {
                     $"modmenu.nameTranslation.{modId}",
                     $"{modId}.name",
                     $"{modId}.modname",
                     $"itemGroup.{modId}",
                     $"mod.{modId}.name",
                 })
        {
            if (names.TryGetValue(key, out var value)) return value;
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirectories(string instanceRoot)
    {
        yield return Path.Combine(instanceRoot, "resourcepacks");
        var versions = Directory.GetParent(instanceRoot);
        if (versions?.Parent is { } gameRoot)
        {
            yield return Path.Combine(gameRoot.FullName, "resourcepacks");
            yield return Path.Combine(gameRoot.FullName, "versions", "resourcepacks");
        }
    }
}
