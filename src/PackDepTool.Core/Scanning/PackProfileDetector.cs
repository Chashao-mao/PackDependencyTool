using System.Globalization;
using System.Text.Json;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Scanning;

/// <summary>
/// Detects Minecraft version / loader / pack name from launcher metadata
/// (Modrinth pack, Prism/MultiMC, CurseForge instance or manifest).
/// Falls back to inferring from parsed mod metadata when no launcher files exist.
/// </summary>
public sealed class PackProfileDetector
{
    public PackProfile Detect(string packRoot, IReadOnlyList<ParsedMod>? parsedMods = null)
    {
        var profile = new PackProfile { PackName = SafeFolderName(packRoot) };

        if (TryModrinth(packRoot, profile) ||
            TryPrism(packRoot, profile) ||
            TryCurseForgeInstance(packRoot, profile) ||
            TryCurseForgeManifest(packRoot, profile))
        {
            return profile;
        }

        if (parsedMods is { Count: > 0 })
            InferFromMods(parsedMods, profile);
        else
        {
            profile.McVersion = "unknown";
            profile.Loader = LoaderKind.Unknown;
            profile.DetectedFrom = "未找到加载器元数据";
        }

        return profile;
    }

    private static bool TryModrinth(string root, PackProfile profile)
    {
        var path = FindFile(root, "modrinth.index.json");
        if (path is null) return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var rootElement = doc.RootElement;
            if (rootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                profile.PackName = name.GetString();

            if (rootElement.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var dep in deps.EnumerateObject())
                {
                    var value = dep.Value.ValueKind == JsonValueKind.String ? dep.Value.GetString() : null;
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    var key = dep.Name.ToLowerInvariant();
                    if (key == "minecraft")
                    {
                        profile.McVersion = value;
                    }
                    else if (key.Contains("fabric"))
                    {
                        profile.Loader = LoaderKind.Fabric;
                        profile.LoaderVersion = value;
                    }
                    else if (key.Contains("quilt"))
                    {
                        profile.Loader = LoaderKind.Quilt;
                        profile.LoaderVersion = value;
                    }
                    else if (key.Contains("neoforge"))
                    {
                        profile.Loader = LoaderKind.NeoForge;
                        profile.LoaderVersion = value;
                    }
                    else if (key.Contains("forge"))
                    {
                        profile.Loader = LoaderKind.Forge;
                        profile.LoaderVersion = value;
                    }
                }
            }

            profile.DetectedFrom = "modrinth.index.json";
            profile.IsInferred = false;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            profile.Warnings.Add($"modrinth.index.json 解析失败：{ex.Message}");
            return false;
        }
    }

    private static bool TryPrism(string root, PackProfile profile)
    {
        var path = FindFile(root, "mmc-pack.json");
        if (path is null) return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("components", out var components) ||
                components.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var component in components.EnumerateArray())
            {
                var uid = component.TryGetProperty("uid", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                var version = component.TryGetProperty("version", out var v) ? v.GetString() : null;
                var uidLower = uid.ToLowerInvariant();
                if (uidLower == "net.minecraft")
                {
                    if (!string.IsNullOrWhiteSpace(version)) profile.McVersion = version!;
                }
                else if (uidLower.Contains("fabric"))
                {
                    profile.Loader = LoaderKind.Fabric;
                    profile.LoaderVersion = version;
                }
                else if (uidLower.Contains("quilt"))
                {
                    profile.Loader = LoaderKind.Quilt;
                    profile.LoaderVersion = version;
                }
                else if (uidLower.Contains("neoforge"))
                {
                    profile.Loader = LoaderKind.NeoForge;
                    profile.LoaderVersion = version;
                }
                else if (uidLower.Contains("forge"))
                {
                    profile.Loader = LoaderKind.Forge;
                    profile.LoaderVersion = version;
                }
            }

            profile.DetectedFrom = "mmc-pack.json";
            profile.IsInferred = false;
            return profile.Loader != LoaderKind.Unknown || profile.McVersion != "unknown";
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            profile.Warnings.Add($"mmc-pack.json 解析失败：{ex.Message}");
            return false;
        }
    }

    private static bool TryCurseForgeInstance(string root, PackProfile profile)
    {
        var path = FindFile(root, "minecraftinstance.json");
        if (path is null) return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var rootElement = doc.RootElement;

            if (rootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                profile.PackName = name.GetString();

            string? loaderName = null;
            string? mcVersion = null;
            if (rootElement.TryGetProperty("baseModLoader", out var loader) && loader.ValueKind == JsonValueKind.Object)
            {
                if (loader.TryGetProperty("name", out var n)) loaderName = n.GetString();
                if (loader.TryGetProperty("minecraftVersion", out var mv)) mcVersion = mv.GetString();
                if (mcVersion is null && loader.TryGetProperty("forgeVersion", out var fv)) mcVersion = null;
            }

            if (mcVersion is null && rootElement.TryGetProperty("gameVersion", out var gv))
                mcVersion = gv.GetString();

            if (!string.IsNullOrWhiteSpace(mcVersion)) profile.McVersion = mcVersion!;
            if (!string.IsNullOrWhiteSpace(loaderName))
            {
                ApplyLoaderToken(loaderName!, profile);
                profile.LoaderVersion ??= ExtractLoaderVersion(loaderName!);
            }

            profile.DetectedFrom = "minecraftinstance.json";
            profile.IsInferred = false;
            return profile.McVersion != "unknown" || profile.Loader != LoaderKind.Unknown;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            profile.Warnings.Add($"minecraftinstance.json 解析失败：{ex.Message}");
            return false;
        }
    }

    private static bool TryCurseForgeManifest(string root, PackProfile profile)
    {
        var path = FindFile(root, "manifest.json");
        if (path is null) return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var rootElement = doc.RootElement;

            if (rootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                profile.PackName = name.GetString();

            if (rootElement.TryGetProperty("minecraft", out var minecraft) && minecraft.ValueKind == JsonValueKind.Object)
            {
                if (minecraft.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    profile.McVersion = v.GetString()!;

                if (minecraft.TryGetProperty("modLoaders", out var loaders) && loaders.ValueKind == JsonValueKind.Array)
                {
                    foreach (var loader in loaders.EnumerateArray())
                    {
                        var id = loader.TryGetProperty("id", out var i) ? i.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            ApplyLoaderToken(id!, profile);
                            profile.LoaderVersion ??= ExtractLoaderVersion(id!);
                            break;
                        }
                    }
                }
            }

            profile.DetectedFrom = "manifest.json";
            profile.IsInferred = false;
            return profile.McVersion != "unknown" || profile.Loader != LoaderKind.Unknown;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            profile.Warnings.Add($"manifest.json 解析失败：{ex.Message}");
            return false;
        }
    }

    private static void InferFromMods(IReadOnlyList<ParsedMod> mods, PackProfile profile)
    {
        profile.IsInferred = true;
        profile.DetectedFrom = "从模组元数据推断";

        var loaderGroups = mods
            .Where(m => m.Loader != LoaderKind.Unknown)
            .GroupBy(m => m.Loader)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .ToList();
        if (loaderGroups.Count > 0)
            profile.Loader = loaderGroups[0].Key;

        var mcCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            foreach (var mc in mod.McVersions)
            {
                if (string.IsNullOrWhiteSpace(mc) || mc == "*") continue;
                mcCounts[mc] = mcCounts.GetValueOrDefault(mc) + 1;
            }

            var exact = TryGetExactVersion(mod.McVersionRange);
            if (exact is not null)
                mcCounts[exact] = mcCounts.GetValueOrDefault(exact) + 1;
        }

        if (mcCounts.Count > 0)
        {
            profile.McVersion = mcCounts
                .OrderByDescending(kv => kv.Value)
                .ThenByDescending(kv => kv.Key, StringComparer.Ordinal)
                .First().Key;
        }
    }

    private static string? TryGetExactVersion(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        var text = range.Trim();
        if (text.Length == 0) return null;
        if (text.IndexOfAny(['<', '>', '=', '~', '^', '*', 'x', 'X', '[', '(', '|', ',']) >= 0)
            return null;
        return text;
    }

    private static void ApplyLoaderToken(string token, PackProfile profile)
    {
        var lower = token.ToLowerInvariant();
        if (lower.Contains("neoforge"))
        {
            profile.Loader = LoaderKind.NeoForge;
        }
        else if (lower.Contains("quilt"))
        {
            profile.Loader = LoaderKind.Quilt;
        }
        else if (lower.Contains("fabric"))
        {
            profile.Loader = LoaderKind.Fabric;
        }
        else if (lower.Contains("forge"))
        {
            profile.Loader = LoaderKind.Forge;
        }
    }

    public static string? ExtractLoaderVersion(string token)
    {
        var lower = token.ToLowerInvariant();
        var markers = new[] { "neoforge-", "fabric-loader-", "fabric-", "quilt-loader-", "quilt-", "forge-", "neoforge", "forge" };
        foreach (var marker in markers)
        {
            var index = lower.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) continue;
            var start = index + marker.Length;
            if (start < token.Length && token[start] == '-') start++;
            if (start >= token.Length) continue;
            var candidate = token[start..].Trim();
            if (candidate.Length > 0 && char.IsDigit(candidate[0])) return candidate;
        }

        return null;
    }

    private static string? FindFile(string root, string name)
    {
        var direct = Path.Combine(root, name);
        if (File.Exists(direct)) return direct;

        try
        {
            return Directory
                .EnumerateFiles(root, name, SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string SafeFolderName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "未命名整合包" : name;
    }
}
