using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;

namespace PackDepTool.App.ViewModels;

public enum PackToolsAction
{
    None,
    ExportModList,
    ExportCsv,
    ExportMissing,
    ExportAudit,
    OpenMods,
    OpenConfig,
    OpenResourcePacks,
    CleanDuplicateFiles,
    CleanDuplicateModFiles,
}

public sealed class PackToolsResult
{
    public PackToolsAction Action { get; init; }
    public IReadOnlyList<string> ExtraFiles { get; init; } = [];
}

public sealed class DuplicateModEntry
{
    public DuplicateModEntry(DuplicateModGroup group)
    {
        Name = group.Name;
        ModId = group.ModId;
        FilesText = string.Join(Environment.NewLine, group.Files.Select(file => $"{file.RelativePath} · {file.SizeText}"));
        FileCount = group.Files.Count;
    }

    public string Name { get; }
    public string ModId { get; }
    public string FilesText { get; }
    public int FileCount { get; }
    public string CountText => $"{FileCount} 个 JAR";
}

public sealed class DuplicateHashEntry
{
    public DuplicateHashEntry(DuplicateFileGroup group)
    {
        HashText = group.Sha1.Length > 14 ? group.Sha1[..14] + "…" : group.Sha1;
        SizeText = group.SizeText;
        FilesText = string.Join(Environment.NewLine, group.Files.Select(file => file.RelativePath));
    }

    public string HashText { get; }
    public string SizeText { get; }
    public string FilesText { get; }
}

public sealed class CategoryStatEntry
{
    public CategoryStatEntry(ModCategory category, int count)
    {
        Label = ModCategoryText.Name(category);
        Count = count;
        CountText = count.ToString();
    }

    public string Label { get; }
    public int Count { get; }
    public string CountText { get; }
}

public sealed class MissingModEntry
{
    public MissingModEntry(MissingModInfo missing)
    {
        Name = missing.Name;
        Id = missing.Id;
        Loader = missing.Loader;
    }

    public string Name { get; }
    public string Id { get; }
    public string Loader { get; }
    public string Label => $"{Name} ({Id})";
}

public sealed partial class PackToolsViewModel : ObservableObject
{
    public PackToolsViewModel(PackAuditReport report)
    {
        Report = report;
        foreach (var group in report.DuplicateModIds)
            DuplicateMods.Add(new DuplicateModEntry(group));
        foreach (var group in report.DuplicateHashes)
            DuplicateHashes.Add(new DuplicateHashEntry(group));
        foreach (var missing in report.MissingMods)
            MissingMods.Add(new MissingModEntry(missing));
        foreach (var pair in report.CategoryCounts.OrderByDescending(pair => pair.Value))
            CategoryStats.Add(new CategoryStatEntry(pair.Key, pair.Value));
        LoaderText = report.LoaderText;
        SideText = report.SideText;
    }

    public PackAuditReport Report { get; }
    public ObservableCollection<DuplicateModEntry> DuplicateMods { get; } = [];
    public ObservableCollection<DuplicateHashEntry> DuplicateHashes { get; } = [];
    public ObservableCollection<MissingModEntry> MissingMods { get; } = [];
    public ObservableCollection<CategoryStatEntry> CategoryStats { get; } = [];
    public bool HasCategories => CategoryStats.Count > 0;
    public bool HasMissing => MissingMods.Count > 0;
    public string LoaderText { get; }
    public string SideText { get; }

    public string Header => $"{Report.PackName} · {Report.Profile}";
    public string Summary => Report.Summary;
    public string JarCountText => Report.JarCount.ToString();
    public string ModCountText => Report.ModCount.ToString();

    public string ProblemText => $"缺失 {Report.MissingCount} · 冲突 {Report.ConflictCount} · 版本不符 {Report.MismatchCount}";
    public string DisabledText => $"禁用 {Report.DisabledCount} 个";
    public string DuplicateText => $"重复 modId {Report.DuplicateModIds.Count} · 相同文件 {Report.DuplicateJarCount}";
    public bool HasDuplicateMods => DuplicateMods.Count > 0;
    public bool HasDuplicateHashes => DuplicateHashes.Count > 0;
    public bool CanCleanDuplicates => Report.DuplicateJarCount > 0;
    public bool CanCleanDuplicateMods => Report.DuplicateModIds.Count > 0;

    public event Action<PackToolsResult?>? Completed;

    [RelayCommand]
    private void ExportModList() => Complete(PackToolsAction.ExportModList);

    [RelayCommand]
    private void ExportCsv() => Complete(PackToolsAction.ExportCsv);

    [RelayCommand]
    private void ExportMissing() => Complete(PackToolsAction.ExportMissing);

    [RelayCommand]
    private void ExportAudit() => Complete(PackToolsAction.ExportAudit);

    [RelayCommand]
    private void OpenMods() => Complete(PackToolsAction.OpenMods);

    [RelayCommand]
    private void OpenConfig() => Complete(PackToolsAction.OpenConfig);

    [RelayCommand]
    private void OpenResourcePacks() => Complete(PackToolsAction.OpenResourcePacks);

    [RelayCommand]
    private void CleanDuplicateFiles()
    {
        var extras = Report.DuplicateHashes
            .SelectMany(group => group.Files
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Select(file => file.AbsolutePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Complete(PackToolsAction.CleanDuplicateFiles, extras);
    }

    [RelayCommand]
    private void CleanDuplicateModFiles()
    {
        var extras = Report.DuplicateModIds
            .SelectMany(group => group.Files
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Select(file => file.AbsolutePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Complete(PackToolsAction.CleanDuplicateModFiles, extras);
    }

    [RelayCommand]
    private void Close() => Completed?.Invoke(null);

    private void Complete(PackToolsAction action, IReadOnlyList<string>? extras = null) =>
        Completed?.Invoke(new PackToolsResult { Action = action, ExtraFiles = extras ?? [] });
}
