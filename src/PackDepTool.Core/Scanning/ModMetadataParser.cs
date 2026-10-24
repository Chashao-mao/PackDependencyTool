using System.Globalization;
using System.Text.Json;
using PackDepTool.Core.Models;
using Tomlyn;
using Tomlyn.Model;

namespace PackDepTool.Core.Scanning;

/// <summary>
/// Parses <c>fabric.mod.json</c>, <c>quilt.mod.json</c>, <c>META-INF/mods.toml</c>,
/// <c>META-INF/neoforge.mods.toml</c> and legacy <c>mcmod.info</c> into a common shape.
/// Malformed metadata raises <see cref="FormatException"/> with a user friendly message.
/// </summary>
public sealed class ModMetadataParser
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public ParsedMod ParseFabric(string json)
    {
        using var doc = ParseJson(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("fabric.mod.json 根节点不是对象");

        var id = NormalizeId(GetString(root, "id"));
        if (id.Length == 0)
            throw new FormatException("fabric.mod.json 缺少 id");

        var mod = new ParsedMod
        {
            ModId = id,
            Name = FirstNonEmpty(GetString(root, "name"), id),
            Version = FirstNonEmpty(GetString(root, "version"), "unknown"),
            Loader = LoaderKind.Fabric,
            Description = GetString(root, "description"),
            Side = ParseFabricSide(GetString(root, "environment")),
            Authors = ParseFabricAuthors(root),
            IconEntry = ParseIconPath(root),
        };

        if (root.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in provides.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = NormalizeId(item.GetString());
                    if (value.Length > 0) mod.Provides.Add(value);
                }
            }
        }

        AddFabricGroup(root, "depends", EdgeType.Requires, mod);
        AddFabricGroup(root, "breaks", EdgeType.Incompatible, mod);
        AddFabricGroup(root, "recommends", EdgeType.Recommends, mod);
        AddFabricGroup(root, "suggests", EdgeType.Optional, mod);

        var depends = TryGetObject(root, "depends");
        var minecraftRange = depends is { } d && d.TryGetProperty("minecraft", out var mc) ? RangeFrom(mc) : null;
        if (!string.IsNullOrWhiteSpace(minecraftRange))
        {
            mod.McVersionRange = minecraftRange;
            var exact = ExactVersion(minecraftRange);
            if (exact is not null) mod.McVersions.Add(exact);
        }

        if (root.TryGetProperty("jars", out var jars) && jars.ValueKind == JsonValueKind.Array)
        {
            foreach (var jar in jars.EnumerateArray())
            {
                if (jar.ValueKind != JsonValueKind.Object) continue;
                var file = GetString(jar, "file");
                if (!string.IsNullOrWhiteSpace(file))
                {
                    mod.Embedded.Add(new ParsedEmbedded
                    {
                        ModId = string.Empty,
                        File = file,
                        Loader = LoaderKind.Fabric,
                    });
                }
            }
        }

        return mod;
    }

    public ParsedMod ParseQuilt(string json)
    {
        using var doc = ParseJson(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("quilt_loader", out var loader) ||
            loader.ValueKind != JsonValueKind.Object)
            throw new FormatException("quilt.mod.json 缺少 quilt_loader 节点");

        var id = NormalizeId(GetString(loader, "id"));
        if (id.Length == 0)
            throw new FormatException("quilt.mod.json 缺少 quilt_loader.id");

        var metadata = TryGetObject(loader, "metadata");
        var mod = new ParsedMod
        {
            ModId = id,
            Name = FirstNonEmpty(metadata is { } m ? GetString(m, "name") : null, id),
            Version = FirstNonEmpty(GetString(loader, "version"), "unknown"),
            Loader = LoaderKind.Quilt,
            Description = metadata is { } md ? GetString(md, "description") : null,
            Side = ParseFabricSide(GetString(loader, "environment")),
            IconEntry = metadata is { } iconMeta ? ParseIconPath(iconMeta) : null,
        };

        if (metadata is { } meta && meta.TryGetProperty("contributors", out var contributors) &&
            contributors.ValueKind == JsonValueKind.Object)
        {
            mod.Authors = string.Join(", ", contributors.EnumerateObject().Select(p => p.Name));
        }

        if (loader.TryGetProperty("provides", out var provides) && provides.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in provides.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = NormalizeId(item.GetString());
                    if (value.Length > 0) mod.Provides.Add(value);
                }
            }
        }

        AddQuiltGroup(loader, "depends", EdgeType.Requires, mod);
        AddQuiltGroup(loader, "breaks", EdgeType.Incompatible, mod);

        var minecraft = mod.Dependencies.FirstOrDefault(d => d.ModId == "minecraft");
        if (minecraft is not null)
        {
            mod.McVersionRange = minecraft.VersionRange;
            var exact = ExactVersion(minecraft.VersionRange);
            if (exact is not null) mod.McVersions.Add(exact);
        }

        return mod;
    }

    public List<ParsedMod> ParseForgeToml(string toml, string source, bool neoForge)
    {
        TomlTable model;
        try
        {
            var syntax = Toml.Parse(toml, source);
            if (syntax.HasErrors)
            {
                var message = string.Join("; ", syntax.Diagnostics.Select(d => d.ToString()));
                throw new FormatException($"TOML 语法错误：{message}");
            }
            model = Toml.ToModel(syntax);
        }
        catch (TomlException ex)
        {
            throw new FormatException($"TOML 解析失败：{ex.Message}", ex);
        }

        if (!TryGetValue(model, "mods", out var modsValue) || modsValue is not TomlTableArray mods || mods.Count == 0)
            throw new FormatException("mods 声明缺失（需要 [[mods]] 表）");

        var loader = neoForge ? LoaderKind.NeoForge : LoaderKind.Forge;
        var dependencyGroups = TryGetValue(model, "dependencies", out var depValue) && depValue is TomlTable depTable
            ? depTable
            : null;

        var results = new List<ParsedMod>();
        foreach (var table in mods)
        {
            var id = NormalizeId(GetTomlString(table, "modId"));
            if (id.Length == 0)
            {
                results.Add(new ParsedMod
                {
                    ModId = string.Empty,
                    Name = "unknown",
                    Loader = loader,
                    Warnings = { "[[mods]] 条目缺少 modId，已跳过" },
                });
                continue;
            }

            var mod = new ParsedMod
            {
                ModId = id,
                Name = FirstNonEmpty(GetTomlString(table, "displayName"), id),
                Version = FirstNonEmpty(GetTomlString(table, "version"), "unknown"),
                Loader = loader,
                Description = GetTomlString(table, "description"),
                Authors = GetTomlString(table, "authors"),
                IconEntry = GetTomlString(table, "logoFile") ?? GetTomlString(model, "logoFile"),
            };

            var mcRange = GetTomlString(table, "mcVersion") ?? GetTomlString(table, "acceptableMinecraftVersions");
            if (!string.IsNullOrWhiteSpace(mcRange))
            {
                mod.McVersionRange = mcRange;
                var exact = ExactVersion(mcRange);
                if (exact is not null) mod.McVersions.Add(exact);
            }

            if (dependencyGroups is not null)
            {
                var matching = FindTableKey(dependencyGroups, id);
                if (matching is not null && dependencyGroups[matching] is TomlTableArray deps)
                {
                    foreach (var dep in deps)
                        AddForgeDependency(dep, mod, neoForge);
                }
            }

            results.Add(mod);
        }

        return results;
    }

    public List<ParsedMod> ParseLegacyMcmodInfo(string json)
    {
        using var doc = ParseJson(json);
        var root = doc.RootElement;

        IEnumerable<JsonElement> items;
        if (root.ValueKind == JsonValueKind.Array)
        {
            items = root.EnumerateArray();
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("modList", out var modList) &&
                 modList.ValueKind == JsonValueKind.Array)
        {
            items = modList.EnumerateArray();
        }
        else
        {
            throw new FormatException("mcmod.info 结构无法识别");
        }

        var results = new List<ParsedMod>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = NormalizeId(GetString(item, "modid"));
            if (id.Length == 0) id = NormalizeId(GetString(item, "modId"));
            if (id.Length == 0) continue;

            var mod = new ParsedMod
            {
                ModId = id,
                Name = FirstNonEmpty(GetString(item, "name"), id),
                Version = FirstNonEmpty(GetString(item, "version"), "unknown"),
                Loader = LoaderKind.Forge,
                Description = GetString(item, "description"),
                IconEntry = GetString(item, "logoFile"),
            };

            var mcVersion = GetString(item, "mcversion") ?? GetString(item, "acceptableMinecraftVersions");
            if (!string.IsNullOrWhiteSpace(mcVersion))
            {
                mod.McVersions.Add(mcVersion!);
                mod.McVersionRange = mcVersion;
            }

            if (item.TryGetProperty("authorList", out var authors) && authors.ValueKind == JsonValueKind.Array)
                mod.Authors = string.Join(", ", authors.EnumerateArray()
                    .Where(a => a.ValueKind == JsonValueKind.String)
                    .Select(a => a.GetString()));

            if (item.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
            {
                foreach (var dep in deps.EnumerateArray())
                {
                    if (dep.ValueKind != JsonValueKind.String) continue;
                    var raw = dep.GetString();
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    var parts = raw.Split('@', 2);
                    var depId = NormalizeId(parts[0]);
                    if (depId.Length == 0) continue;

                    mod.Dependencies.Add(new ParsedDependency
                    {
                        ModId = depId,
                        Type = EdgeType.Requires,
                        VersionRange = parts.Length > 1 ? parts[1].Trim() : null,
                        Side = ModSide.Unknown,
                    });
                }
            }

            results.Add(mod);
        }

        return results;
    }

    // ------------------------------------------------------------------

    private static void AddFabricGroup(JsonElement root, string property, EdgeType type, ParsedMod mod)
    {
        if (!root.TryGetProperty(property, out var group) || group.ValueKind != JsonValueKind.Object)
            return;

        foreach (var dep in group.EnumerateObject())
        {
            var depId = NormalizeId(dep.Name);
            if (depId.Length == 0) continue;
            if (type == EdgeType.Requires && depId == "minecraft")
                continue; // captured as the pack's MC constraint

            mod.Dependencies.Add(new ParsedDependency
            {
                ModId = depId,
                Type = type,
                VersionRange = RangeFrom(dep.Value),
                Side = ModSide.Unknown,
            });
        }
    }

    private static void AddQuiltGroup(JsonElement loader, string property, EdgeType type, ParsedMod mod)
    {
        if (!loader.TryGetProperty(property, out var group) || group.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in group.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var simpleId = NormalizeId(entry.GetString());
                if (simpleId.Length > 0)
                {
                    mod.Dependencies.Add(new ParsedDependency
                    {
                        ModId = simpleId,
                        Type = type,
                        VersionRange = null,
                    });
                }
                continue;
            }

            if (entry.ValueKind != JsonValueKind.Object) continue;

            var depId = NormalizeId(GetString(entry, "id"));
            if (depId.Length == 0) continue;

            var optional = false;
            var note = (string?)null;
            if (entry.TryGetProperty("optional", out var optionalElement) &&
                optionalElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                optional = optionalElement.GetBoolean();

            if (entry.TryGetProperty("unless", out _))
            {
                optional = true;
                note = "条件依赖";
            }

            var effectiveType = type;
            if (type == EdgeType.Requires && optional)
                effectiveType = EdgeType.Optional;

            mod.Dependencies.Add(new ParsedDependency
            {
                ModId = depId,
                Type = effectiveType,
                VersionRange = entry.TryGetProperty("versions", out var versions) ? RangeFrom(versions) : null,
                Side = ModSide.Unknown,
                Note = note,
            });
        }
    }

    private static void AddForgeDependency(TomlTable table, ParsedMod mod, bool neoForge)
    {
        var depId = NormalizeId(GetTomlString(table, "modId"));
        if (depId.Length == 0) return;

        var typeText = GetTomlString(table, "type")?.Trim().ToLowerInvariant();
        var mandatory = GetTomlBool(table, "mandatory");

        EdgeType type;
        string? note = null;
        switch (typeText)
        {
            case "required":
                type = EdgeType.Requires;
                break;
            case "optional":
                type = EdgeType.Optional;
                break;
            case "incompatible":
                type = EdgeType.Incompatible;
                break;
            case "embedded":
                type = EdgeType.Embedded;
                break;
            case "discouraged":
                type = EdgeType.Optional;
                note = "NeoForge 标记为 discouraged";
                break;
            case "tool":
                return;
            case null or "":
                type = mandatory == false ? EdgeType.Optional : EdgeType.Requires;
                break;
            default:
                type = mandatory == false ? EdgeType.Optional : EdgeType.Requires;
                note = $"未知依赖类型：{typeText}";
                break;
        }

        if (type == EdgeType.Requires && depId == "minecraft")
        {
            var range = GetTomlString(table, "versionRange");
            if (!string.IsNullOrWhiteSpace(range))
            {
                mod.McVersionRange = range;
                var exact = ExactVersion(range);
                if (exact is not null) mod.McVersions.Add(exact);
            }
            return;
        }

        mod.Dependencies.Add(new ParsedDependency
        {
            ModId = depId,
            Type = type,
            VersionRange = GetTomlString(table, "versionRange"),
            Side = ParseSide(GetTomlString(table, "side")),
            Note = note,
        });
    }

    private static string? RangeFrom(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Array => string.Join(" || ", element.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString())
                .Where(v => !string.IsNullOrWhiteSpace(v))),
            _ => null,
        };
    }

    private static string? ParseFabricAuthors(JsonElement root)
    {
        if (!root.TryGetProperty("authors", out var authors)) return null;
        if (authors.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var author in authors.EnumerateArray())
            {
                if (author.ValueKind == JsonValueKind.String)
                {
                    var value = author.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) names.Add(value!);
                }
                else if (author.ValueKind == JsonValueKind.Object)
                {
                    var name = GetString(author, "name") ?? GetString(author, "contact");
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
                }
            }
            return names.Count > 0 ? string.Join(", ", names) : null;
        }
        return null;
    }

    private static ModSide ParseFabricSide(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "*" or "both" or "universal" => ModSide.Both,
        "client" => ModSide.Client,
        "server" => ModSide.Server,
        _ => ModSide.Unknown,
    };

    private static ModSide ParseSide(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "both" or "*" or "universal" => ModSide.Both,
        "client" => ModSide.Client,
        "server" => ModSide.Server,
        _ => ModSide.Unknown,
    };

    private static string? ParseIconPath(JsonElement root)
    {
        if (!root.TryGetProperty("icon", out var icon)) return null;

        if (icon.ValueKind == JsonValueKind.String)
        {
            var path = icon.GetString();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        if (icon.ValueKind != JsonValueKind.Object) return null;

        string? best = null;
        var bestSize = -1L;
        foreach (var entry in icon.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String) continue;
            var path = entry.Value.GetString();
            if (string.IsNullOrWhiteSpace(path)) continue;

            var size = long.TryParse(entry.Name, out var parsed) ? parsed : 0;
            if (size >= bestSize)
            {
                bestSize = size;
                best = path;
            }
        }

        return best;
    }

    private static JsonDocument ParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"JSON 解析失败：{ex.Message}", ex);
        }
    }

    private static string NormalizeId(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string FirstNonEmpty(string? first, string fallback) =>
        string.IsNullOrWhiteSpace(first) ? fallback : first;

    private static string? ExactVersion(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        var text = range.Trim();
        if (text.IndexOfAny(['<', '>', '=', '~', '^', '*', 'x', 'X', '[', '(', '|', ',', ' ']) >= 0)
            return null;
        return text;
    }

    private static JsonElement? TryGetObject(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetValue(TomlTable table, string key, out object? value)
    {
        if (table.TryGetValue(key, out value)) return true;

        foreach (var pair in table)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? FindTableKey(TomlTable table, string key)
    {
        foreach (var pair in table)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                return pair.Key;
        }
        return null;
    }

    private static string? GetTomlString(TomlTable table, string key) =>
        TryGetValue(table, key, out var value)
            ? value switch
            {
                string s => s,
                long l => l.ToString(CultureInfo.InvariantCulture),
                double d => d.ToString(CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                _ => value?.ToString(),
            }
            : null;

    private static bool? GetTomlBool(TomlTable table, string key)
    {
        if (!TryGetValue(table, key, out var value)) return null;
        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }
}
