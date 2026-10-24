using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PackDepTool.App.ViewModels;

public sealed class EnrichmentPlan
{
    public int InstalledCount { get; init; }
    public int MissingCount { get; init; }
    public bool HasCurseForgeAccess { get; init; }
    public bool UsesCurseForgeMirror { get; init; }
}

public sealed class EnrichmentPlanResult
{
    public bool UseModrinth { get; init; }
    public bool UseCurseForge { get; init; }
    public bool AddDependencyEdges { get; init; }
    public bool HasAnySelection => UseModrinth || UseCurseForge || AddDependencyEdges;
}

public sealed partial class EnrichmentPlanViewModel : ObservableObject
{
    public EnrichmentPlanViewModel(EnrichmentPlan plan)
    {
        InstalledCount = plan.InstalledCount;
        MissingCount = plan.MissingCount;
        HasCurseForgeAccess = plan.HasCurseForgeAccess;
        UsesCurseForgeMirror = plan.UsesCurseForgeMirror;
        UseModrinth = plan.InstalledCount > 0;
        UseCurseForge = plan.HasCurseForgeAccess && plan.InstalledCount > 0;
        AddDependencyEdges = true;
    }

    public int InstalledCount { get; }
    public int MissingCount { get; }
    public bool HasCurseForgeAccess { get; }
    public bool UsesCurseForgeMirror { get; }
    public string SummaryText =>
        $"已安装 {InstalledCount} 个模组，当前缺失 {MissingCount} 个依赖。";
    public string CurseForgeHint => !HasCurseForgeAccess
        ? "未配置 API Key 或 CurseForge 镜像，本次不能使用 CurseForge。"
        : UsesCurseForgeMirror
            ? "使用自定义 CurseForge 镜像；不会发送本地 API Key。"
            : "使用当前已配置的 API Key。";

    [ObservableProperty] private bool _useModrinth;
    [ObservableProperty] private bool _useCurseForge;
    [ObservableProperty] private bool _addDependencyEdges;

    public bool CanStart => UseModrinth || UseCurseForge || AddDependencyEdges;

    partial void OnUseModrinthChanged(bool value) => OnPropertyChanged(nameof(CanStart));
    partial void OnUseCurseForgeChanged(bool value) => OnPropertyChanged(nameof(CanStart));
    partial void OnAddDependencyEdgesChanged(bool value) => OnPropertyChanged(nameof(CanStart));

    public event Action<EnrichmentPlanResult?>? Completed;

    [RelayCommand]
    private void Start()
    {
        if (!CanStart) return;
        Completed?.Invoke(new EnrichmentPlanResult
        {
            UseModrinth = UseModrinth,
            UseCurseForge = UseCurseForge,
            AddDependencyEdges = AddDependencyEdges,
        });
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(null);
}
