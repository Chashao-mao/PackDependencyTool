using System.Text.Json.Serialization;

namespace PackDepTool.Core.Models;

/// <summary>Coarse functional role of a mod, used for filtering and pack-author reports.</summary>
public enum ModCategory
{
    [JsonStringEnumMemberName("performance")] Performance,
    [JsonStringEnumMemberName("library")] Library,
    [JsonStringEnumMemberName("ui")] Ui,
    [JsonStringEnumMemberName("world")] World,
    [JsonStringEnumMemberName("tech")] Tech,
    [JsonStringEnumMemberName("magic")] Magic,
    [JsonStringEnumMemberName("adventure")] Adventure,
    [JsonStringEnumMemberName("decoration")] Decoration,
    [JsonStringEnumMemberName("storage")] Storage,
    [JsonStringEnumMemberName("utility")] Utility,
    [JsonStringEnumMemberName("fix")] Fix,
    [JsonStringEnumMemberName("audio")] Audio,
    [JsonStringEnumMemberName("visual")] Visual,
    [JsonStringEnumMemberName("farming")] Farming,
    [JsonStringEnumMemberName("mobs")] Mobs,
    [JsonStringEnumMemberName("compat")] Compat,
    [JsonStringEnumMemberName("modpack")] Modpack,
    [JsonStringEnumMemberName("other")] Other,
}

public static class ModCategoryText
{
    public static string Name(ModCategory category) => category switch
    {
        ModCategory.Performance => "优化",
        ModCategory.Library => "前置/库",
        ModCategory.Ui => "界面/HUD",
        ModCategory.World => "世界/地图",
        ModCategory.Tech => "科技",
        ModCategory.Magic => "魔法",
        ModCategory.Adventure => "冒险/战斗",
        ModCategory.Decoration => "装饰/建筑",
        ModCategory.Storage => "存储",
        ModCategory.Utility => "工具",
        ModCategory.Fix => "修复/兼容",
        ModCategory.Audio => "音效",
        ModCategory.Visual => "视觉/渲染",
        ModCategory.Farming => "农业/食物",
        ModCategory.Mobs => "生物",
        ModCategory.Compat => "兼容/补丁",
        ModCategory.Modpack => "整合包管理",
        _ => "未分类",
    };

    public static IReadOnlyList<ModCategory> All { get; } =
    [
        ModCategory.Performance,
        ModCategory.Library,
        ModCategory.Ui,
        ModCategory.World,
        ModCategory.Tech,
        ModCategory.Magic,
        ModCategory.Adventure,
        ModCategory.Decoration,
        ModCategory.Storage,
        ModCategory.Utility,
        ModCategory.Fix,
        ModCategory.Compat,
        ModCategory.Audio,
        ModCategory.Visual,
        ModCategory.Farming,
        ModCategory.Mobs,
        ModCategory.Modpack,
        ModCategory.Other,
    ];
}
