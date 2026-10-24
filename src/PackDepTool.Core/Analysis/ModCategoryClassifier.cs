using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

/// <summary>
/// Lightweight offline classifier. It scores id/name/description keywords, with exact
/// ids weighted far above descriptions so obvious mods never get mislabelled.
/// </summary>
public sealed class ModCategoryClassifier
{
    private sealed record Rule(ModCategory Category, string[] Terms);

    private static readonly Rule[] Rules =
    [
        new(ModCategory.Performance,
        [
            "sodium", "lithium", "phosphor", "starlight", "ferritecore", "ferrite core", "krypton",
            "c2me", "modernfix", "memoryleakfix", "embeddium", "rubidium", "canary", "saturn",
            "pluto", "redirector", "nvidium", "entityculling", "moreculling", "dynamic fps",
            "fps", "optimizer", "optimization", "optimisation", "performance", "ai improvements", "noisium",
            "threadtweak", "fastload", "badoptimizations", "async", "smooth", "优化", "性能",
        ]),
        new(ModCategory.Library,
        [
            "fabric api", "fabric-api", "architectury", "cloth config", "cloth-config", "geckolib",
            "citadel", "curios", "patchouli", "bookshelf", "balm", "collective", "puzzles lib",
            "midnightlib", "midnight lib", "owo lib", "owo-lib", "cardinal components", "trinkets",
            "forge config api", "kotlin for forge", "yacl", "yet another config", "iceberg",
            "prism lib", "resourceful lib", "supermartijn642configlib", "configured",
            "kotlinforforge", "kotlin for forge", "kotlin", "api", "library", "前置", "库",
        ]),
        new(ModCategory.Ui,
        [
            "jei", "rei", "emi", "jade", "wthit", "appleskin", "betterf3", "modmenu",
            "catalogue", "controlling", "mouse tweaks", "inventory hud", "inventory", "toast control",
            "tooltip", "hud", "gui", "overlay", "zoom", "screenshot", "chat", "界面", "提示",
        ]),
        new(ModCategory.World,
        [
            "journeymap", "xaero", "minimap", "biomes", "terralith", "tectonic", "worldgen",
            "world gen", "structures", "yungs", "yung's", "dungeons", "dimension", "chunk",
            "explorer", "map", "地图", "世界", "结构", "地形",
        ]),
        new(ModCategory.Tech,
        [
            "create", "mekanism", "thermal", "industrial", "immersive engineering", "ae2",
            "applied energistics", "gregtech", "refined storage", "powah", "flux", "machine",
            "factory", "tech", "automation", "logistics", "pipe", "generator", "nuclear",
            "reactor", "computercraft", "cc tweaked", "科技", "机械", "自动化", "能源",
        ]),
        new(ModCategory.Magic,
        [
            "botania", "ars nouveau", "occultism", "blood magic", "astral", "spell", "mana",
            "wizard", "magic", "witch", "runic", "soul", "魔法", "法术", "符文",
        ]),
        new(ModCategory.Adventure,
        [
            "combat", "weapon", "sword", "bow", "epic fight", "paraglider", "artifacts",
            "boss", "rpg", "adventure", "quest", "aether", "twilight", "ice and fire",
            "cataclysm", "apotheosis", "冒险", "战斗", "武器", "任务",
        ]),
        new(ModCategory.Decoration,
        [
            "decorative", "furniture", "macaw", "chipped", "blockus", "supplementaries",
            "handcrafted", "building", "decoration", "painting", "lamp", "装饰", "建筑", "家具",
        ]),
        new(ModCategory.Storage,
        [
            "storage", "drawer", "refined storage", "ae2", "backpack", "chest", "sophisticated",
            "colossal", "barrel", "shulker", "存储", "抽屉", "背包",
        ]),
        new(ModCategory.Utility,
        [
            "waystones", "gravestone", "corpse", "tombstone", "travel", "crafttweaker", "kubejs",
            "guide", "book", "tool", "utility", "工具", "传送", "墓碑",
        ]),
        new(ModCategory.Fix,
        [
            "mixin", "compat", "fix", "patch", "bug", "crash", "memoryleak", "connectivity",
            "integration", "修复", "补丁", "兼容",
        ]),
        new(ModCategory.Audio,
        [
            "sound", "music", "ambience", "voice", "audio", "soundpack", "音效", "音乐",
        ]),
        new(ModCategory.Visual,
        [
            "shader", "iris", "oculus", "optifine", "continuity", "connected", "particle",
            "animation", "visual", "camera", "firstperson", "skin", "render", "graphics",
            "distant horizons", "光影", "渲染", "动画", "视觉",
        ]),
        new(ModCategory.Farming,
        [
            "farmer", "farm", "crop", "food", "cooking", "agricraft", "kitchen", "bakery",
            "harvest", "农业", "食物", "烹饪",
        ]),
        new(ModCategory.Mobs,
        [
            "mob", "creature", "animal", "alexsmobs", "iceandfire", "variants", "zoo", "pets",
            "monster", "生物", "动物", "怪物",
        ]),
        new(ModCategory.Compat,
        [
            "compat", "integration", "patch", "fix", "bridge", "兼容", "联动", "补丁",
        ]),
        new(ModCategory.Modpack,
        [
            "modmenu", "defaultoptions", "packmenu", "modpack", "permissions", "claim", "ftb",
            "teams", "整合包", "服务端", "权限",
        ]),
    ];

    // Normalize terms once instead of doing it for every node/rule pair on
    // every graph rebuild.
    private static readonly Rule[] NormalizedRules = Rules
        .Select(rule => new Rule(rule.Category,
            rule.Terms.Select(Normalize)
                .Where(term => term.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray()))
        .ToArray();

    public void Classify(GraphDocument graph)
    {
        foreach (var node in graph.Nodes)
            Classify(node);
    }

    public void Classify(ModNode node)
    {
        var id = Normalize(node.Id);
        var name = Normalize(node.Name);
        var nameZh = Normalize(node.NameZh);
        var title = Normalize(node.ProjectTitle);
        var description = Normalize(node.Description);

        var best = ModCategory.Other;
        var bestScore = 0;
        var bestTerm = string.Empty;

        foreach (var rule in NormalizedRules)
        {
            var score = 0;
            var matched = string.Empty;
            foreach (var term in rule.Terms)
            {
                if (term.Length == 0) continue;

                var hit = 0;
                if (id == term) hit = 14;
                else if (id.Contains(term, StringComparison.Ordinal)) hit = 8;
                if (name.Contains(term, StringComparison.Ordinal)) hit = Math.Max(hit, 5);
                if (nameZh.Contains(term, StringComparison.Ordinal)) hit = Math.Max(hit, 5);
                if (title.Contains(term, StringComparison.Ordinal)) hit = Math.Max(hit, 4);
                if (description.Contains(term, StringComparison.Ordinal)) hit = Math.Max(hit, 2);

                if (hit > 0)
                {
                    score += hit;
                    if (matched.Length == 0) matched = term;
                }
            }

            if (score > bestScore)
            {
                best = rule.Category;
                bestScore = score;
                bestTerm = matched;
            }
        }

        node.Category = bestScore >= 3 ? best : ModCategory.Other;
        node.CategoryReason = bestScore >= 3 ? bestTerm : null;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '\'' ? ch : ' ')
            .ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
