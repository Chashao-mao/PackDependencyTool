using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.App.Services;
using PackDepTool.Core.Models;

namespace PackDepTool.App.ViewModels;

public sealed partial class NodeItemViewModel : ObservableObject
{
    public NodeItemViewModel(ModNode model, GraphDocument? graph = null, int index = 0, int issueCount = 0)
    {
        Model = model;
        StatusBrush = GraphPalette.StatusBrush(model.Status);
        Icon = ModIconCache.Get(model);
        Initial = BuildInitial(model);
        AvatarBrush = new SolidColorBrush(GraphPalette.LoaderColor(model.Loader));
        SideText = GraphPalette.SideText(model.Side) + (model.SideInferred ? "*" : string.Empty);
        SideBrush = new SolidColorBrush(model.Side switch
        {
            ModSide.Both => GraphPalette.Success,
            ModSide.Client => GraphPalette.EdgeRequires,
            ModSide.Server => GraphPalette.Warning,
            _ => GraphPalette.TextMuted,
        });
        IndexText = index.ToString();
        Version = model.DisplayVersion;
        StatusText = model.IsSynthetic && model.Status == ModStatus.Unknown
            ? "可选未安装"
            : GraphPalette.StatusText(model.Status);
        StatusTextBrush = model.Status is ModStatus.Ok or ModStatus.Unknown
            ? new SolidColorBrush(GraphPalette.TextSecondary)
            : StatusBrush;

        var relatedEdges = graph?.Edges.Where(e =>
            string.Equals(e.From, model.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.To, model.Id, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        IsInstalledOptionalDependency = !model.IsSynthetic &&
            relatedEdges.Any(e => e.Type == EdgeType.Optional);

        var outgoing = graph?.Edges.Where(e => string.Equals(e.From, model.Id, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        var requires = outgoing.Count(e => e.Type == EdgeType.Requires);
        var optional = outgoing.Count(e => e.Type == EdgeType.Optional);
        var incompatible = outgoing.Count(e => e.Type == EdgeType.Incompatible);
        var embedded = outgoing.Count(e => e.Type == EdgeType.Embedded);
        var recommends = outgoing.Count(e => e.Type == EdgeType.Recommends);

        var parts = new List<string>();
        if (requires > 0) parts.Add($"必需 {requires}");
        if (incompatible > 0) parts.Add($"冲突 {incompatible}");
        if (optional > 0) parts.Add($"可选 {optional}");
        if (embedded > 0) parts.Add($"内嵌 {embedded}");
        if (recommends > 0) parts.Add($"推荐 {recommends}");
        DependencySummary = parts.Count == 0 ? "无依赖" : string.Join(" · ", parts);

        var rail = new List<RailSegment>();
        if (incompatible > 0) rail.Add(new RailSegment(new SolidColorBrush(GraphPalette.EdgeIncompatible)));
        if (requires > 0) rail.Add(new RailSegment(new SolidColorBrush(GraphPalette.EdgeRequires)));
        if (optional > 0) rail.Add(new RailSegment(new SolidColorBrush(GraphPalette.EdgeOptional)));
        if (embedded > 0) rail.Add(new RailSegment(new SolidColorBrush(GraphPalette.EdgeEmbedded)));
        if (recommends > 0) rail.Add(new RailSegment(new SolidColorBrush(GraphPalette.EdgeRecommends)));
        DependencyRail = rail.Count > 0
            ? rail
            : [new RailSegment(new SolidColorBrush(GraphPalette.TextMuted))];

        IssueText = model.Status switch
        {
            ModStatus.Missing => "未安装",
            ModStatus.VersionMismatch => "版本不符",
            ModStatus.Conflict => "存在冲突",
            ModStatus.Unknown => model.IsSynthetic ? "未安装（可选）" : "未知来源",
            _ => string.Empty,
        };

        IssueCount = issueCount;
        IssueBadgeText = issueCount > 0 ? $"{issueCount} 项问题" : string.Empty;
        HasIssueBadge = issueCount > 0;
    }

    public ModNode Model { get; }
    public string Id => Model.Id;
    public string? NameZh => Model.NameZh;
    public string Name => !string.IsNullOrWhiteSpace(Model.NameZh)
        ? Model.NameZh!
        : (string.IsNullOrWhiteSpace(Model.Name) ? Model.Id : Model.Name);
    public string Subtitle => (!string.IsNullOrWhiteSpace(Model.NameZh)
        ? $"{Model.Name} · {Model.DisplayVersion}"
        : $"{Model.Id} · {Model.DisplayVersion}") + $" · {CategoryText}";
    public string CategoryText => ModCategoryText.Name(Model.Category);
    public string LoaderText => GraphPalette.LoaderText(Model.Loader);
    public string SideText { get; }
    public string SideTooltip => Model.SideInferred
        ? "模组未声明侧，按依赖关系或双端默认推断"
        : "模组元数据声明的运行侧";
    public IBrush SideBrush { get; }
    public string Version { get; }
    public string IndexText { get; }
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;
    public string Initial { get; }
    public IBrush AvatarBrush { get; }
    public string StatusText { get; }
    public IBrush StatusTextBrush { get; }
    public IBrush StatusBrush { get; }
    public string DependencySummary { get; }
    public IReadOnlyList<RailSegment> DependencyRail { get; }
    public IBrush RailPrimaryBrush => DependencyRail[0].Brush;
    public string IssueText { get; }
    public bool IsInstalledOptionalDependency { get; }
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isEmbeddedExpanded;

    public ObservableCollection<NodeItemViewModel> EmbeddedChildren { get; } = [];
    public bool HasEmbeddedChildren => EmbeddedChildren.Count > 0;
    public int EmbeddedCount => EmbeddedChildren.Count;
    public string EmbeddedToggleText => IsEmbeddedExpanded
        ? $"▾ 内嵌 {EmbeddedCount}"
        : $"▸ 内嵌 {EmbeddedCount}";

    public void SetEmbeddedChildren(IEnumerable<NodeItemViewModel> children, bool expanded)
    {
        EmbeddedChildren.Clear();
        foreach (var child in children)
            EmbeddedChildren.Add(child);
        IsEmbeddedExpanded = HasEmbeddedChildren && expanded;
        OnPropertyChanged(nameof(HasEmbeddedChildren));
        OnPropertyChanged(nameof(EmbeddedCount));
        OnPropertyChanged(nameof(EmbeddedToggleText));
    }

    [RelayCommand]
    private void ToggleEmbedded()
    {
        if (!HasEmbeddedChildren) return;
        IsEmbeddedExpanded = !IsEmbeddedExpanded;
    }

    partial void OnIsEmbeddedExpandedChanged(bool value) =>
        OnPropertyChanged(nameof(EmbeddedToggleText));

    public int IssueCount { get; }
    public string IssueBadgeText { get; }
    public bool HasIssueBadge { get; }
    public bool IsProblem => Model.Status is ModStatus.Missing or ModStatus.Conflict or ModStatus.VersionMismatch;
    public string Tooltip =>
        $"{Name}\n{Model.Id} · {Model.DisplayVersion}\n状态：{StatusText}\n依赖：{DependencySummary}" +
        (string.IsNullOrWhiteSpace(Model.NameZh) ? string.Empty : $"\n英文名：{Model.Name}") +
        (string.IsNullOrWhiteSpace(Model.StatusReason) ? string.Empty : $"\n{Model.StatusReason}");

    private static string BuildInitial(ModNode model)
    {
        var source = !string.IsNullOrWhiteSpace(model.NameZh)
            ? model.NameZh!
            : (string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name);
        var letter = source.TrimStart().FirstOrDefault();
        return letter == default ? "?" : char.ToUpperInvariant(letter).ToString();
    }
}

/// <summary>One colored segment of the RimCrow-style dependency rail in the mod list.</summary>
public sealed record RailSegment(IBrush Brush);

/// <summary>One dependency row shown in the details panel.</summary>
public sealed class DependencyRowViewModel
{
    public DependencyRowViewModel(
        string direction,
        DependencyEdge edge,
        ModNode? other,
        string resultText,
        IBrush resultBrush,
        bool canLocate = false)
    {
        DirectionText = direction;
        var optionalState = edge.Type == EdgeType.Optional
            ? other is null || other.IsSynthetic ? " · 未安装" : " · 已安装"
            : string.Empty;
        TypeText = GraphPalette.EdgeText(edge.Type) + optionalState +
                   (edge.ManualOverride ? " · 手动" : string.Empty);
        TypeBrush = new SolidColorBrush(GraphPalette.EdgeColor(edge.Type, edge.ManualOverride));
        TargetId = other?.Id ?? edge.To;
        TargetName = other is null ? TargetId : (string.IsNullOrWhiteSpace(other.Name) ? other.Id : other.Name);
        RangeText = string.IsNullOrWhiteSpace(edge.VersionRange) ? "任意版本" : edge.VersionRange;
        Note = edge.Note;
        ResultText = resultText;
        ResultBrush = resultBrush;
        IsManual = edge.ManualOverride;
        CanLocate = canLocate;
        (GroupTitle, GroupBrush, GroupOrder) = edge.Type switch
        {
            EdgeType.Incompatible => ("冲突于", new SolidColorBrush(GraphPalette.Danger), 0),
            EdgeType.Requires when direction == "→" => ("依赖于", new SolidColorBrush(GraphPalette.EdgeRequires), 1),
            EdgeType.Requires => ("被依赖", new SolidColorBrush(GraphPalette.EdgeRequires), 2),
            EdgeType.Optional or EdgeType.Recommends => ("可选 / 推荐", new SolidColorBrush(GraphPalette.EdgeOptional), 4),
            EdgeType.Embedded => ("内嵌", new SolidColorBrush(GraphPalette.EdgeEmbedded), 3),
            EdgeType.Manual => ("手动关系", new SolidColorBrush(GraphPalette.EdgeManual), 5),
            _ => ("其他关系", new SolidColorBrush(GraphPalette.TextMuted), 6),
        };
    }

    public string DirectionText { get; }
    public string TypeText { get; }
    public IBrush TypeBrush { get; }
    public string TargetId { get; }
    public string TargetName { get; }
    public string RangeText { get; }
    public string ResultText { get; }
    public IBrush ResultBrush { get; }
    public string? Note { get; }
    public bool IsManual { get; }
    public bool CanLocate { get; }
    public string GroupTitle { get; }
    public IBrush GroupBrush { get; }
    public int GroupOrder { get; }
}

public sealed partial class DependencyGroupViewModel : ObservableObject
{
    private const int CollapsedLimit = 5;
    private readonly IReadOnlyList<DependencyRowViewModel> _allRows;

    public DependencyGroupViewModel(string title, IBrush brush, IReadOnlyList<DependencyRowViewModel> rows)
    {
        Title = title;
        Brush = brush;
        _allRows = rows;
        Rows = new ObservableCollection<DependencyRowViewModel>(rows.Take(CollapsedLimit));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(ToggleText));
    }

    public string Title { get; }
    public IBrush Brush { get; }
    public ObservableCollection<DependencyRowViewModel> Rows { get; }
    public bool HasMore => _allRows.Count > CollapsedLimit;
    public string ToggleText => IsExpanded ? "收起" : $"展开全部 ({_allRows.Count})";
    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        Rows.Clear();
        foreach (var row in value ? _allRows : _allRows.Take(CollapsedLimit))
            Rows.Add(row);
        OnPropertyChanged(nameof(ToggleText));
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}


public sealed class DetailRowViewModel
{
    public DetailRowViewModel(string label, string? value)
    {
        Label = label;
        Value = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    public string Label { get; }
    public string Value { get; }
}

public sealed class IssueItemViewModel
{
    public IssueItemViewModel(AnalysisIssue issue)
    {
        Model = issue;
        SeverityBrush = new SolidColorBrush(issue.Severity switch
        {
            IssueSeverity.Error => GraphPalette.Danger,
            IssueSeverity.Warning => GraphPalette.Warning,
            _ => GraphPalette.TextMuted,
        });
        SeverityText = issue.Severity switch
        {
            IssueSeverity.Error => "错误",
            IssueSeverity.Warning => "警告",
            _ => "提示",
        };
        Location = BuildLocation(issue);
    }

    public AnalysisIssue Model { get; }
    public string Title => Model.Title;
    public string Message => Model.Message;
    public string SeverityText { get; }
    public IBrush SeverityBrush { get; }
    public string? NodeId => Model.NodeId;
    public string Location { get; }

    private static string BuildLocation(AnalysisIssue issue)
    {
        if (!string.IsNullOrWhiteSpace(issue.NodeId) && !string.IsNullOrWhiteSpace(issue.RelatedNodeId))
            return $"{issue.RelatedNodeId} → {issue.NodeId}";
        if (!string.IsNullOrWhiteSpace(issue.NodeId)) return issue.NodeId!;
        if (!string.IsNullOrWhiteSpace(issue.RelatedNodeId)) return issue.RelatedNodeId!;
        return string.Empty;
    }
}

public sealed class ManualLinkItemViewModel
{
    public ManualLinkItemViewModel(ManualLink link)
    {
        Model = link;
        TypeText = GraphPalette.EdgeText(link.Type);
        SideText = GraphPalette.SideText(link.Side);
    }

    public ManualLink Model { get; }
    public string PairText => $"{Model.From}  →  {Model.To}";
    public string TypeText { get; }
    public string SideText { get; }
    public string Detail =>
        $"{TypeText} · {SideText}" +
        (string.IsNullOrWhiteSpace(Model.VersionRange) ? string.Empty : $" · {Model.VersionRange}") +
        (string.IsNullOrWhiteSpace(Model.Note) ? string.Empty : $" · {Model.Note}");
}

public enum ConnectionLineMode
{
    Required,
    RequiredOptional,
    AllRelations,
}

public sealed record ConnectionLineOption(ConnectionLineMode Value, string Label);

public sealed record EdgeTypeOption(EdgeType Value, string Label);

public sealed record SideOption(ModSide Value, string Label);

public sealed record LayoutOption(string Value, string Label)
{
    public static IReadOnlyList<LayoutOption> All { get; } =
    [
        new("auto", "自动选择"),
        new("force", "力导向"),
        new("layered", "分层布局"),
    ];
}

public enum ModStatusFilter
{
    All,
    Problems,
    Missing,
    Conflict,
    VersionMismatch,
    Ok,
}

public sealed record StatusFilterOption(ModStatusFilter Value, string Label)
{
    public static IReadOnlyList<StatusFilterOption> All { get; } =
    [
        new(ModStatusFilter.All, "全部状态"),
        new(ModStatusFilter.Problems, "仅问题"),
        new(ModStatusFilter.Missing, "仅缺失"),
        new(ModStatusFilter.Conflict, "仅冲突"),
        new(ModStatusFilter.VersionMismatch, "仅版本不符"),
        new(ModStatusFilter.Ok, "仅正常"),
    ];
}

public enum ModSideFilter
{
    All,
    Both,
    Client,
    Server,
    Unknown,
}

public sealed record SideFilterOption(ModSideFilter Value, string Label)
{
    public static IReadOnlyList<SideFilterOption> All { get; } =
    [
        new(ModSideFilter.All, "全部侧"),
        new(ModSideFilter.Both, "双端"),
        new(ModSideFilter.Client, "客户端"),
        new(ModSideFilter.Server, "服务端"),
        new(ModSideFilter.Unknown, "未知侧"),
    ];
}

public enum ModSortMode
{
    ProblemFirst,
    Name,
    Version,
    Dependencies,
}

public sealed record ModSortOption(ModSortMode Value, string Label)
{
    public static IReadOnlyList<ModSortOption> All { get; } =
    [
        new(ModSortMode.ProblemFirst, "问题优先"),
        new(ModSortMode.Name, "按名称"),
        new(ModSortMode.Version, "按版本"),
        new(ModSortMode.Dependencies, "按依赖数"),
    ];
}

public sealed record CategoryFilterOption(ModCategory? Value, string Label)
{
    public static IReadOnlyList<CategoryFilterOption> All { get; } =
        new[] { new CategoryFilterOption(null, "全部分类") }
            .Concat(ModCategoryText.All.Select(category =>
                new CategoryFilterOption(category, ModCategoryText.Name(category))))
            .ToList();
}

public sealed record ViewOption(string Value, string Label)
{
    public static IReadOnlyList<ViewOption> All { get; } =
    [
        new("list", "模组清单"),
        new("graph", "依赖图"),
    ];
}

public sealed record LocalRuleTypeOption(LocalRuleType Value, string Label, string Hint);

public sealed partial class LocalRuleItemViewModel : ObservableObject
{
    private readonly Action<LocalRuleItemViewModel>? _onEnabledChanged;

    public LocalRuleItemViewModel(LocalRule rule, Action<LocalRuleItemViewModel>? onEnabledChanged = null)
    {
        Model = rule;
        _onEnabledChanged = onEnabledChanged;
        _isEnabled = rule.Enabled;
        TypeText = LocalRuleText.TypeName(rule.Type);
        Expression = LocalRuleText.Describe(rule);
        PriorityText = rule.Priority.ToString();
        Note = rule.Note ?? string.Empty;
        ValueText = rule.Value ?? string.Empty;
    }

    public LocalRule Model { get; }
    public string TypeText { get; }
    public string Expression { get; }
    public string PriorityText { get; }
    public string Note { get; }
    public string ValueText { get; }

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        Model.Enabled = value;
        _onEnabledChanged?.Invoke(this);
    }
}

public sealed record ThemeOption(string Value, string Label)
{
    public static IReadOnlyList<ThemeOption> All { get; } =
    [
        new("Dark", "深色"),
        new("Light", "浅色"),
    ];
}

public sealed partial class CategoryChipItem : ObservableObject
{
    public CategoryChipItem(ModCategory? value, string label, int count, Action<ModCategory?> select)
    {
        Value = value;
        Label = label;
        Count = count;
        SelectCommand = new RelayCommand(() => select(Value));
    }

    public ModCategory? Value { get; }
    public string Label { get; }
    public int Count { get; }
    public string Display => $"{Label} {Count}";
    public IRelayCommand SelectCommand { get; }

    [ObservableProperty] private bool _isActive;
}

public sealed class HistoryEntryItem
{
    public HistoryEntryItem(ModHistoryEntry model) => Model = model;

    public ModHistoryEntry Model { get; }
    public string TimeText => Model.Timestamp.ToLocalTime().ToString("MM-dd HH:mm");
    public string Summary => Model.Summary;
    public string CountText => $"{Model.TotalMods} 模组 · 禁用 {Model.DisabledMods}";
    public string Reason => Model.Reason;
}

public sealed class HistoryChangeItem
{
    public HistoryChangeItem(ModHistoryChange change)
    {
        KindText = change.Kind switch
        {
            ModHistoryChangeKind.Added => "新增",
            ModHistoryChangeKind.Removed => "移除",
            ModHistoryChangeKind.VersionChanged => "版本",
            ModHistoryChangeKind.Disabled => "禁用",
            ModHistoryChangeKind.Enabled => "启用",
            _ => "变化",
        };
        Brush = new SolidColorBrush(change.Kind switch
        {
            ModHistoryChangeKind.Added => GraphPalette.Success,
            ModHistoryChangeKind.Removed => GraphPalette.Danger,
            ModHistoryChangeKind.VersionChanged => GraphPalette.Warning,
            ModHistoryChangeKind.Disabled => GraphPalette.TextMuted,
            ModHistoryChangeKind.Enabled => GraphPalette.Success,
            _ => GraphPalette.TextSecondary,
        });
        Text = change.Detail;
    }

    public string KindText { get; }
    public string Text { get; }
    public IBrush Brush { get; }
}

public sealed class DisabledModItem
{
    public DisabledModItem(DisabledModFile model) => Model = model;

    public DisabledModFile Model { get; }
    public string DisplayName => Model.DisplayName;
    public string FileName => Model.FileName;
    public string SizeText => Model.SizeText;
    public string ModifiedText => Model.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
