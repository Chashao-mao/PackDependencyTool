using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.App.Controls;
using PackDepTool.App.Services;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Export;
using PackDepTool.Core.Models;
using PackDepTool.Core.Net;
using PackDepTool.Core.Persistence;
using PackDepTool.App.Views;

namespace PackDepTool.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly IDialogService _dialogs;

    private Window? _window;
    private DependencyGraphView? _graphView;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _iconFetchCts;
    private readonly HashSet<string> _iconFetchAttempted = new(StringComparer.Ordinal);
    private ScanResult? _scanResult;
    private GraphDocument? _graph;
    private bool _iconsPrimed;
    private readonly SemaphoreSlim _graphRebuildGate = new(1, 1);
    private int _graphRebuildVersion;
    private List<AnalysisIssue> _issues = [];
    private List<ManualLink> _manualLinks = [];
    private List<LocalRule> _localRules = [];
    private Dictionary<string, string> _translationOverrides = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, LayoutPoint> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModUpdateState> _updateStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedModUpdate>
        _updateCheckCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan UpdateCheckTtl = TimeSpan.FromHours(6);

    private sealed record ModUpdateState(bool Available, string? LatestVersion, string? Error);
    private sealed record CachedModUpdate(ModUpdateState State, DateTimeOffset CheckedAt);

    private string? _selectedNodeId;
    private string? _selectedEdgeId;
    private string? _pendingReplaceNodeId;
    private HashSet<string> _pendingReplaceProjectIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingFocusNodeId;
    private bool _suppressSelectionSync;
    private bool _suppressRecent;
    private bool _suppressCheckedSync;
    private bool _suppressCategoryFilterChange;
    private readonly HashSet<string> _checkedNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedEmbeddedParents = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _relationFocusIds = new(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<ConnectionLineOption> ConnectionLineOptions { get; } =
    [
        new(ConnectionLineMode.Required, "必需 + 冲突"),
        new(ConnectionLineMode.RequiredOptional, "必需 + 可选 + 冲突"),
        new(ConnectionLineMode.AllRelations, "全部关系"),
    ];
    private string? _relationFocusId;
    private CancellationTokenSource? _filterLayoutCts;

    public MainWindowViewModel(AppServices services, IDialogService dialogs)
    {
        _services = services;
        _dialogs = dialogs;
        ShowRequires = true;
        ShowIncompatible = true;
        ShowManual = services.Settings.ShowManual;
        ShowOptional = services.Settings.ShowOptional;
        ShowEmbedded = services.Settings.ShowEmbedded;
        ShowRecommends = services.Settings.ShowRecommends;
        HideIsolated = services.Settings.HideIsolated;
        ShowLabels = services.Settings.ShowLabels;
        IsOffline = !services.Settings.NetworkEnabled;
    }

    // ------------------------------------------------------------------ state

    [ObservableProperty] private string _packPath = string.Empty;
    [ObservableProperty] private string _packPathDisplay = "尚未选择整合包";
    [ObservableProperty] private string _packName = "未选择整合包";
    [ObservableProperty] private string _profileSummary = "等待扫描";
    [ObservableProperty] private string _countSummary = "0 个模组 · 0 条关系";
    [ObservableProperty] private string _statusText = "就绪 · 选择一个整合包目录开始分析";
    [ObservableProperty] private string _updateStatusText = string.Empty;
    [ObservableProperty] private bool _isCheckingUpdates;
    private string? _lastUpdateUrl;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private bool _hasGraph;
    [ObservableProperty] private bool _hasOptionalNodeItems;
    [ObservableProperty] private bool _hasMissingNodeItems;
    [ObservableProperty] private bool _isMissingSectionExpanded;
    [ObservableProperty] private bool _showUninstalledOptional;
    [ObservableProperty] private bool _showMissing = true;
    [ObservableProperty] private bool _showOptionalInReport;
    [ObservableProperty] private ConnectionLineOption _selectedConnectionLineMode = ConnectionLineOptions[0];
    [ObservableProperty] private bool _isRelationFocus;
    [ObservableProperty] private string _relationFocusTitle = string.Empty;
    [ObservableProperty] private GraphScene _scene = new();
    [ObservableProperty] private bool _isOffline;

    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty] private bool _showRequires = true;
    [ObservableProperty] private bool _showIncompatible = true;
    [ObservableProperty] private bool _showOptional;
    [ObservableProperty] private bool _showEmbedded;
    [ObservableProperty] private bool _showRecommends;
    [ObservableProperty] private bool _showManual = true;

    [ObservableProperty] private bool _showFabric = true;
    [ObservableProperty] private bool _showQuilt = true;
    [ObservableProperty] private bool _showForge = true;
    [ObservableProperty] private bool _showNeoForge = true;
    [ObservableProperty] private bool _showUnknown = true;

    [ObservableProperty] private bool _onlyProblems;
    [ObservableProperty] private bool _onlyUpdatable;
    [ObservableProperty] private bool _hasUpdateCheck;
    [ObservableProperty] private bool _sortDescending;
    [ObservableProperty] private bool _hideIsolated;
    [ObservableProperty] private bool _showLabels = true;
    [ObservableProperty] private bool _focusEdgesOnly = true;
    [ObservableProperty] private bool _allVisibleChecked;
    [ObservableProperty] private int _checkedCount;
    [ObservableProperty] private bool _hasChecked;
    [ObservableProperty] private StatusFilterOption _selectedStatusFilter = StatusFilterOption.All[0];
    [ObservableProperty] private SideFilterOption _selectedSideFilter = SideFilterOption.All[0];
    [ObservableProperty] private ModSortOption _selectedSortOption = ModSortOption.All[0];
    [ObservableProperty] private CategoryFilterOption _selectedCategoryFilter = CategoryFilterOption.All[0];

    [ObservableProperty] private int _missingCount;
    [ObservableProperty] private int _mismatchCount;
    [ObservableProperty] private int _conflictCount;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _infoCount;

    [ObservableProperty] private string _detailTitle = "未选择";
    [ObservableProperty] private string _detailSubtitle = "在图中点选节点或关系查看详情";
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _isNodeSelected;
    [ObservableProperty] private bool _isEdgeSelected;
    [ObservableProperty] private bool _isMultiSelection;
    [ObservableProperty] private bool _canAddRelation;
    [ObservableProperty] private bool _canEditManual;
    [ObservableProperty] private bool _canDeleteManual;
    [ObservableProperty] private bool _canOpenProject;
    [ObservableProperty] private bool _canOpenFile;
    [ObservableProperty] private bool _canOpenCurseForge;
    [ObservableProperty] private bool _canOpenModrinth;
    [ObservableProperty] private bool _canOpenMcmod;
    [ObservableProperty] private Bitmap? _detailIcon;
    [ObservableProperty] private bool _hasDetailIcon;
    [ObservableProperty] private string _detailInitial = "?";
    [ObservableProperty] private IBrush _detailAvatarBrush = Brushes.Gray;
    [ObservableProperty] private string _detailDescription = string.Empty;
    [ObservableProperty] private bool _hasDetailDescription;

    [ObservableProperty] private NodeItemViewModel? _selectedNodeItem;
    [ObservableProperty] private IssueItemViewModel? _selectedIssue;
    [ObservableProperty] private ManualLinkItemViewModel? _selectedManualLink;
    [ObservableProperty] private LocalRuleItemViewModel? _selectedLocalRule;
    [ObservableProperty] private int _enabledRuleCount;
    [ObservableProperty] private string _rulesSummary = "本地规则 0 条";
    [ObservableProperty] private string? _selectedRecentPack;
    [ObservableProperty] private ViewOption _selectedViewOption = ViewOption.All[0];
    [ObservableProperty] private bool _hasDependencyRows;
    [ObservableProperty] private bool _hasMissingDependencies;
    [ObservableProperty] private bool _isBasicInfoExpanded = true;
    [ObservableProperty] private bool _isDescriptionExpanded = true;
    [ObservableProperty] private bool _isDependenciesExpanded = true;
    [ObservableProperty] private bool _canDisableSelected;
    [ObservableProperty] private bool _canDeleteSelected;
    [ObservableProperty] private bool _canDownloadOptional;
    [ObservableProperty] private string _downloadSelectedLabel = "下载此模组";
    [ObservableProperty] private bool _hasHistory;
    [ObservableProperty] private HistoryEntryItem? _selectedHistoryEntry;

    public ObservableCollection<NodeItemViewModel> NodeItems { get; } = [];
    public ObservableCollection<NodeItemViewModel> OptionalNodeItems { get; } = [];
    public ObservableCollection<NodeItemViewModel> MissingNodeItems { get; } = [];
    public ObservableCollection<IssueItemViewModel> Issues { get; } = [];
    public ObservableCollection<IssueItemViewModel> ReportIssues { get; } = [];
    public ObservableCollection<ManualLinkItemViewModel> ManualLinks { get; } = [];
    public ObservableCollection<LocalRuleItemViewModel> LocalRules { get; } = [];
    public ObservableCollection<DetailRowViewModel> DetailRows { get; } = [];
    public ObservableCollection<DependencyRowViewModel> SelectedDependencies { get; } = [];
    public ObservableCollection<DependencyGroupViewModel> SelectedDependencyGroups { get; } = [];
    public ObservableCollection<string> AvailableModIds { get; } = [];
    public ObservableCollection<string> RecentPacks { get; } = [];
    public ObservableCollection<HistoryEntryItem> HistoryEntries { get; } = [];
    public ObservableCollection<HistoryChangeItem> HistoryChanges { get; } = [];
    public ObservableCollection<CategoryChipItem> CategoryChips { get; } = [];
    public IReadOnlyList<StatusFilterOption> StatusFilters => StatusFilterOption.All;
    public IReadOnlyList<SideFilterOption> SideFilters => SideFilterOption.All;
    public IReadOnlyList<ModSortOption> SortOptions => ModSortOption.All;
    public IReadOnlyList<CategoryFilterOption> CategoryFilters => CategoryFilterOption.All;
    public IReadOnlyList<ViewOption> ViewOptions => ViewOption.All;

    public bool IsListView => !string.Equals(SelectedViewOption?.Value, "graph", StringComparison.OrdinalIgnoreCase);
    public bool IsGraphView => string.Equals(SelectedViewOption?.Value, "graph", StringComparison.OrdinalIgnoreCase);

    public bool IsActiveConflict(DependencyEdge edge)
    {
        if (edge.Type != EdgeType.Incompatible || _graph is null) return true;
        var target = _graph.FindNode(edge.To);
        return target is not null && !target.IsSynthetic &&
               VersionRange.Satisfies(edge.VersionRange, target.Version) == true;
    }
    public string? RelationFocusId => _relationFocusId;

    public string DataDirectory => _services.Paths.DataDirectory;
    public bool IsPackLoaded => !string.IsNullOrWhiteSpace(PackPath);
    public string NetworkStatusText => IsOffline ? "离线模式" : "联网可用";
    public string VersionLabel => "v1.0.3";
    public string IssueSummary => $"错误 {ErrorCount} · 警告 {WarningCount} · 提示 {InfoCount}";
    public bool ShowOptionalSection => ShowUninstalledOptional && HasOptionalNodeItems;
    public bool ShowMissingSection => ShowMissing && HasMissingNodeItems;
    public string SortDirectionText => SortDescending ? "↓" : "↑";
    public string SortDirectionTooltip => SortDescending ? "当前倒序，点击切换正序" : "当前正序，点击切换倒序";
    public bool ShowLoaderFilters => _graph is { } graph &&
        graph.Nodes.Where(node => !node.IsSynthetic)
            .Select(node => node.Loader)
            .Where(loader => loader != LoaderKind.Unknown)
            .Distinct()
            .Count() > 1;

    public bool ShowFabricFilter => HasLoader(LoaderKind.Fabric);
    public bool ShowQuiltFilter => HasLoader(LoaderKind.Quilt);
    public bool ShowForgeFilter => HasLoader(LoaderKind.Forge);
    public bool ShowNeoForgeFilter => HasLoader(LoaderKind.NeoForge);
    public bool ShowUnknownFilter => HasLoader(LoaderKind.Unknown);

    private bool HasLoader(LoaderKind loader) =>
        _graph?.Nodes.Any(node => !node.IsSynthetic && node.Loader == loader) == true;
    public string BasicInfoToggleText => IsBasicInfoExpanded ? "收起" : "展开";
    public string DescriptionToggleText => IsDescriptionExpanded ? "收起" : "展开";
    public string DependenciesToggleText => IsDependenciesExpanded ? "收起" : "展开";
    public string MissingToggleText => IsMissingSectionExpanded ? "收起" : "展开";
    public string ProblemSummary =>
        $"缺失 {MissingCount} · 冲突 {ConflictCount} · 版本 {MismatchCount} · 错误 {ErrorCount} · 警告 {WarningCount} · 提示 {InfoCount}";

    // ------------------------------------------------------------------ lifecycle

    public async Task InitializeAsync(
        string? startupPath = null,
        bool openSettings = false,
        string? addLinkFrom = null,
        bool openDownloadPack = false,
        bool openDownloadMod = false,
        string? downloadQuery = null,
        string? selectNodeId = null,
        string? exportPclPath = null,
        bool openGameSetup = false,
        string? installGameSpec = null,
        bool openLoaderSwitch = false,
        bool openGraph = false,
        bool openToolbox = false)
    {
        ApplyTheme();
        _translationOverrides = await _services.TranslationOverrides.LoadAsync();
        RefreshRecentPacks();

        var target = startupPath;
        if (string.IsNullOrWhiteSpace(target))
        {
            var last = _services.Settings.LastPackPath;
            if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last))
                target = last;
        }

        if (!string.IsNullOrWhiteSpace(target))
            await OpenPackAsync(target, autoScan: true);

        if (openSettings)
            await OpenSettingsAsync();
        else if (!string.IsNullOrWhiteSpace(addLinkFrom))
            await AddManualLinkAsync(addLinkFrom);
        else if (openDownloadPack)
            await OpenDownloadCenterAsync(DownloadProjectType.Modpack, downloadQuery);
        else if (openDownloadMod)
            await OpenDownloadCenterAsync(DownloadProjectType.Mod, downloadQuery);

        if (!string.IsNullOrWhiteSpace(selectNodeId))
            SelectNode(selectNodeId, center: true);

        if (!string.IsNullOrWhiteSpace(exportPclPath))
            await ExportPclPackToAsync(exportPclPath);

        if (!string.IsNullOrWhiteSpace(installGameSpec))
            await InstallGameFromCliAsync(installGameSpec);

        if (openGameSetup)
            await OpenGameSetupAsync();

        if (openLoaderSwitch)
            await OpenLoaderSwitchAsync();

        if (openGraph)
            SelectedViewOption = ViewOptions.First(option => option.Value == "graph");

        if (openToolbox)
            await OpenPackToolsAsync();

        if (_services.Settings.CheckUpdatesOnStartup)
            _ = CheckForUpdatesAsync();
    }

    public void AttachWindow(Window window)
    {
        _window = window;
        _dialogs.Owner = window;
        ApplyTheme();

        var settings = _services.Settings;
        if (settings.WindowWidth >= 1000 && settings.WindowHeight >= 640)
        {
            window.Width = settings.WindowWidth;
            window.Height = settings.WindowHeight;
        }

        if (settings.WindowX >= 0 && settings.WindowY >= 0)
        {
            try
            {
                window.Position = new PixelPoint(settings.WindowX, settings.WindowY);
            }
            catch (Exception)
            {
                // Ignore off-screen saved positions.
            }
        }

        if (settings.Maximized)
            window.WindowState = WindowState.Maximized;
    }

    public void SaveWindowState(Window window)
    {
        var settings = _services.Settings;
        settings.Maximized = window.WindowState == WindowState.Maximized;

        if (window.WindowState == WindowState.Normal)
        {
            if (window.Width >= 1000) settings.WindowWidth = window.Width;
            if (window.Height >= 640) settings.WindowHeight = window.Height;
        }

        var position = window.Position;
        if (position.X > -32000 && position.Y > -32000)
        {
            settings.WindowX = position.X;
            settings.WindowY = position.Y;
        }

        settings.ShowOptional = ShowOptional;
        settings.ShowEmbedded = ShowEmbedded;
        settings.ShowRecommends = ShowRecommends;
        settings.ShowManual = ShowManual;
        settings.HideIsolated = HideIsolated;
        settings.ShowLabels = ShowLabels;

        try
        {
            _services.SaveSettings();
        }
        catch (Exception)
        {
            // Closing must not be blocked by a failed settings write.
        }
    }

    public void AttachGraph(DependencyGraphView view)
    {
        _graphView = view;
        view.SelectionChanged += (_, args) => HandleGraphSelection(args.NodeIds, args.EdgeId);
        view.NodeMoved += (_, args) => _positions[args.NodeId] = new LayoutPoint(args.X, args.Y);
        view.AddRelationRequested += nodeId => _ = AddManualLinkAsync(nodeId);
        view.EditManualRequested += edgeId =>
        {
            SelectEdge(edgeId, center: false, fromGraph: true);
            _ = EditManualLinkAsync();
        };
        view.DeleteManualRequested += edgeId => _ = DeleteManualByEdgeAsync(edgeId);
        view.OpenProjectRequested += nodeId => OpenProjectForNode(nodeId);
        view.OpenFileRequested += nodeId => OpenFileForNode(nodeId);
        view.CopyIdRequested += nodeId => _ = CopyIdAsync(nodeId);
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var path = await _dialogs.PickFolderAsync("选择整合包文件夹（包含 mods 目录）");
        if (string.IsNullOrWhiteSpace(path)) return;
        await OpenPackAsync(path, autoScan: true);
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (string.IsNullOrWhiteSpace(PackPath))
        {
            await PickFolderAsync();
            return;
        }

        await ScanAsync();
    }

    [RelayCommand]
    private async Task EnrichAsync()
    {
        if (IsBusy) return;
        if (_graph is null || _scanResult is null)
        {
            StatusText = "请先扫描整合包";
            return;
        }

        if (IsOffline)
        {
            StatusText = "离线模式：请先在设置中启用联网补全";
            return;
        }

        var curseForgeKey = await _services.Keys.GetCurseForgeKeyAsync();
        var curseForgeMirror = !string.IsNullOrWhiteSpace(_services.Settings.CurseForgeApiBaseUrl);
        var plan = new EnrichmentPlan
        {
            InstalledCount = _graph.Nodes.Count(node => !node.IsSynthetic),
            MissingCount = _graph.Nodes.Count(node => node.Status == ModStatus.Missing),
            HasCurseForgeAccess = !string.IsNullOrWhiteSpace(curseForgeKey) || curseForgeMirror,
            UsesCurseForgeMirror = curseForgeMirror,
        };
        var selection = await _dialogs.ShowEnrichmentPlanAsync(plan);
        if (selection is null || !selection.HasAnySelection)
        {
            StatusText = "已取消联网补全";
            return;
        }

        var cts = BeginOperation();
        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressText = "联网补全…";
        StatusText = "正在查询 Modrinth / CurseForge…";

        try
        {
            var progress = new Progress<EnrichmentProgress>(p =>
            {
                ProgressText = p.Total > 0 ? $"{p.Phase} {p.Done}/{p.Total}" : p.Phase;
                StatusText = string.IsNullOrWhiteSpace(p.Message) ? p.Phase : $"{p.Phase} · {p.Message}";
            });

            var options = new EnrichmentOptions
            {
                UseModrinth = selection.UseModrinth,
                UseCurseForge = selection.UseCurseForge,
                AddDependencyEdges = selection.AddDependencyEdges,
            };

            var stats = await _services.Enrichment.EnrichAsync(
                _graph, _scanResult.Profile, options, progress, cts.Token);
            // Projects/slugs may have been enriched, so refresh Chinese names first.
            ApplyTranslations();
            _services.Categories.Classify(_graph);
            RefreshCategoryChips();

            // Re-analyze after new online edges / projects were added.
            var newIssues = _services.Analyzer.Analyze(
                _graph, _scanResult.Profile, new LocalRuleSet(_localRules));
            MergeIssues(newIssues);
            // New online nodes/edges may not have a side yet.
            DependencyGraphBuilder.InferSides(_graph);
            foreach (var message in stats.Messages.Take(10))
            {
                _issues.Add(new AnalysisIssue
                {
                    Severity = IssueSeverity.Info,
                    Kind = IssueKind.Unknown,
                    Title = "联网补全",
                    Message = message,
                });
            }

            RefreshAnalysis();
            EnsurePositions();
            BuildScene();
            await LayoutAsync(fit: false, cts.Token);

            StatusText = $"联网补全完成：{stats.Summary()}";
            if (stats.CurseForgeSkipped) StatusText += "（CurseForge 未配置 Key）";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消联网补全";
        }
        catch (Exception ex)
        {
            StatusText = $"联网补全失败：{ex.Message}";
        }
        finally
        {
            EndOperation(cts);
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task RelayoutAsync()
    {
        if (IsBusy || _graph is null) return;

        var cts = BeginOperation();
        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressText = "重新布局…";
        StatusText = "正在计算布局…";

        try
        {
            await LayoutVisibleAsync(fit: true, cts.Token);
            StatusText = "布局已更新";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消布局";
        }
        catch (Exception ex)
        {
            StatusText = $"布局失败：{ex.Message}";
        }
        finally
        {
            EndOperation(cts);
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task ExportGraphPngAsync()
    {
        if (_graph is null || Scene.Nodes.Count == 0)
        {
            await _dialogs.ShowMessageAsync("导出关系图 PNG", "当前关系图没有可导出的节点。");
            return;
        }

        var path = await _dialogs.PickSaveFileAsync(
            "导出关系图 PNG", ExportFileName("relationship-graph", "png"), "png", "PNG 图片");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            StatusText = $"正在渲染关系图 PNG（{Scene.Nodes.Count} 个节点）…";
            var title = string.IsNullOrWhiteSpace(PackName) ? "Minecraft 模组关系图" : $"{PackName} · 模组关系图";
            var light = string.Equals(_services.Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
            await Task.Run(() => GraphPngExporter.Export(Scene, path, title, light, RelationFocusId));
            StatusText = $"关系图 PNG 已导出：{Path.GetFileName(path)}";
            await _dialogs.ShowMessageAsync("导出完成",
                $"关系图已写入：{Environment.NewLine}{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = $"关系图导出失败：{ex.Message}";
            await _dialogs.ShowMessageAsync("导出失败", ex.Message);
        }
    }

    [RelayCommand] private Task ExportJsonAsync() => ExportAsync(ExportFormat.Json);
    [RelayCommand] private Task ExportDotAsync() => ExportAsync(ExportFormat.Dot);
    [RelayCommand] private Task ExportGraphMlAsync() => ExportAsync(ExportFormat.GraphMl);
    [RelayCommand] private Task ExportCsvAsync() => ExportAsync(ExportFormat.Csv);
    [RelayCommand] private Task ExportReportAsync() => ExportAsync(ExportFormat.MarkdownReport);

    [RelayCommand]
    private async Task AddManualLinkAsync(string? fromId)
    {
        if (_graph is null)
        {
            StatusText = "请先扫描整合包";
            return;
        }

        var ids = _graph.Nodes.Select(n => n.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var from = string.IsNullOrWhiteSpace(fromId) ? _selectedNodeId : fromId;
        var link = await _dialogs.ShowManualLinkAsync(null, ids, from);
        if (link is null) return;
        await SaveManualLinkAsync(link);
    }

    [RelayCommand]
    private async Task EditManualLinkAsync()
    {
        if (_graph is null) return;

        var link = GetSelectedManualLink();
        if (link is null)
        {
            StatusText = "请选择一条手动关系";
            return;
        }

        var ids = _graph.Nodes.Select(n => n.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var edited = await _dialogs.ShowManualLinkAsync(link, ids, link.From);
        if (edited is null) return;

        edited.Id = link.Id;
        await SaveManualLinkAsync(edited);
    }

    [RelayCommand]
    private async Task DeleteManualLinkAsync()
    {
        var link = GetSelectedManualLink();
        if (link is null)
        {
            StatusText = "请选择一条手动关系";
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "删除手动关系",
            $"确定删除 {link.From} → {link.To} 吗？删除后不可恢复。",
            "删除");
        if (!confirmed) return;

        _manualLinks.RemoveAll(l => l.Id == link.Id);
        await PersistManualLinksAsync();
        RebuildGraph();
        ClearSelection(fromGraph: false);
        StatusText = "已删除手动关系";
    }

    private async Task DeleteManualByEdgeAsync(string edgeId)
    {
        SelectEdge(edgeId, center: false, fromGraph: true);
        await DeleteManualLinkAsync();
    }

    [RelayCommand]
    private async Task ImportManualLinksAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("导入手动关系", "json", "JSON 文件");
        if (string.IsNullOrWhiteSpace(path)) return;

        var result = await _services.ManualLinks.ImportAsync(path);
        if (result.Errors.Count > 0)
        {
            await _dialogs.ShowMessageAsync(
                "导入完成（含问题）",
                $"成功导入 {result.Links.Count} 条，{result.Errors.Count} 条被跳过：\n\n" +
                string.Join("\n", result.Errors.Take(12)));
        }

        if (result.Links.Count == 0) return;

        foreach (var imported in result.Links)
        {
            _manualLinks.RemoveAll(existing =>
                existing.Id == imported.Id ||
                (string.Equals(existing.From, imported.From, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(existing.To, imported.To, StringComparison.OrdinalIgnoreCase)));
            _manualLinks.Add(imported);
        }

        await PersistManualLinksAsync();
        RebuildGraph();
        StatusText = $"已导入 {result.Links.Count} 条手动关系";
    }

    [RelayCommand]
    private async Task ExportManualLinksAsync()
    {
        if (_scanResult is null)
        {
            StatusText = "请先扫描整合包";
            return;
        }

        var path = await _dialogs.PickSaveFileAsync(
            "导出手动关系", "manual-links.json", "json", "JSON 文件");
        if (string.IsNullOrWhiteSpace(path)) return;

        await _services.ManualLinks.ExportAsync(path, _scanResult.RootPath, _manualLinks);
        StatusText = $"已导出手动关系：{path}";
    }

    // ------------------------------------------------------------------ download / import

    [RelayCommand]
    private Task DownloadModsAsync() => OpenDownloadCenterAsync(DownloadProjectType.Mod);

    [RelayCommand]
    private Task DownloadModpackAsync() => OpenDownloadCenterAsync(DownloadProjectType.Modpack);

    [RelayCommand]
    private async Task DownloadOptionalDependencyAsync()
    {
        if (_selectedNodeId is null || _graph is null) return;
        var node = _graph.FindNode(_selectedNodeId);
        if (node is null) return;
        await DownloadOptionalNodeAsync(node);
    }

    /// <summary>Directly download a synthetic optional/recommended node (used by a list-row button).</summary>
    public async Task DownloadOptionalNodeAsync(ModNode node)
    {
        if (IsBusy || _scanResult is null) return;
        if (!node.IsSynthetic)
        {
            await OpenDownloadCenterAsync(DownloadProjectType.Mod, node.Id);
            return;
        }

        if (!await _dialogs.ConfirmAsync(
                "下载可选依赖",
                $"直接下载 {node.Name}（{node.Id}）？\n\n会只选择与 {_scanResult.Profile.McVersion} / {_scanResult.Profile.DisplayLoader} 兼容的版本。"))
            return;

        var cts = BeginOperation();
        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = "下载可选依赖…";
        StatusText = $"正在解析 {node.Name}…";

        var detail = new DownloadProgressViewModel("下载可选依赖", $"{node.Name} · {node.Id}");
        DownloadProgressWindow.ShowFor(detail);
        BatchModResult? result = null;
        try
        {
            var progress = new Progress<DownloadProgress>(report =>
            {
                detail.Update(report);
                ProgressValue = report.Fraction;
                ProgressText = report.Total > 0 ? $"{report.Phase} {report.Done}/{report.Total}" : report.Phase;
                if (!string.IsNullOrWhiteSpace(report.Message))
                    StatusText = $"{report.Phase} · {report.Message}";
            });

            result = await _services.Batch.DownloadAsync(
                new[] { node }, _scanResult.Profile, _scanResult.RootPath, progress, cts.Token,
                maxConcurrency: _services.Settings.MaxParallelDownloads, graph: _graph);
            detail.Finish(result.FailedCount == 0,
                $"成功 {result.SuccessCount} · 失败 {result.FailedCount}");
        }
        catch (OperationCanceledException)
        {
            detail.Finish(false, "已取消");
        }
        catch (Exception ex)
        {
            detail.Finish(false, ex.Message);
            StatusText = $"下载可选依赖失败：{ex.Message}";
        }
        finally
        {
            EndOperation(cts);
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }

        if (result is not null)
        {
            var downloadedPath = result.Items.FirstOrDefault(item => item.Success)?.FilePath;
            await ShowBatchResultAsync("下载可选依赖", result);
            await ScanAsync();
            if (!string.IsNullOrWhiteSpace(downloadedPath) &&
                _graph?.Nodes.Any(installed => !installed.IsSynthetic &&
                                               string.Equals(installed.Id, node.Id,
                                                   StringComparison.OrdinalIgnoreCase)) != true)
                StatusText = $"已下载到 {downloadedPath}，但扫描未识别该模组。";
        }
    }

    [RelayCommand]
    private async Task DownloadSelectedModAsync()
    {
        if (_selectedNodeId is null || _graph is null) return;
        var node = _graph.FindNode(_selectedNodeId);
        if (node is null) return;

        _pendingReplaceNodeId = HasLocalJar(node) ? node.Id : null;
        _pendingReplaceProjectIds = ProjectKeys(node);
        try
        {
            await OpenDownloadCenterAsync(DownloadProjectType.Mod, node.Id);
        }
        finally
        {
            _pendingReplaceNodeId = null;
            _pendingReplaceProjectIds.Clear();
        }
    }

    private static HashSet<string> ProjectKeys(ModNode node)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(node.ProjectIds.Modrinth))
            keys.Add($"Modrinth:{node.ProjectIds.Modrinth}");
        if (!string.IsNullOrWhiteSpace(node.ProjectIds.CurseForge))
            keys.Add($"CurseForge:{node.ProjectIds.CurseForge}");
        return keys;
    }

    private IReadOnlySet<string> BuildInstalledProjectIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_graph is null) return ids;
        foreach (var node in _graph.Nodes.Where(node => !node.IsSynthetic))
        {
            if (!string.IsNullOrWhiteSpace(node.ProjectIds.Modrinth))
                ids.Add(node.ProjectIds.Modrinth);
            if (!string.IsNullOrWhiteSpace(node.ProjectIds.CurseForge))
                ids.Add(node.ProjectIds.CurseForge);
            if (!string.IsNullOrWhiteSpace(node.Slug))
                ids.Add(node.Slug);
            ids.Add(node.Id);
        }
        return ids;
    }

    private async Task OpenDownloadCenterAsync(DownloadProjectType type, string? initialQuery = null)
    {
        if (type == DownloadProjectType.Mod && _scanResult is null)
        {
            StatusText = "请先打开或扫描一个整合包，再下载模组";
            await _dialogs.ShowMessageAsync("下载模组", "请先打开或扫描一个整合包，再下载模组。");
            return;
        }

        var context = new DownloadCenterContext
        {
            InitialType = type,
            PackRoot = _scanResult?.RootPath ?? PackPath,
            McVersion = _scanResult?.Profile.McVersion,
            Loader = _scanResult?.Profile.Loader ?? LoaderKind.Unknown,
            InitialQuery = initialQuery,
            DefaultDestination = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
                ? Path.Combine(home, "Downloads")
                : null,
            InstalledProjectIds = BuildInstalledProjectIds(),
        };

        var result = await _dialogs.ShowDownloadCenterAsync(context);
        if (result is null) return;

        if (result.ImportResult is not null)
        {
            var import = result.ImportResult;
            var message = $"整合包已导入到：{Environment.NewLine}{import.PackRoot}{Environment.NewLine}{Environment.NewLine}" +
                          $"下载 {import.DownloadedFiles} 个文件，解压 {import.ExtractedFiles} 个，跳过 {import.SkippedFiles} 个。";
            if (import.Warnings.Count > 0)
                message += $"{Environment.NewLine}{Environment.NewLine}提示：{Environment.NewLine}" +
                           string.Join(Environment.NewLine, import.Warnings.Take(10));
            if (import.MissingFiles.Count > 0)
                message += $"{Environment.NewLine}{Environment.NewLine}缺失 {import.MissingFiles.Count} 个文件，可稍后在主界面用「联网补全」或手动补充。";

            await _dialogs.ShowMessageAsync("整合包导入完成", message);
        }

        if (!string.IsNullOrWhiteSpace(result.PackRoot))
        {
            await OpenPackAsync(result.PackRoot, autoScan: true);
        }
        else if (!string.IsNullOrWhiteSpace(result.ModFilePath))
        {
            if (_pendingReplaceNodeId is not null && _graph is not null && _scanResult is not null)
            {
                var oldNode = _graph.FindNode(_pendingReplaceNodeId);
                var oldPath = oldNode is null ? null : _services.ModFiles.ResolveLocalFile(oldNode, _scanResult.RootPath);
                var sameProject = _pendingReplaceProjectIds.Overlaps(result.DownloadedProjectIds);
                if (!sameProject)
                {
                    StatusText = $"已下载模组 {Path.GetFileName(result.ModFilePath)}，未替换其他模组，正在重新扫描…";
                }
                else if (oldPath is not null &&
                    !string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(result.ModFilePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _services.ModFiles.Disable(oldNode!, _scanResult.RootPath);
                        StatusText = $"已下载同项目新版本并禁用旧版本，正在重新扫描…";
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        StatusText = $"新版本已下载，但旧版本禁用失败：{ex.Message}";
                    }
                }
                else
                {
                    StatusText = $"已下载模组 {Path.GetFileName(result.ModFilePath)}，正在重新扫描…";
                }
            }
            else
            {
                StatusText = $"已下载模组 {Path.GetFileName(result.ModFilePath)}，正在重新扫描…";
            }

            await ScanAsync();
        }
    }

    [RelayCommand]
    private async Task ImportLocalModpackAsync()
    {
        if (IsBusy) return;

        var archive = await _dialogs.PickOpenFileAsync(
            "选择整合包文件（.mrpack / .zip）", ["mrpack", "zip"], "整合包文件");
        if (string.IsNullOrWhiteSpace(archive)) return;

        await ImportModpackArchiveAsync(archive);
    }

    /// <summary>Called when a .mrpack/.zip is dropped onto the window.</summary>
    public async Task ImportDroppedModpackAsync(string archive)
    {
        if (IsBusy) return;
        if (!File.Exists(archive)) return;

        var extension = Path.GetExtension(archive);
        if (!extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return;

        await ImportModpackArchiveAsync(archive);
    }

    /// <summary>Copies JARs dropped onto the window into the current pack's mods folder.</summary>
    public async Task ImportDroppedJarsAsync(IReadOnlyList<string> jars)
    {
        if (IsBusy) return;
        if (_scanResult is null)
        {
            StatusText = "请先打开或扫描一个整合包，再拖入模组";
            await _dialogs.ShowMessageAsync("拖入模组", "请先打开或扫描一个整合包，再拖入 .jar 模组。");
            return;
        }

        var modsDirectory = Path.Combine(_scanResult.RootPath, "mods");
        try
        {
            Directory.CreateDirectory(modsDirectory);
            var copied = 0;
            var skipped = 0;
            var failures = new List<string>();
            foreach (var source in jars.Where(File.Exists))
            {
                try
                {
                    var name = Path.GetFileName(source);
                    var destination = Path.Combine(modsDirectory, name);
                    if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }

                    if (File.Exists(destination))
                    {
                        var stem = Path.GetFileNameWithoutExtension(name);
                        var extension = Path.GetExtension(name);
                        destination = Path.Combine(modsDirectory,
                            $"{stem}-dropped-{DateTime.Now:HHmmss}{extension}");
                    }

                    File.Copy(source, destination, overwrite: false);
                    copied++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    failures.Add($"{Path.GetFileName(source)}：{ex.Message}");
                }
            }

            StatusText = $"已拖入 {copied} 个模组" +
                         (skipped > 0 ? $"，跳过 {skipped} 个已在 mods 目录的文件" : string.Empty) +
                         "，正在重新扫描…";
            if (failures.Count > 0)
                await _dialogs.ShowMessageAsync("部分模组未导入",
                    string.Join(Environment.NewLine, failures.Take(10)));
            await ScanAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = $"拖入模组失败：{ex.Message}";
            await _dialogs.ShowMessageAsync("拖入模组失败", ex.Message);
        }
    }

    private async Task ImportModpackArchiveAsync(string archive)
    {
        var parent = await _dialogs.PickFolderAsync("选择整合包导入到的父文件夹");
        if (string.IsNullOrWhiteSpace(parent)) return;

        var cts = BeginOperation();
        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = "正在导入整合包…";
        StatusText = "正在读取整合包清单…";

        PackImportResult? import = null;
        var detail = new DownloadProgressViewModel(
            "导入整合包", $"{Path.GetFileName(archive)} → {parent}");
        DownloadProgressWindow.ShowFor(detail);
        try
        {
            var progress = new Progress<DownloadProgress>(report =>
            {
                detail.Update(report);
                ProgressValue = report.Fraction;
                ProgressText = report.Total > 0
                    ? $"{report.Phase} {report.Done}/{report.Total}"
                    : report.Phase;
                if (!string.IsNullOrWhiteSpace(report.Message))
                    StatusText = $"{report.Phase} · {report.Message}";
            });

            import = await _services.Importer.ImportAsync(archive, parent, progress, cts.Token);
            StatusText = $"整合包已导入：{import.PackRoot}";
            detail.Finish(true, $"整合包已导入：{import.PackRoot}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消导入";
            detail.Finish(false, "已取消导入");
        }
        catch (Exception ex)
        {
            StatusText = $"导入失败：{ex.Message}";
            detail.Finish(false, ex.Message);
            await _dialogs.ShowMessageAsync("导入失败", ex.Message);
        }
        finally
        {
            EndOperation(cts);
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }

        if (import is null) return;

        var message = $"下载 {import.DownloadedFiles} 个文件，解压 {import.ExtractedFiles} 个，跳过 {import.SkippedFiles} 个。";
        if (import.Warnings.Count > 0)
            message += $"{Environment.NewLine}{Environment.NewLine}提示：{Environment.NewLine}" +
                       string.Join(Environment.NewLine, import.Warnings.Take(10));
        if (import.MissingFiles.Count > 0)
            message += $"{Environment.NewLine}{Environment.NewLine}缺失 {import.MissingFiles.Count} 个文件，可稍后用「联网补全」或手动补充。";

        await _dialogs.ShowMessageAsync("整合包导入完成", message);
        await OpenPackAsync(import.PackRoot, autoScan: true);
    }

    public async Task InstallGameFromCliAsync(string spec)
    {
        var parts = spec.Split(';');
        if (parts.Length < 4)
        {
            StatusText = "参数格式：--install-game=MC版本;加载器;加载器版本;目标目录";
            return;
        }

        var loader = parts[1].Trim().ToLowerInvariant() switch
        {
            "fabric" => GameLoader.Fabric,
            "quilt" => GameLoader.Quilt,
            "forge" => GameLoader.Forge,
            "neoforge" => GameLoader.NeoForge,
            _ => GameLoader.Vanilla,
        };

        var request = new GameInstallRequest
        {
            TargetDirectory = parts[3].Trim(),
            MinecraftVersion = parts[0].Trim(),
            Loader = loader,
            LoaderVersion = loader == GameLoader.Vanilla ? null : parts[2].Trim(),
            DownloadAssets = false,
            JavaPath = MinecraftInstallService.DetectJava(),
        };

        var cts = BeginOperation();
        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = "正在下载游戏本体…";
        StatusText = $"正在安装 {request.MinecraftVersion} / {loader}…";

        try
        {
            var progress = new Progress<DownloadProgress>(report =>
            {
                ProgressValue = report.Fraction;
                ProgressText = report.Total > 0
                    ? $"{report.Phase} {report.Done}/{report.Total}"
                    : report.Phase;
                if (!string.IsNullOrWhiteSpace(report.Message))
                    StatusText = $"{report.Phase} · {report.Message}";
            });

            var result = await _services.GameInstaller.InstallAsync(request, progress, cts.Token);
            StatusText = $"安装完成：{result.InstanceDirectory}（{result.FilesDownloaded} 个文件）";
            await OpenPackAsync(result.InstanceDirectory, autoScan: true);
        }
        catch (Exception ex)
        {
            StatusText = $"安装失败：{ex.Message}";
        }
        finally
        {
            EndOperation(cts);
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task OpenLoaderSwitchAsync()
    {
        var context = new LoaderSwitchContext
        {
            InstanceDirectory = _scanResult?.RootPath ?? PackPath,
            MinecraftVersion = _scanResult?.Profile.McVersion,
            CurrentLoader = _scanResult?.Profile.Loader ?? LoaderKind.Unknown,
            CurrentLoaderVersion = _scanResult?.Profile.LoaderVersion,
        };

        var result = await _dialogs.ShowLoaderSwitchAsync(context);
        if (result is null) return;

        var message = $"实例：{result.InstanceDirectory}{Environment.NewLine}" +
                      $"Minecraft {result.MinecraftVersion} / {result.LoaderName}" +
                      (string.IsNullOrWhiteSpace(result.LoaderVersion) ? "" : $" {result.LoaderVersion}") +
                      $"{Environment.NewLine}mods 与 config 已保留，旧加载器版本文件未删除。";
        if (result.Warnings.Count > 0)
            message += $"{Environment.NewLine}{Environment.NewLine}提示：{Environment.NewLine}" +
                       string.Join(Environment.NewLine, result.Warnings.Take(8));
        message += $"{Environment.NewLine}{Environment.NewLine}接下来会重扫该实例，不兼容的模组会出现在问题报告里。";

        await _dialogs.ShowMessageAsync("加载器切换完成", message);
        await OpenPackAsync(result.InstanceDirectory, autoScan: true);
    }

    [RelayCommand]
    private void OpenPackFolder()
    {
        var path = _scanResult?.RootPath ?? PackPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            StatusText = "尚未打开整合包目录。";
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        StatusText = $"已打开：{path}";
    }

    [RelayCommand]
    private async Task OpenGameSetupAsync()
    {
        var result = await _dialogs.ShowGameSetupAsync();
        if (result is null) return;

        var message = $"实例目录：{Environment.NewLine}{result.InstanceDirectory}{Environment.NewLine}{Environment.NewLine}" +
                      $"Minecraft {result.MinecraftVersion} / {result.LoaderName}" +
                      (string.IsNullOrWhiteSpace(result.LoaderVersion) ? "" : $" {result.LoaderVersion}") +
                      $"{Environment.NewLine}下载 {result.FilesDownloaded} 个文件，{result.BytesDownloaded / 1024d / 1024d:0.#} MB。";
        if (result.Warnings.Count > 0)
            message += $"{Environment.NewLine}{Environment.NewLine}提示：{Environment.NewLine}" +
                       string.Join(Environment.NewLine, result.Warnings.Take(10));
        message += $"{Environment.NewLine}{Environment.NewLine}该目录已写入 launcher_profiles.json、versions、libraries、assets；" +
                   $"在启动器里把这个实例目录选为游戏目录即可直接读取版本，不会重复下载。{Environment.NewLine}" +
                   $"接下来会把它作为整合包打开并扫描，可直接往 mods 里放不同加载器版本的模组。";

        await _dialogs.ShowMessageAsync("游戏本体与加载器安装完成", message);
        await OpenPackAsync(result.InstanceDirectory, autoScan: true);
    }

    [RelayCommand]
    private async Task ExportPclPackAsync()
    {
        if (IsBusy) return;
        if (_graph is null || _scanResult is null)
        {
            StatusText = "请先扫描整合包";
            return;
        }

        var packName = string.IsNullOrWhiteSpace(_scanResult.Profile.PackName)
            ? new DirectoryInfo(_scanResult.RootPath).Name
            : _scanResult.Profile.PackName!;
        var suggested = $"{packName}.mrpack";
        var target = await _dialogs.PickSaveFileAsync(
            "导出整合包", suggested, "mrpack", "Modrinth 整合包 (.mrpack)");
        if (string.IsNullOrWhiteSpace(target)) return;

        await ExportPclPackToAsync(target);
    }

    public async Task ExportPclPackToAsync(string target)
    {
        if (_graph is null || _scanResult is null) return;

        var cts = BeginOperation();
        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = "正在导出 .mrpack…";
        StatusText = "正在分析可在线下载的模组…";

        PclPackExportResult? result = null;
        try
        {
            var progress = new Progress<string>(message =>
            {
                StatusText = message;
                ProgressText = message;
            });

            result = await _services.PclExporter.ExportAsync(
                _graph, _scanResult.Profile, _scanResult.RootPath, target, progress, cts.Token);
            StatusText = $"已导出整合包：{result.FilePath}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消导出";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
            await _dialogs.ShowMessageAsync("导出失败", ex.Message);
        }
        finally
        {
            EndOperation(cts);
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }

        if (result is null) return;

        var message = $"文件：{result.FilePath}{Environment.NewLine}" +
                      $"Modrinth 在线文件 {result.ModrinthFiles} 个，overrides 模组 {result.OverrideMods} 个，配置 {result.ConfigFiles} 个。";
        if (result.Warnings.Count > 0)
            message += $"{Environment.NewLine}{Environment.NewLine}提示：{Environment.NewLine}" +
                       string.Join(Environment.NewLine, result.Warnings.Take(10));
        message += $"{Environment.NewLine}{Environment.NewLine}可直接把此 .mrpack 拖入支持该格式的启动器导入。";

        await _dialogs.ShowMessageAsync("整合包导出完成", message);
    }

    public async Task IgnoreMissingDependencyAsync(NodeItemViewModel item)
    {
        if (_graph is null || _scanResult is null) return;
        var missing = item.Model;
        if (!missing.IsSynthetic || missing.Status != ModStatus.Missing) return;

        var existing = _localRules.FirstOrDefault(rule =>
            rule.Type == LocalRuleType.IgnoreMissing &&
            string.Equals(rule.To, missing.Id, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(rule.From) || rule.From == "*"));
        if (existing is not null)
        {
            StatusText = $"{missing.Id} 已被本地规则忽略";
            return;
        }

        var rule = new LocalRule
        {
            Type = LocalRuleType.IgnoreMissing,
            From = "*",
            To = missing.Id,
            Note = "从缺失依赖列表忽略",
            Enabled = true,
        };
        await SaveLocalRuleAsync(rule);
        StatusText = $"已本地忽略缺失依赖：{missing.Id}";
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (_graph is null || _scanResult is null || IsBusy || IsCheckingUpdates) return;
        if (IsOffline)
        {
            StatusText = "离线模式：无法检查模组更新";
            return;
        }

        var nodes = _graph.Nodes
            .Where(node => !node.IsSynthetic && !PlatformModIds.IsPlatform(node.Id) &&
                           !string.IsNullOrWhiteSpace(node.FilePath))
            .ToList();
        if (nodes.Count == 0) return;

        var cts = BeginOperation();
        IsBusy = true;
        IsCheckingUpdates = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = "识别在线项目…";
        StatusText = "正在匹配模组项目…";

        // Resolve unknown Modrinth projects in one hash batch instead of doing
        // a search request per mod. Unmatched mods are reported as unknown and
        // are never turned into one-off search requests.
        var unknownHashes = nodes
            .Where(node => string.IsNullOrWhiteSpace(node.ProjectIds.Modrinth) &&
                           string.IsNullOrWhiteSpace(node.ProjectIds.CurseForge) &&
                           !string.IsNullOrWhiteSpace(node.Sha1))
            .Select(node => node.Sha1!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unknownHashes.Count > 0)
        {
            try
            {
                var matched = await _services.Modrinth.GetVersionsByHashesAsync(unknownHashes, cts.Token)
                    .ConfigureAwait(true);
                foreach (var node in nodes)
                {
                    if (string.IsNullOrWhiteSpace(node.ProjectIds.Modrinth) &&
                        !string.IsNullOrWhiteSpace(node.Sha1) &&
                        matched.TryGetValue(node.Sha1, out var matchedVersion))
                    {
                        node.ProjectIds.Modrinth = matchedVersion.ProjectId;
                    }
                }
            }
            catch (Exception ex) when (ex is ModrinthApiException or HttpRequestException)
            {
                // A failed batch must not turn into hundreds of fallback searches.
                StatusText = $"项目批量匹配失败，继续使用已知项目：{ex.Message}";
            }
        }

        StatusText = $"正在检查 {nodes.Count} 个模组的更新…";
        var states = new System.Collections.Concurrent.ConcurrentDictionary<string, ModUpdateState>(
            StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        var unresolved = 0;
        var cachedCount = 0;
        IProgress<(int Done, int Total, string Name)> progress =
            new Progress<(int Done, int Total, string Name)>(value =>
        {
            ProgressValue = (double)value.Done / value.Total;
            ProgressText = $"检查更新 {value.Done}/{value.Total}";
            StatusText = $"检查更新 · {value.Name}";
        });

        try
        {
            await Parallel.ForEachAsync(
                nodes,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 5,
                    CancellationToken = cts.Token,
                },
                async (node, token) =>
                {
                    var done = Interlocked.Increment(ref completed);
                    progress.Report((done, nodes.Count, node.Name));
                    try
                    {
                        DownloadProject? project = null;
                        if (!string.IsNullOrWhiteSpace(node.ProjectIds.Modrinth))
                        {
                            project = new DownloadProject
                            {
                                Source = DownloadSource.Modrinth,
                                Type = DownloadProjectType.Mod,
                                ProjectId = node.ProjectIds.Modrinth,
                                Name = node.Name,
                                Slug = node.Slug,
                            };
                        }
                        else if (!string.IsNullOrWhiteSpace(node.ProjectIds.CurseForge))
                        {
                            project = new DownloadProject
                            {
                                Source = DownloadSource.CurseForge,
                                Type = DownloadProjectType.Mod,
                                ProjectId = node.ProjectIds.CurseForge,
                                Name = node.Name,
                                Slug = node.Slug,
                            };
                        }

                        if (project is null)
                        {
                            Interlocked.Increment(ref unresolved);
                            states[node.Id] = new ModUpdateState(false, null, "未识别项目，需先联网补全");
                            return;
                        }

                        var ranges = _graph.Edges
                            .Where(edge => string.Equals(edge.To, node.Id, StringComparison.OrdinalIgnoreCase) &&
                                           edge.Type == EdgeType.Requires &&
                                           !string.IsNullOrWhiteSpace(edge.VersionRange))
                            .Select(edge => edge.VersionRange!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var cacheKey = $"{project.Source}:{project.ProjectId}:{_scanResult.Profile.McVersion}:" +
                                       $"{_scanResult.Profile.Loader}:{node.Version}:" +
                                       string.Join('|', ranges.OrderBy(range => range, StringComparer.OrdinalIgnoreCase));
                        if (_updateCheckCache.TryGetValue(cacheKey, out var cached) &&
                            DateTimeOffset.UtcNow - cached.CheckedAt < UpdateCheckTtl)
                        {
                            states[node.Id] = cached.State;
                            Interlocked.Increment(ref cachedCount);
                            return;
                        }

                        var versions = await _services.Downloader.GetVersionsAsync(
                                project, _scanResult.Profile.McVersion, _scanResult.Profile.Loader, token)
                            .ConfigureAwait(false);
                        var latest = versions
                            .Where(version => version.IsCompatible &&
                                              ranges.All(range =>
                                                  VersionRange.Satisfies(range, version.VersionNumber) != false))
                            .OrderByDescending(version => version.PublishedAt ?? DateTimeOffset.MinValue)
                            .FirstOrDefault();
                        if (latest is null)
                        {
                            states[node.Id] = new ModUpdateState(false, null, "没有兼容版本");
                            return;
                        }

                        var currentFile = string.IsNullOrWhiteSpace(node.FilePath)
                            ? null
                            : Path.GetFileName(node.FilePath);
                        var differs = !string.Equals(latest.FileName, currentFile,
                                           StringComparison.OrdinalIgnoreCase) ||
                                      ModVersion.Compare(latest.VersionNumber, node.Version) > 0;
                        var state = new ModUpdateState(differs, latest.VersionNumber, null);
                        states[node.Id] = state;
                        _updateCheckCache[cacheKey] = new CachedModUpdate(state, DateTimeOffset.UtcNow);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or
                                                   InvalidOperationException or CurseForgeApiException or
                                                   ModrinthApiException or MissingApiKeyException)
                    {
                        states[node.Id] = new ModUpdateState(false, null, ex.Message);
                    }
                }).ConfigureAwait(true);

            _updateStates.Clear();
            foreach (var (id, state) in states)
                _updateStates[id] = state;
            HasUpdateCheck = true;
            RefreshView();
            var updatable = _updateStates.Values.Count(state => state.Available);
            var cachedText = cachedCount > 0 ? $"，{cachedCount} 个使用缓存" : string.Empty;
            StatusText = unresolved > 0
                ? $"更新检查完成：{updatable} 个可更新{cachedText}，{unresolved} 个未识别项目（可先联网补全）"
                : $"更新检查完成：{updatable} 个可更新{cachedText}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消更新检查";
        }
        catch (Exception ex)
        {
            StatusText = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            EndOperation(cts);
            IsBusy = false;
            IsCheckingUpdates = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    // ------------------------------------------------------------------ local rules

    [RelayCommand]
    private void CancelUpdateCheck()
    {
        if (IsCheckingUpdates)
            _operationCts?.Cancel();
    }

    [RelayCommand]
    private async Task AddLocalRuleAsync()
    {
        if (_graph is null)
        {
            StatusText = "请先扫描整合包";
            return;
        }

        var rule = await _dialogs.ShowLocalRuleAsync(null, AvailableModIds.ToList());
        if (rule is null) return;
        await SaveLocalRuleAsync(rule);
    }

    [RelayCommand]
    private async Task EditLocalRuleAsync()
    {
        if (SelectedLocalRule is null)
        {
            StatusText = "请先选择一条本地规则";
            return;
        }

        var edited = await _dialogs.ShowLocalRuleAsync(SelectedLocalRule.Model, AvailableModIds.ToList());
        if (edited is null) return;

        edited.Id = SelectedLocalRule.Model.Id;
        edited.CreatedAt = SelectedLocalRule.Model.CreatedAt;
        await SaveLocalRuleAsync(edited);
    }

    [RelayCommand]
    private async Task DeleteLocalRuleAsync()
    {
        if (SelectedLocalRule is null)
        {
            StatusText = "请先选择一条本地规则";
            return;
        }

        var rule = SelectedLocalRule.Model;
        var confirmed = await _dialogs.ConfirmAsync(
            "删除本地规则",
            $"确定删除这条规则吗？\n\n{LocalRuleText.TypeName(rule.Type)}：{LocalRuleText.Describe(rule)}",
            "删除");
        if (!confirmed) return;

        _localRules.RemoveAll(existing => existing.Id == rule.Id);
        await PersistLocalRulesAsync();
        SelectedLocalRule = null;
        await RebuildGraphAsync();
        StatusText = "已删除本地规则";
    }

    [RelayCommand]
    private async Task ImportLocalRulesAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("导入本地规则", "json", "JSON 文件");
        if (string.IsNullOrWhiteSpace(path)) return;

        var result = await _services.LocalRules.ImportAsync(path);
        if (result.Errors.Count > 0)
        {
            await _dialogs.ShowMessageAsync(
                "导入完成（含问题）",
                $"成功导入 {result.Rules.Count} 条，{result.Errors.Count} 条被跳过：\n\n" +
                string.Join("\n", result.Errors.Take(12)));
        }

        if (result.Rules.Count == 0) return;

        foreach (var imported in result.Rules)
        {
            var index = _localRules.FindIndex(existing =>
                string.Equals(existing.Id, imported.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _localRules[index] = imported;
            else _localRules.Add(imported);
        }

        await PersistLocalRulesAsync();
        await RebuildGraphAsync();
        StatusText = $"已导入 {result.Rules.Count} 条本地规则";
    }

    [RelayCommand]
    private async Task ExportLocalRulesAsync()
    {
        if (_scanResult is null)
        {
            StatusText = "请先扫描整合包";
            return;
        }

        var path = await _dialogs.PickSaveFileAsync(
            "导出本地规则", "local-rules.json", "json", "JSON 文件");
        if (string.IsNullOrWhiteSpace(path)) return;

        await _services.LocalRules.ExportAsync(path, _scanResult.RootPath, _localRules);
        StatusText = $"已导出本地规则：{path}";
    }

    private async Task SaveLocalRuleAsync(LocalRule rule)
    {
        if (_scanResult is null) return;

        var index = _localRules.FindIndex(existing =>
            string.Equals(existing.Id, rule.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _localRules[index] = rule;
        else _localRules.Add(rule);

        await PersistLocalRulesAsync();
        await RebuildGraphAsync();
        SelectedLocalRule = LocalRules.FirstOrDefault(item =>
            string.Equals(item.Model.Id, rule.Id, StringComparison.OrdinalIgnoreCase));
        StatusText = $"已保存本地规则：{LocalRuleText.TypeName(rule.Type)}";
    }

    private async Task PersistLocalRulesAsync()
    {
        if (_scanResult is null) return;
        try
        {
            await _services.LocalRules.SaveAsync(_scanResult.RootPath, _localRules);
        }
        catch (Exception ex)
        {
            StatusText = $"保存本地规则失败：{ex.Message}";
        }
    }

    private void OnLocalRuleToggled(LocalRuleItemViewModel item)
    {
        var rule = _localRules.FirstOrDefault(existing =>
            string.Equals(existing.Id, item.Model.Id, StringComparison.OrdinalIgnoreCase));
        if (rule is null) return;

        rule.Enabled = item.IsEnabled;
        rule.UpdatedAt = DateTimeOffset.UtcNow;

        Dispatcher.UIThread.Post(async () =>
        {
            await PersistLocalRulesAsync();
            await RebuildGraphAsync();
            StatusText = item.IsEnabled
                ? $"已启用本地规则：{LocalRuleText.TypeName(rule.Type)}"
                : $"已停用本地规则：{LocalRuleText.TypeName(rule.Type)}";
        });
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var previousLayout = _services.Settings.LayoutAlgorithm;
        var updated = await _dialogs.ShowSettingsAsync(_services.Settings);
        if (updated is null) return;

        _services.Settings.CopyFrom(updated);
        ApplyTheme();
        IsOffline = !_services.Settings.NetworkEnabled;
        OnPropertyChanged(nameof(NetworkStatusText));
        _services.SaveSettings();

        if (_services.Settings.LayoutAlgorithm != previousLayout && _graph is not null)
        {
            await RelayoutAsync();
        }
        else
        {
            StatusText = "设置已保存";
            if (!string.IsNullOrWhiteSpace(_services.Settings.ModrinthContact))
                StatusText += "（Modrinth User-Agent 将在下次启动生效）";
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_services.Paths.DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"无法打开数据目录：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenCurseForge()
    {
        if (_selectedNodeId is not null) OpenCurseForgeForNode(_selectedNodeId);
    }

    [RelayCommand]
    private void OpenModrinth()
    {
        if (_selectedNodeId is not null) OpenModrinthForNode(_selectedNodeId);
    }

    [RelayCommand]
    private void OpenMcmod()
    {
        if (_selectedNodeId is not null) OpenMcmodForNode(_selectedNodeId);
    }

    [RelayCommand]
    private async Task EditChineseNameAsync()
    {
        if (_selectedNodeId is null || _graph is null) return;
        var node = _graph.FindNode(_selectedNodeId);
        if (node is null) return;

        var value = await _dialogs.ShowTextInputAsync(
            "设置中文名",
            $"为 {node.Name}（{node.Id}）设置中文显示名。留空则清除自定义名称。",
            "中文名",
            node.NameZh ?? string.Empty);
        if (value is null) return;

        if (string.IsNullOrWhiteSpace(value))
            _translationOverrides.Remove(node.Id);
        else
            _translationOverrides[node.Id] = value.Trim();

        await _services.TranslationOverrides.SaveAsync(_translationOverrides);
        ApplyTranslations();
        RefreshAnalysis();
        BuildScene();
        SelectNode(node.Id, center: false, fromGraph: true);
        StatusText = string.IsNullOrWhiteSpace(value)
            ? $"已恢复 {node.Id} 的内置译名"
            : $"已设置 {node.Id} 的中文名：{value.Trim()}";
    }

    [RelayCommand]
    private async Task OpenSelectedProjectAsync()
    {
        if (_selectedNodeId is not null) OpenProjectForNode(_selectedNodeId);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenSelectedFile()
    {
        if (_selectedNodeId is not null) OpenFileForNode(_selectedNodeId);
    }

    [RelayCommand]
    private async Task CopySelectedIdAsync()
    {
        if (_selectedNodeId is not null) await CopyIdAsync(_selectedNodeId);
    }

    [RelayCommand]
    public void ClearSelection() => ClearSelection(fromGraph: false);

    [RelayCommand]
    private void ShowInGraph()
    {
        if (_selectedNodeId is not null) ShowGraphAndFocus(_selectedNodeId);
    }

    public Task OpenPathAsync(string path) => OpenPackAsync(path, autoScan: true);

    // ------------------------------------------------------------------ selection wiring

    partial void OnSelectedNodeItemChanged(NodeItemViewModel? value)
    {
        if (_suppressSelectionSync || value is null) return;
        SelectNode(value.Id, center: true, fromGraph: false);
    }

    partial void OnSelectedIssueChanged(IssueItemViewModel? value)
    {
        if (_suppressSelectionSync || value?.NodeId is not { } id) return;
        SelectNode(id, center: true, fromGraph: false);
    }

    partial void OnSelectedManualLinkChanged(ManualLinkItemViewModel? value)
    {
        if (_suppressSelectionSync || value is null) return;
        SelectEdge($"manual:{value.Model.Id}", center: true, fromGraph: false);
    }

    partial void OnSelectedRecentPackChanged(string? value)
    {
        if (_suppressRecent || string.IsNullOrWhiteSpace(value)) return;
        _ = OpenPackAsync(value, autoScan: true);
    }

    partial void OnSelectedViewOptionChanged(ViewOption value)
    {
        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(IsGraphView));

        if (IsGraphView)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _graphView?.FitToView();
                if (_pendingFocusNodeId is { } focusId)
                {
                    _pendingFocusNodeId = null;
                    _graphView?.SelectNode(focusId, center: true);
                }
            }, DispatcherPriority.Loaded);
        }
    }

    public void HandleGraphSelection(IReadOnlyList<string> nodeIds, string? edgeId)
    {
        if (!string.IsNullOrWhiteSpace(edgeId))
        {
            SelectEdge(edgeId, center: false, fromGraph: true);
            return;
        }

        if (nodeIds.Count == 0)
        {
            ClearSelection(fromGraph: true);
        }
        else if (nodeIds.Count == 1)
        {
            SelectNode(nodeIds[0], center: false, fromGraph: true);
        }
        else
        {
            ShowMultiSelection(nodeIds);
        }
    }

    public void SelectNode(string id, bool center = true, bool fromGraph = false)
    {
        var node = _graph?.FindNode(id);
        if (node is null) return;

        _selectedNodeId = node.Id;
        _selectedEdgeId = null;
        UpdateNodeDetail(node);

        if (!fromGraph) _graphView?.SelectNode(node.Id, center);

        _suppressSelectionSync = true;
        SelectedNodeItem = NodeItems.FirstOrDefault(n => string.Equals(n.Id, node.Id, StringComparison.OrdinalIgnoreCase));
        SelectedManualLink = null;
        SelectedIssue = null;
        _suppressSelectionSync = false;
    }

    /// <summary>
    /// Brings a dependency target into the mod list. If the target is hidden by
    /// the current filters/search, the filters are reset so the locate button
    /// never silently does nothing.
    /// </summary>
    public NodeItemViewModel? LocateNode(string id)
    {
        if (_graph is null || _graph.FindNode(id) is null) return null;

        var item = NodeItems.FirstOrDefault(node =>
            string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            ShowListView();
            SelectNode(id, center: false);
            return item;
        }

        var node = _graph.FindNode(id)!;
        if (node.IsSynthetic)
        {
            ShowListView();
            if (node.IsOptionalDependency || node.IsRecommendedDependency)
                ShowUninstalledOptional = true;
            else
            {
                ShowMissing = true;
                IsMissingSectionExpanded = true;
            }
            SelectNode(id, center: false);
            return null;
        }

        // Installed mod hidden by a filter or relation focus.
        ClearRelationFocus();
        ResetFilters();
        OnlyUpdatable = false;
        ShowMissing = true;
        ShowListView();
        SelectNode(id, center: false);
        return NodeItems.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public void ShowGraphAndFocus(string id)
    {
        _pendingFocusNodeId = id;
        SelectedViewOption = ViewOption.All.First(option => option.Value == "graph");
        SelectNode(id, center: false);
    }

    public void ShowListView() => SelectedViewOption = ViewOption.All.First(option => option.Value == "list");

    /// <summary>All mods that can be the target of a manual relation from <paramref name="fromId"/>.</summary>
    public IReadOnlyList<ModNode> GetManualLinkTargets(string fromId) =>
        _graph?.Nodes
            .Where(node => !string.Equals(node.Id, fromId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .ToList() ?? [];

    /// <summary>Creates/replaces a manual relation directly (used by the right-click submenu).</summary>
    public async Task CreateManualLinkAsync(string fromId, string toId, EdgeType type)
    {
        if (_graph is null) return;

        var link = new ManualLink
        {
            From = fromId,
            To = toId,
            Type = type,
            Side = ModSide.Both,
        };

        await SaveManualLinkAsync(link);
        StatusText = $"已创建{GraphPalette.EdgeText(type)}关系：{fromId} → {toId}";
    }

    public async Task CopyNodeIdAsync(string nodeId) => await CopyIdAsync(nodeId);

    public void SelectEdge(string edgeId, bool center = true, bool fromGraph = false)
    {
        var edge = _graph?.Edges.FirstOrDefault(e => string.Equals(e.Id, edgeId, StringComparison.OrdinalIgnoreCase));
        if (edge is null) return;

        _selectedEdgeId = edge.Id;
        _selectedNodeId = null;
        UpdateEdgeDetail(edge);

        if (!fromGraph) _graphView?.SelectEdge(edge.Id, center);

        _suppressSelectionSync = true;
        SelectedNodeItem = null;
        SelectedManualLink = ManualLinks.FirstOrDefault(m =>
            string.Equals(m.Model.Id, edge.ManualLinkId, StringComparison.OrdinalIgnoreCase));
        SelectedIssue = null;
        _suppressSelectionSync = false;
    }

    public void ClearSelection(bool fromGraph)
    {
        _selectedNodeId = null;
        _selectedEdgeId = null;
        HasSelection = false;
        IsNodeSelected = false;
        IsEdgeSelected = false;
        IsMultiSelection = false;
        CanAddRelation = false;
        CanEditManual = false;
        CanDeleteManual = false;
        CanOpenProject = false;
        CanOpenFile = false;
        CanOpenCurseForge = false;
        CanOpenModrinth = false;
        CanOpenMcmod = false;
        CanDisableSelected = false;
        CanDeleteSelected = false;
        CanDownloadOptional = false;
        DownloadSelectedLabel = "下载此模组";
        DetailTitle = "未选择";
        DetailSubtitle = "在图中点选节点或关系查看详情";
        DetailRows.Clear();
        SelectedDependencies.Clear();
        SelectedDependencyGroups.Clear();
        HasDependencyRows = false;
        DetailIcon = null;
        HasDetailIcon = false;
        DetailInitial = "?";
        DetailDescription = string.Empty;
        HasDetailDescription = false;

        _suppressSelectionSync = true;
        SelectedNodeItem = null;
        SelectedManualLink = null;
        SelectedIssue = null;
        _suppressSelectionSync = false;

        if (!fromGraph) _graphView?.ClearSelection();
    }

    private void ShowMultiSelection(IReadOnlyList<string> nodeIds)
    {
        _selectedNodeId = null;
        _selectedEdgeId = null;
        HasSelection = true;
        IsNodeSelected = false;
        IsEdgeSelected = false;
        IsMultiSelection = true;
        CanAddRelation = false;
        CanEditManual = false;
        CanDeleteManual = false;
        CanOpenProject = false;
        CanOpenFile = false;
        CanOpenCurseForge = false;
        CanOpenModrinth = false;
        CanOpenMcmod = false;
        CanDisableSelected = false;
        CanDeleteSelected = false;
        CanDownloadOptional = false;
        DetailTitle = $"已选择 {nodeIds.Count} 个模组";
        DetailSubtitle = "框选结果 · 在左侧列表或图中选择单个节点查看详情";
        DetailRows.Clear();
        SelectedDependencies.Clear();
        SelectedDependencyGroups.Clear();
        HasDependencyRows = false;
        DetailIcon = null;
        HasDetailIcon = false;
        DetailInitial = "?";
        DetailDescription = string.Empty;
        HasDetailDescription = false;

        var models = nodeIds
            .Select(id => _graph?.FindNode(id))
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();
        DetailRows.Add(new DetailRowViewModel("缺失", models.Count(n => n.Status == ModStatus.Missing).ToString()));
        DetailRows.Add(new DetailRowViewModel("冲突", models.Count(n => n.Status == ModStatus.Conflict).ToString()));
        DetailRows.Add(new DetailRowViewModel("版本不匹配", models.Count(n => n.Status == ModStatus.VersionMismatch).ToString()));
        DetailRows.Add(new DetailRowViewModel("模组", string.Join(", ", models.Take(12).Select(n => n.Id)) +
                                                          (models.Count > 12 ? " …" : string.Empty)));

        _suppressSelectionSync = true;
        SelectedNodeItem = null;
        SelectedManualLink = null;
        _suppressSelectionSync = false;
    }

    private void UpdateNodeDetail(ModNode node)
    {
        HasSelection = true;
        IsNodeSelected = true;
        IsEdgeSelected = false;
        IsMultiSelection = false;
        CanAddRelation = true;
        CanOpenProject = HasProjectLink(node);
        CanOpenFile = !string.IsNullOrWhiteSpace(node.FilePath);
        CanEditManual = false;
        CanDeleteManual = false;
        CanOpenCurseForge = true;
        CanOpenModrinth = true;
        CanOpenMcmod = true;
        CanDisableSelected = HasLocalJar(node);
        CanDeleteSelected = HasLocalJar(node);
        DownloadSelectedLabel = HasLocalJar(node) ? "更换版本" : "下载此模组";
        CanDownloadOptional = node.IsSynthetic && (node.IsOptionalDependency || node.IsRecommendedDependency);

        var displayName = !string.IsNullOrWhiteSpace(node.NameZh)
            ? node.NameZh!
            : (string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name);
        DetailTitle = displayName;
        DetailSubtitle = !string.IsNullOrWhiteSpace(node.NameZh)
            ? $"{node.Name} · {node.Id} · {node.DisplayVersion}"
            : $"{node.Id} · {node.DisplayVersion}";
        DetailIcon = ModIconCache.Get(node);
        HasDetailIcon = DetailIcon is not null;
        DetailInitial = BuildInitial(node);
        DetailAvatarBrush = new SolidColorBrush(GraphPalette.LoaderColor(node.Loader));
        DetailDescription = node.Description ?? string.Empty;
        HasDetailDescription = !string.IsNullOrWhiteSpace(DetailDescription);
        DetailRows.Clear();

        var status = node.IsSynthetic && node.Status == ModStatus.Unknown
            ? "未安装（可选依赖）"
            : GraphPalette.StatusText(node.Status);
        if (!string.IsNullOrWhiteSpace(node.StatusReason)) status += $"（{node.StatusReason}）";
        DetailRows.Add(new DetailRowViewModel("状态", status));
        if (node.IsSynthetic)
            DetailRows.Add(new DetailRowViewModel("说明", node.IsOptionalDependency
                ? "JAR 未安装；它只作为另一个模组的可选依赖占位，不是缺失的必需依赖。"
                : node.IsRecommendedDependency
                    ? "JAR 未安装；它只作为另一个模组的推荐依赖占位，不是缺失的必需依赖。"
                    : "JAR 未安装；它被其他模组声明为必需依赖，因此标记为缺失。"));
        DetailRows.Add(new DetailRowViewModel("modId", node.Id));
        if (!string.IsNullOrWhiteSpace(node.NameZh))
            DetailRows.Add(new DetailRowViewModel("英文名", node.Name));
        if (node.McmodId is > 0)
            DetailRows.Add(new DetailRowViewModel("百科 ID", node.McmodId.Value.ToString()));
        DetailRows.Add(new DetailRowViewModel("版本", node.DisplayVersion));
        DetailRows.Add(new DetailRowViewModel("加载器", GraphPalette.LoaderText(node.Loader)));
        DetailRows.Add(new DetailRowViewModel("侧",
            GraphPalette.SideText(node.Side) + (node.SideInferred ? "（推断）" : string.Empty)));
        var mcText = node.McVersions.Count > 0
            ? string.Join(", ", node.McVersions)
            : (string.IsNullOrWhiteSpace(node.McVersionRange) ? "unknown" : node.McVersionRange);
        DetailRows.Add(new DetailRowViewModel("Minecraft", mcText));
        DetailRows.Add(new DetailRowViewModel("分类", ModCategoryText.Name(node.Category)));
        DetailRows.Add(new DetailRowViewModel(
            "来源", node.IsSynthetic ? "其他模组的可选/推荐依赖引用（未安装）" : SourceText(node.Source)));
        if (!string.IsNullOrWhiteSpace(node.ProjectTitle))
            DetailRows.Add(new DetailRowViewModel("项目名称", node.ProjectTitle));
        if (!string.IsNullOrWhiteSpace(node.ProjectIds.Modrinth))
            DetailRows.Add(new DetailRowViewModel("Modrinth", node.ProjectIds.Modrinth));
        if (!string.IsNullOrWhiteSpace(node.ProjectIds.CurseForge))
            DetailRows.Add(new DetailRowViewModel("CurseForge", node.ProjectIds.CurseForge));
        if (!string.IsNullOrWhiteSpace(node.Slug))
            DetailRows.Add(new DetailRowViewModel("slug", node.Slug));
        if (!string.IsNullOrWhiteSpace(node.FilePath))
            DetailRows.Add(new DetailRowViewModel("文件", node.FilePath));
        if (node.IsEmbedded)
            DetailRows.Add(new DetailRowViewModel("内嵌", "是（Jar-in-Jar）"));
        if (!string.IsNullOrWhiteSpace(node.Authors))
            DetailRows.Add(new DetailRowViewModel("作者", node.Authors));
        if (node.Provides.Count > 0)
            DetailRows.Add(new DetailRowViewModel("提供 modId", string.Join(", ", node.Provides)));

        BuildDependencyRows(node);
    }

    private void BuildDependencyRows(ModNode node)
    {
        SelectedDependencies.Clear();
        SelectedDependencyGroups.Clear();
        HasDependencyRows = false;
        if (_graph is null) return;

        var rows = new List<(int Order, DependencyRowViewModel Row)>();

        foreach (var edge in _graph.Edges)
        {
            var outgoing = string.Equals(edge.From, node.Id, StringComparison.OrdinalIgnoreCase);
            var incoming = string.Equals(edge.To, node.Id, StringComparison.OrdinalIgnoreCase);
            if (!outgoing && !incoming) continue;

            var otherId = outgoing ? edge.To : edge.From;
            var other = _graph.FindNode(otherId);
            var dependencyTarget = outgoing ? other : node;

            var (result, brush, order) = DescribeDependency(edge, dependencyTarget, outgoing);
            var inactiveConflict = edge.Type == EdgeType.Incompatible &&
                                   (dependencyTarget is null || dependencyTarget.IsSynthetic ||
                                    VersionRange.Satisfies(edge.VersionRange, dependencyTarget.Version) != true);
            rows.Add((
                (outgoing ? 0 : 1000) + order,
                new DependencyRowViewModel(
                    outgoing ? "→" : "←",
                    edge,
                    other,
                    result,
                    brush,
                    canLocate: other is not null && !other.IsSynthetic,
                    inactiveConflict: inactiveConflict)));
        }

        foreach (var row in rows.OrderBy(r => r.Order).ThenBy(r => r.Row.TargetName, StringComparer.CurrentCultureIgnoreCase))
            SelectedDependencies.Add(row.Row);

        SelectedDependencyGroups.Clear();
        foreach (var group in SelectedDependencies
                     .GroupBy(row => new { row.GroupOrder, row.GroupTitle })
                     .OrderBy(group => group.Key.GroupOrder))
        {
            SelectedDependencyGroups.Add(new DependencyGroupViewModel(
                group.Key.GroupTitle,
                group.First().GroupBrush,
                group.OrderBy(row => row.TargetName, StringComparer.CurrentCultureIgnoreCase).ToList()));
        }

        HasDependencyRows = SelectedDependencies.Count > 0;
    }

    private static (string Text, IBrush Brush, int Order) DescribeDependency(
        DependencyEdge edge, ModNode? dependencyTarget, bool outgoing)
    {
        var order = edge.Type switch
        {
            EdgeType.Requires => 0,
            EdgeType.Incompatible => 1,
            EdgeType.Embedded => 2,
            EdgeType.Recommends => 3,
            EdgeType.Optional => 4,
            _ => 5,
        };

        if (edge.Type == EdgeType.Incompatible)
        {
            var inRange = string.IsNullOrWhiteSpace(edge.VersionRange) ||
                          (dependencyTarget is not null && !dependencyTarget.IsSynthetic &&
                           VersionRange.Satisfies(edge.VersionRange, dependencyTarget.Version) != false);
            return inRange
                ? (outgoing ? "声明冲突" : "对方声明冲突",
                    new SolidColorBrush(GraphPalette.Danger), order)
                : ("声明冲突（当前版本不在范围）", new SolidColorBrush(GraphPalette.TextMuted), order);
        }

        if (dependencyTarget is null || dependencyTarget.IsSynthetic)
        {
            return edge.Type == EdgeType.Requires
                ? ("未安装", new SolidColorBrush(GraphPalette.Danger), order)
                : ("未安装（可选）", new SolidColorBrush(GraphPalette.TextMuted), order);
        }

        var installed = dependencyTarget.DisplayVersion;
        var prefix = outgoing ? "已装" : "本机";

        if (!string.IsNullOrWhiteSpace(edge.VersionRange))
        {
            var satisfied = VersionRange.Satisfies(edge.VersionRange, dependencyTarget.Version);
            if (satisfied == false)
                return ($"版本不符（{prefix} {installed}）", new SolidColorBrush(GraphPalette.Warning), order);
        }

        return ($"{prefix} {installed}", new SolidColorBrush(GraphPalette.Success), order);
    }

    private static string BuildInitial(ModNode node)
    {
        var source = !string.IsNullOrWhiteSpace(node.NameZh)
            ? node.NameZh!
            : (string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name);
        var letter = source.TrimStart().FirstOrDefault();
        return letter == default ? "?" : char.ToUpperInvariant(letter).ToString();
    }

    private void ApplyTranslations()
    {
        if (_graph is null) return;

        foreach (var node in _graph.Nodes)
        {
            // No third-party translation index is bundled or redistributed.
            // Only user-provided overrides set a Chinese name.
            node.McmodId = null;
            node.NameZh = _translationOverrides.TryGetValue(node.Id, out var custom) &&
                          !string.IsNullOrWhiteSpace(custom)
                ? custom.Trim()
                : null;
        }
    }

    private void UpdateEdgeDetail(DependencyEdge edge)
    {
        HasSelection = true;
        IsNodeSelected = false;
        IsEdgeSelected = true;
        IsMultiSelection = false;
        CanAddRelation = false;
        CanEditManual = edge.ManualOverride && !string.IsNullOrWhiteSpace(edge.ManualLinkId);
        CanDeleteManual = CanEditManual;
        CanOpenProject = false;
        CanOpenFile = false;
        CanOpenCurseForge = false;
        CanOpenModrinth = false;
        CanOpenMcmod = false;
        CanDisableSelected = false;
        CanDeleteSelected = false;
        CanDownloadOptional = false;

        var from = _graph?.FindNode(edge.From);
        var to = _graph?.FindNode(edge.To);
        DetailTitle = $"{from?.Name ?? edge.From} → {to?.Name ?? edge.To}";
        DetailSubtitle = $"{GraphPalette.EdgeText(edge.Type)} · {edge.From} → {edge.To}";
        DetailRows.Clear();
        SelectedDependencies.Clear();
        SelectedDependencyGroups.Clear();
        HasDependencyRows = false;
        DetailIcon = null;
        HasDetailIcon = false;
        DetailInitial = "?";
        DetailDescription = string.Empty;
        HasDetailDescription = false;
        DetailRows.Add(new DetailRowViewModel("类型", GraphPalette.EdgeText(edge.Type)));
        DetailRows.Add(new DetailRowViewModel("起点", edge.From));
        DetailRows.Add(new DetailRowViewModel("终点", edge.To));
        DetailRows.Add(new DetailRowViewModel("版本范围", edge.VersionRange));
        DetailRows.Add(new DetailRowViewModel("侧", GraphPalette.SideText(edge.Side)));
        DetailRows.Add(new DetailRowViewModel("来源", SourceText(edge.Source)));
        DetailRows.Add(new DetailRowViewModel("置信度", edge.Confidence.ToString("0.00")));
        DetailRows.Add(new DetailRowViewModel("手动覆盖", edge.ManualOverride ? "是" : "否"));
        if (!string.IsNullOrWhiteSpace(edge.Note))
            DetailRows.Add(new DetailRowViewModel("备注", edge.Note));
        DetailRows.Add(new DetailRowViewModel("关系 ID", edge.Id));
    }

    private void RefreshDetailFromState()
    {
        if (_selectedEdgeId is not null)
        {
            var edge = _graph?.Edges.FirstOrDefault(e =>
                string.Equals(e.Id, _selectedEdgeId, StringComparison.OrdinalIgnoreCase));
            if (edge is not null)
            {
                _selectedNodeId = null;
                UpdateEdgeDetail(edge);
                return;
            }
        }

        if (_selectedNodeId is not null)
        {
            var node = _graph?.FindNode(_selectedNodeId);
            if (node is not null)
            {
                UpdateNodeDetail(node);
                return;
            }
        }

        ClearSelection(fromGraph: false);
    }

    // ------------------------------------------------------------------ scan / graph

    private async Task OpenPackAsync(string path, bool autoScan)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            if (File.Exists(full)) full = Path.GetDirectoryName(full) ?? full;
            if (!Directory.Exists(full))
            {
                StatusText = $"目录不存在：{full}";
                return;
            }

            PackPath = full;
            PackPathDisplay = full;
            PackName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
            _services.Settings.LastPackPath = full;
            AddRecentPack(full);
            _services.SaveSettings();

            if (autoScan) await ScanAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"无法打开目录：{ex.Message}";
        }
    }

    private async Task ScanAsync()
    {
        if (IsBusy) return;
        _filterLayoutCts?.Cancel();
        _iconFetchCts?.Cancel();
        _checkedNodeIds.Clear();
        _relationFocusId = null;
        _relationFocusIds.Clear();
        IsRelationFocus = false;
        RelationFocusTitle = string.Empty;
        UpdateCheckedState();

        var cts = BeginOperation();
        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = "准备扫描…";
        StatusText = "正在扫描 mods/*.jar…";

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                ProgressValue = p.Fraction;
                ProgressText = $"{p.Phase} {p.Processed}/{p.Total}";
                if (!string.IsNullOrWhiteSpace(p.CurrentFile))
                    StatusText = $"正在解析 {p.CurrentFile}";
            });

            var scan = await _services.Scanner.ScanAsync(PackPath, progress, cts.Token);
            _scanResult = scan;
            ModIconCache.Clear();
            _iconsPrimed = false;
            _updateStates.Clear();
            _updateCheckCache.Clear();
            HasUpdateCheck = false;
            OnlyUpdatable = false;
            PackPath = scan.RootPath;
            PackPathDisplay = scan.RootPath;
            PackName = scan.Profile.PackName ?? Path.GetFileName(scan.RootPath);
            UpdateProfileChips(scan.Profile);

            _manualLinks = await _services.ManualLinks.LoadAsync(scan.RootPath, cts.Token);
            _localRules = await _services.LocalRules.LoadAsync(scan.RootPath, cts.Token);
            _positions.Clear();
            RebuildGraph();
            await LayoutAsync(fit: true, cts.Token);

            StatusText = $"扫描完成：{scan.Jars.Count} 个 JAR · {_graph?.Nodes.Count ?? 0} 个模组 · {_graph?.Edges.Count ?? 0} 条关系";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消扫描";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            EndOperation(cts);
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }

        if (_scanResult is not null)
        {
            await RecordHistoryAsync("扫描");
            StartIconFetch();
        }
    }

    private async Task RecordHistoryAsync(string reason)
    {
        if (_scanResult is null || _graph is null) return;

        try
        {
            var file = await _services.HistoryRecorder.RecordAsync(_scanResult, _graph, reason);
            PopulateHistory(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"写入模组历史失败：{ex.Message}";
        }
    }

    private void PopulateHistory(ModHistoryFile file)
    {
        var selectedId = SelectedHistoryEntry?.Model.Id;
        HistoryEntries.Clear();
        foreach (var entry in file.Entries.OrderByDescending(entry => entry.Timestamp))
            HistoryEntries.Add(new HistoryEntryItem(entry));

        HasHistory = HistoryEntries.Count > 0;
        SelectedHistoryEntry = HistoryEntries.FirstOrDefault(entry => entry.Model.Id == selectedId) ??
                               HistoryEntries.FirstOrDefault();
    }

    partial void OnSelectedHistoryEntryChanged(HistoryEntryItem? value)
    {
        HistoryChanges.Clear();
        if (value is null) return;
        foreach (var change in value.Model.Changes)
            HistoryChanges.Add(new HistoryChangeItem(change));
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (_scanResult is null) return;
        if (!await _dialogs.ConfirmAsync("清空历史", "清空当前整合包的模组历史记录？此操作不可撤销。")) return;

        await _services.HistoryStore.ClearAsync(_scanResult.RootPath);
        HistoryEntries.Clear();
        HistoryChanges.Clear();
        HasHistory = false;
        SelectedHistoryEntry = null;
        StatusText = "已清空模组历史";
    }

    private bool HasLocalJar(ModNode node)
    {
        if (_scanResult is null || node.IsSynthetic || string.IsNullOrWhiteSpace(node.FilePath))
            return false;

        var path = Path.IsPathRooted(node.FilePath)
            ? node.FilePath
            : Path.Combine(_scanResult.RootPath, node.FilePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) && path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Installed mods that require one of the roots, transitively.</summary>
    private List<ModNode> FindOrphanDependents(IEnumerable<ModNode> roots)
    {
        var result = new List<ModNode>();
        if (_graph is null) return result;

        var seen = new HashSet<string>(roots.Select(node => node.Id), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(seen);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in _graph.Edges.Where(edge =>
                         edge.Type == EdgeType.Requires &&
                         string.Equals(edge.To, current, StringComparison.OrdinalIgnoreCase)))
            {
                var dependent = _graph.FindNode(edge.From);
                if (dependent is null || dependent.IsSynthetic || !HasLocalJar(dependent) ||
                    !seen.Add(dependent.Id))
                    continue;
                result.Add(dependent);
                queue.Enqueue(dependent.Id);
            }
        }
        return result;
    }

    [RelayCommand]
    private async Task DisableSelectedModAsync()
    {
        if (_scanResult is null || _selectedNodeId is null || _graph is null) return;
        var node = _graph.FindNode(_selectedNodeId);
        if (node is null || !HasLocalJar(node))
        {
            StatusText = "当前模组没有可禁用的本地 JAR。";
            return;
        }

        var orphans = FindOrphanDependents([node]);
        var orphanText = orphans.Count == 0
            ? string.Empty
            : $"{Environment.NewLine}{Environment.NewLine}以下模组依赖它，将一并禁用：{Environment.NewLine}" +
              string.Join(Environment.NewLine, orphans.Take(8).Select(dep => $"· {dep.Name} ({dep.Id})")) +
              (orphans.Count > 8 ? $"{Environment.NewLine}· …共 {orphans.Count} 个" : string.Empty);
        if (!await _dialogs.ConfirmAsync(
                orphans.Count > 0 ? "禁用模组及其依赖" : "禁用模组",
                $"禁用 {node.Name}（{node.Id}）？{Environment.NewLine}{Environment.NewLine}" +
                "将把 JAR 重命名为 .jar.disabled，之后可在「更多 → 管理已禁用模组」中恢复。" +
                orphanText))
            return;

        try
        {
            var disabled = 0;
            foreach (var target in new[] { node }.Concat(orphans))
            {
                if (!HasLocalJar(target)) continue;
                _services.ModFiles.Disable(target, _scanResult.RootPath);
                disabled++;
            }
            StatusText = $"已禁用 {disabled} 个模组，正在重新扫描…";
            await ScanAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = $"禁用失败：{ex.Message}";
            await _dialogs.ShowMessageAsync("禁用失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedModAsync()
    {
        if (_scanResult is null || _selectedNodeId is null || _graph is null) return;
        var node = _graph.FindNode(_selectedNodeId);
        var path = node is null ? null : _services.ModFiles.ResolveLocalFile(node, _scanResult.RootPath);
        if (node is null || path is null || !path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "当前模组没有可删除的本地 JAR。";
            return;
        }

        if (!await _dialogs.ConfirmAsync(
                "永久删除模组",
                $"永久删除 {node.Name}（{node.Id}）？{Environment.NewLine}{Environment.NewLine}" +
                "文件会直接从 mods 目录删除，无法恢复。",
                "永久删除"))
            return;

        try
        {
            File.Delete(path);
            StatusText = $"已永久删除 {Path.GetFileName(path)}，正在重新扫描…";
            await ScanAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"删除失败：{ex.Message}";
            await _dialogs.ShowMessageAsync("删除失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task OpenInstancesAsync()
    {
        var folders = _services.Settings.VersionsDirectories.Count > 0
            ? _services.Settings.VersionsDirectories.ToList()
            : string.IsNullOrWhiteSpace(_services.Settings.VersionsDirectory)
                ? []
                : [_services.Settings.VersionsDirectory!];
        var result = await _dialogs.ShowInstanceManagerAsync(folders, _services.Settings.VersionsDirectory);
        if (result is null) return;

        _services.Settings.VersionsDirectories = result.VersionsDirectories.ToList();
        if (!string.IsNullOrWhiteSpace(result.VersionsDirectory))
            _services.Settings.VersionsDirectory = result.VersionsDirectory;
        try
        {
            _services.SettingsStore.Save(_services.Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"实例目录已选择，但保存设置失败：{ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(result.InstancePath))
        {
            StatusText = "未选择实例。";
            return;
        }

        // A startup/background scan may still own the busy gate. Cancel and wait
        // before switching so the new instance is not silently ignored.
        if (IsBusy)
        {
            _operationCts?.Cancel();
            for (var attempt = 0; attempt < 50 && IsBusy; attempt++)
                await Task.Delay(100);
        }

        StatusText = $"正在打开实例：{result.InstancePath}";
        await OpenPackAsync(result.InstancePath, autoScan: true);
    }

    [RelayCommand]
    private async Task OpenDisabledModsAsync()
    {
        if (_scanResult is null)
        {
            StatusText = "请先扫描整合包。";
            return;
        }

        var files = _services.ModFiles.ListDisabled(_scanResult.RootPath);
        if (files.Count == 0)
        {
            await _dialogs.ShowMessageAsync("已禁用模组", "当前整合包没有 .jar.disabled 文件。");
            return;
        }

        var result = await _dialogs.ShowDisabledModsAsync(files);
        if (result is null || result.Items.Count == 0) return;

        if (result.Action == DisabledModsAction.Delete)
        {
            if (!await _dialogs.ConfirmAsync(
                    "永久删除已禁用模组",
                    $"永久删除 {result.Items.Count} 个文件？{Environment.NewLine}{Environment.NewLine}" +
                    "文件会直接从 mods 目录删除，无法恢复。",
                    "永久删除"))
                return;

            var deleted = 0;
            var deleteFailures = new List<string>();
            foreach (var item in result.Items)
            {
                try
                {
                    File.Delete(item.Model.FullPath);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    deleteFailures.Add($"{item.DisplayName}：{ex.Message}");
                }
            }

            StatusText = $"已永久删除 {deleted} 个禁用模组，正在重新扫描…";
            if (deleteFailures.Count > 0)
                await _dialogs.ShowMessageAsync("部分模组未删除",
                    string.Join(Environment.NewLine, deleteFailures.Take(12)));
            await ScanAsync();
            return;
        }

        var enabled = 0;
        var failures = new List<string>();
        foreach (var item in result.Items)
        {
            try
            {
                _services.ModFiles.Enable(item.Model.FullPath);
                enabled++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failures.Add($"{item.DisplayName}：{ex.Message}");
            }
        }

        StatusText = $"已启用 {enabled} 个模组，正在重新扫描…";
        if (failures.Count > 0)
            await _dialogs.ShowMessageAsync("部分模组未启用", string.Join(Environment.NewLine, failures.Take(12)));

        await ScanAsync();
    }

    [RelayCommand]
    private async Task AutoCompleteDependenciesAsync()
    {
        if (IsBusy || _scanResult is null || _graph is null) return;

        var missing = _graph.Nodes
            .Where(node => node.Status == ModStatus.Missing && !PlatformModIds.IsPlatform(node.Id))
            .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (missing.Count == 0)
        {
            await _dialogs.ShowMessageAsync("下载缺失依赖", "当前没有缺失的必需依赖。");
            return;
        }

        var preview = string.Join(Environment.NewLine, missing.Take(12).Select(node => $"· {node.Name} ({node.Id})"));
        if (!await _dialogs.ConfirmAsync(
                "下载缺失依赖",
                $"将尝试从 Modrinth / CurseForge 下载以下 {missing.Count} 个缺失依赖：" +
                $"{Environment.NewLine}{Environment.NewLine}{preview}" +
                (missing.Count > 12 ? $"{Environment.NewLine}…" : "") +
                $"{Environment.NewLine}{Environment.NewLine}只会选择与 {_scanResult.Profile.McVersion} / {_scanResult.Profile.DisplayLoader} 兼容的版本。"))
            return;

        var cts = BeginOperation();
        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = "正在补全依赖…";
        StatusText = "正在解析缺失依赖…";

        var detail = new DownloadProgressViewModel(
            "下载缺失依赖",
            $"{missing.Count} 个依赖 · {_scanResult.Profile.McVersion} · {_scanResult.Profile.DisplayLoader}");
        DownloadProgressWindow.ShowFor(detail);
        AutoCompleteResult? result = null;

        try
        {
            var progress = new Progress<DownloadProgress>(report =>
            {
                detail.Update(report);
                ProgressValue = report.Fraction;
                ProgressText = report.Total > 0
                    ? $"{report.Phase} {report.Done}/{report.Total}"
                    : report.Phase;
                if (!string.IsNullOrWhiteSpace(report.Message))
                    StatusText = $"{report.Phase} · {report.Message}";
            });

            result = await _services.AutoCompleter.CompleteAsync(
                _graph, _scanResult.Profile, _scanResult.RootPath, progress, cts.Token,
                maxConcurrency: _services.Settings.MaxParallelDownloads);
            detail.Finish(result.FailedCount == 0, $"下载 {result.DownloadedCount} 个，失败 {result.FailedCount} 个");
        }
        catch (OperationCanceledException)
        {
            detail.Finish(false, "已取消");
        }
        catch (Exception ex)
        {
            detail.Finish(false, ex.Message);
            StatusText = $"下载缺失依赖失败：{ex.Message}";
        }
        finally
        {
            EndOperation(cts);
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }

        if (result is null) return;

        var message = $"成功下载 {result.DownloadedCount} 个，失败 {result.FailedCount} 个。";
        if (result.FailedCount > 0)
            message += Environment.NewLine + Environment.NewLine +
                       string.Join(Environment.NewLine,
                           result.Items.Where(item => !item.Success)
                               .Take(10)
                               .Select(item => $"· {item.Name} ({item.ModId})：{item.Error}"));
        await _dialogs.ShowMessageAsync("下载缺失依赖完成", message);
        await ScanAsync();
    }

    private void StartIconFetch(bool forceRefresh = false)
    {
        if (_graph is null || _scanResult is null) return;

        var fingerprint = _scanResult.RootPath + "|" + string.Join(',',
            _graph.Nodes
                .OrderBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .Select(node => $"{node.Id}:{node.Sha1}:{node.FilePath}"));
        if (!forceRefresh && !_iconFetchAttempted.Add(fingerprint)) return;

        _iconFetchCts?.Cancel();
        _iconFetchCts?.Dispose();
        _iconFetchCts = new CancellationTokenSource();
        var cts = _iconFetchCts;
        _ = FetchOnlineIconsAsync(cts, forceRefresh);
    }

    [RelayCommand]
    private void RefreshOnlineIcons()
    {
        _iconFetchAttempted.Clear();
        StatusText = "正在刷新在线图标…";
        StartIconFetch(forceRefresh: true);
    }

    private async Task FetchOnlineIconsAsync(CancellationTokenSource cts, bool forceRefresh)
    {
        var nodes = _graph?.Nodes.ToList();
        if (nodes is null || nodes.Count == 0) return;

        try
        {
            var progress = new Progress<IconProgress>(report =>
            {
                if (report.Total > 0)
                    StatusText = $"正在获取在线图标 {report.Done}/{report.Total} · {report.Message}".TrimEnd(' ', '·');
                else
                    StatusText = $"正在获取在线图标：{report.Phase}";
            });

            var count = await _services.Icons.FetchIconsAsync(nodes, progress, cts.Token, forceRefresh);
            if (count <= 0 || cts.IsCancellationRequested) return;

            var selected = _selectedNodeId;
            BuildScene();
            if (!string.IsNullOrWhiteSpace(selected))
                SelectNode(selected, center: false, fromGraph: true);
            else
                RefreshDetailFromState();

            StatusText = $"已从 Modrinth / CurseForge 获取 {count} 个模组图标";
        }
        catch (OperationCanceledException)
        {
            // A newer scan/graph superseded this pass.
        }
        catch (Exception)
        {
            // Icons are optional; never interrupt analysis.
        }
        finally
        {
            if (ReferenceEquals(_iconFetchCts, cts))
            {
                _iconFetchCts = null;
                cts.Dispose();
            }
        }
    }

    private void RebuildGraph()
    {
        if (_scanResult is null) return;

        var previousIcons = _graph?.Nodes
            .Where(node => node.IconData is { Length: > 0 })
            .ToDictionary(node => node.Id, node => node.IconData!, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        var result = _services.Builder.Build(_scanResult, _manualLinks, _localRules);
        _graph = result.Graph;
        _issues = result.Issues;
        foreach (var node in _graph.Nodes)
        {
            if (node.IconData is null && previousIcons.TryGetValue(node.Id, out var icon))
                node.IconData = icon;
        }
        // Cached online icons only need one disk pass per scanned pack. Rule
        // edits rebuild the graph often; re-reading every icon on each edit
        // made rule add/delete visibly stall.
        if (!_iconsPrimed)
        {
            _services.Icons.LoadCachedIcons(_graph.Nodes);
            _iconsPrimed = true;
        }
        ApplyTranslations();
        _services.Categories.Classify(_graph);
        RefreshAnalysis();
        RefreshCategoryChips();
        EnsurePositions();
        BuildScene();
        RefreshDetailFromState();
    }

    private async Task RebuildGraphAsync()
    {
        if (_scanResult is null) return;

        var version = ++_graphRebuildVersion;
        await _graphRebuildGate.WaitAsync();
        try
        {
            var scan = _scanResult;
            var manualLinks = _manualLinks.ToList();
            var localRules = _localRules.ToList();
            var previousIcons = _graph?.Nodes
                .Where(node => node.IconData is { Length: > 0 })
                .ToDictionary(node => node.Id, node => node.IconData!, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            var result = await Task.Run(
                () => _services.Builder.Build(scan, manualLinks, localRules)).ConfigureAwait(true);
            if (version != _graphRebuildVersion || !ReferenceEquals(scan, _scanResult)) return;

            _graph = result.Graph;
            _issues = result.Issues;
            foreach (var node in _graph.Nodes)
                if (node.IconData is null && previousIcons.TryGetValue(node.Id, out var icon))
                    node.IconData = icon;

            if (!_iconsPrimed)
            {
                _services.Icons.LoadCachedIcons(_graph.Nodes);
                _iconsPrimed = true;
            }

            ApplyTranslations();
            _services.Categories.Classify(_graph);
            RefreshAnalysis();
            RefreshCategoryChips();
            EnsurePositions();
            BuildScene();
            RefreshDetailFromState();
        }
        finally
        {
            _graphRebuildGate.Release();
        }
    }

    private async Task LayoutVisibleAsync(bool fit, CancellationToken cancellationToken)
    {
        if (_graph is null) return;

        var visibleIds = Scene?.Nodes
            .Select(node => node.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        if (visibleIds.Count == 0 || visibleIds.Count == _graph.Nodes.Count)
        {
            await LayoutAsync(fit, cancellationToken);
            return;
        }

        var subgraph = new GraphDocument
        {
            Nodes = _graph.Nodes.Where(node => visibleIds.Contains(node.Id)).ToList(),
            Edges = _graph.Edges
                .Where(edge => visibleIds.Contains(edge.From) && visibleIds.Contains(edge.To))
                .ToList(),
        };
        var algorithm = _services.Settings.LayoutAlgorithm;
        var positions = await Task.Run(
            () => _services.Layout.Compute(subgraph, new LayoutOptions { Algorithm = algorithm },
                cancellationToken),
            cancellationToken);

        var merged = new Dictionary<string, LayoutPoint>(_positions, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, position) in positions)
            merged[id] = position;
        _positions = merged;
        BuildScene();

        if (fit)
            _graphView?.FitToView();
        else
            _graphView?.InvalidateVisual();
    }

    private async Task LayoutAsync(bool fit, CancellationToken cancellationToken)
    {
        if (_graph is null) return;

        var algorithm = _services.Settings.LayoutAlgorithm;
        var graph = _graph;
        var options = new LayoutOptions { Algorithm = algorithm };

        var positions = await Task.Run(
            () => _services.Layout.Compute(graph, options, cancellationToken),
            cancellationToken);

        _positions = new Dictionary<string, LayoutPoint>(positions, StringComparer.OrdinalIgnoreCase);
        BuildScene();

        if (fit)
            _graphView?.FitToView();
        else
            _graphView?.InvalidateVisual();
    }

    private void EnsurePositions()
    {
        if (_graph is null) return;

        var fallbackIndex = 0;
        foreach (var node in _graph.Nodes)
        {
            if (_positions.ContainsKey(node.Id)) continue;

            var anchors = _graph.Edges
                .Where(e => string.Equals(e.From, node.Id, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(e.To, node.Id, StringComparison.OrdinalIgnoreCase))
                .Select(e => string.Equals(e.From, node.Id, StringComparison.OrdinalIgnoreCase) ? e.To : e.From)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(_positions.ContainsKey)
                .Select(id => _positions[id])
                .ToList();

            LayoutPoint point;
            if (anchors.Count > 0)
            {
                var average = new LayoutPoint(anchors.Average(p => p.X), anchors.Average(p => p.Y));
                point = new LayoutPoint(average.X + 260, average.Y + 90 + fallbackIndex % 4 * 40);
            }
            else
            {
                point = new LayoutPoint(140 + fallbackIndex % 6 * 250, 140 + fallbackIndex / 6 * 150);
            }

            _positions[node.Id] = point;
            fallbackIndex++;
        }

        // Remove stale positions.
        var existing = _graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _positions.Keys.Where(id => !existing.Contains(id)).ToList())
            _positions.Remove(id);
    }

    private void BuildScene()
    {
        if (_graph is null)
        {
            Scene = new GraphScene();
            HasGraph = false;
            NodeItems.Clear();
            OptionalNodeItems.Clear();
            HasOptionalNodeItems = false;
            CountSummary = "0 个模组 · 0 条关系";
            return;
        }

        var term = SearchText.Trim();
        var byId = _graph.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var edgeTypeVisible = EdgeTypeVisible();
        var loaderVisible = LoaderVisible();

        bool NodePassesFilter(ModNode node) =>
            loaderVisible(node.Loader) &&
            (ShowMissing || node.Status != ModStatus.Missing) &&
            (!node.IsSynthetic || node.IsOptionalDependency || node.IsRecommendedDependency ||
             node.Status == ModStatus.Missing) &&
            !node.IsEmbedded &&
            (!node.IsOptionalDependency || ShowUninstalledOptional) &&
            (!node.IsRecommendedDependency || ShowUninstalledOptional) &&
            (!IsRelationFocus || _relationFocusIds.Contains(node.Id)) &&
            MatchesStatusFilter(node.Status) &&
            MatchesSideFilter(node.Side) &&
            MatchesCategoryFilter(node.Category) &&
            (!OnlyProblems || node.Status is ModStatus.Missing or ModStatus.Conflict or ModStatus.VersionMismatch);

        bool EdgePassesFilter(DependencyEdge edge)
        {
            if (edge.Type == EdgeType.Manual || edge.ManualOverride)
            {
                if (!ShowManual) return false;
            }
            else if (!edgeTypeVisible(edge.Type))
            {
                return false;
            }

            return byId.TryGetValue(edge.From, out var from) &&
                   byId.TryGetValue(edge.To, out var to) &&
                   NodePassesFilter(from) &&
                   NodePassesFilter(to);
        }

        var candidateEdges = _graph.Edges.Where(EdgePassesFilter).ToList();
        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in candidateEdges)
        {
            connected.Add(edge.From);
            connected.Add(edge.To);
        }

        var visibleNodes = _graph.Nodes
            .Where(NodePassesFilter)
            .Where(n => !HideIsolated || connected.Contains(n.Id))
            .ToList();

        var visibleIds = visibleNodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        candidateEdges = candidateEdges
            .Where(e => visibleIds.Contains(e.From) && visibleIds.Contains(e.To))
            .ToList();

        var embeddedCounts = _graph.Edges
            .Where(edge => edge.Type == EdgeType.Embedded)
            .GroupBy(edge => edge.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var nodeVms = new List<GraphNodeVm>(visibleNodes.Count);
        var fallbackIndex = 0;
        foreach (var node in visibleNodes)
        {
            var hasPosition = _positions.TryGetValue(node.Id, out var position);
            if (!hasPosition)
            {
                position = new LayoutPoint(140 + fallbackIndex % 6 * 250, 140 + fallbackIndex / 6 * 150);
                fallbackIndex++;
                _positions[node.Id] = position;
            }

            nodeVms.Add(new GraphNodeVm(node, position.X, position.Y)
            {
                IsSearchMatch = MatchesSearch(node, term),
                EmbeddedCount = embeddedCounts.GetValueOrDefault(node.Id),
            });
        }

        nodeVms.Sort(CompareNodes);
        var nodeById = nodeVms.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        var edgeVms = new List<GraphEdgeVm>(candidateEdges.Count);
        var pairCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in candidateEdges.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal))
        {
            if (!nodeById.TryGetValue(edge.From, out var from) || !nodeById.TryGetValue(edge.To, out var to))
                continue;

            var key = edge.From + "\u0000" + edge.To;
            var parallel = pairCounts.GetValueOrDefault(key);
            pairCounts[key] = parallel + 1;

            edgeVms.Add(new GraphEdgeVm(edge, from, to, parallel)
            {
                IsSearchMatch = term.Length == 0 || from.IsSearchMatch || to.IsSearchMatch,
            });
        }

        var hubDegrees = edgeVms
            .GroupBy(edge => edge.To.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodeVms)
            node.HubInDegree = hubDegrees.GetValueOrDefault(node.Id);

        Scene = new GraphScene
        {
            Nodes = nodeVms,
            Edges = edgeVms,
            SearchTerm = term,
            ShowLabels = ShowLabels,
            FocusEdgesOnly = FocusEdgesOnly,
        };
        HasGraph = nodeVms.Count > 0;
        var optionalVisible = nodeVms.Count(node =>
            node.Model.IsOptionalDependency || node.Model.IsRecommendedDependency);
        CountSummary = optionalVisible > 0
            ? $"已安装 {nodeVms.Count - optionalVisible} · 可选 {optionalVisible} / {_graph.Nodes.Count} 个模组 · {edgeVms.Count} / {_graph.Edges.Count} 条关系"
            : $"显示 {nodeVms.Count} / {_graph.Nodes.Count} 个模组 · {edgeVms.Count} / {_graph.Edges.Count} 条关系";

        RebuildNodeList(nodeVms, term);
    }

    private void RebuildNodeList(IReadOnlyList<GraphNodeVm> visibleNodes, string term)
    {
        var selectedId = SelectedNodeItem?.Id;
        var matching = visibleNodes
            .Where(n => term.Length == 0 || n.IsSearchMatch)
            .Where(n => !OnlyUpdatable || (_updateStates.TryGetValue(n.Id, out var update) && update.Available))
            .ToList();

        matching = SelectedSortOption.Value switch
        {
            ModSortMode.Name => matching
                .OrderBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            ModSortMode.Version => matching
                .OrderByDescending(n => n.Model.Version, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            ModSortMode.Dependencies => matching
                .OrderByDescending(n => _graph?.Edges.Count(edge =>
                    string.Equals(edge.From, n.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(edge.To, n.Id, StringComparison.OrdinalIgnoreCase)) ?? 0)
                .ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            ModSortMode.Category => matching
                .OrderBy(n => ModCategoryText.Name(n.Model.Category), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            ModSortMode.Loader => matching
                .OrderBy(n => GraphPalette.LoaderText(n.Model.Loader), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            ModSortMode.Time => matching
                .OrderByDescending(n => n.Model.FileModifiedAt ?? DateTimeOffset.MinValue)
                .ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            _ => matching
                .OrderBy(n => n.Model.Status is ModStatus.Ok or ModStatus.Unknown ? 1 : 0)
                .ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
        };

        if (SortDescending)
            matching = Enumerable.Reverse(matching).ToList();

        var embeddedByParent = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (_graph is not null)
        {
            foreach (var edge in _graph.Edges.Where(edge => edge.Type == EdgeType.Embedded))
            {
                if (!embeddedByParent.TryGetValue(edge.From, out var children))
                    embeddedByParent[edge.From] = children = [];
                if (!children.Contains(edge.To, StringComparer.OrdinalIgnoreCase))
                    children.Add(edge.To);
            }
        }

        var uninstalled = matching
            .Where(node => node.Model.IsSynthetic)
            .ToList();
        var optional = ShowUninstalledOptional
            ? uninstalled
                .Where(node => node.Model.IsOptionalDependency || node.Model.IsRecommendedDependency)
                .OrderBy(node => node.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList()
            : [];
        var missing = ShowMissing
            ? uninstalled
                .Where(node => node.Model.Status == ModStatus.Missing &&
                               !node.Model.IsOptionalDependency &&
                               !node.Model.IsRecommendedDependency)
                .OrderBy(node => node.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList()
            : [];
        matching = matching
            .Where(node => !node.Model.IsSynthetic)
            .ToList();

        var parentedEmbedded = embeddedByParent.Values
            .SelectMany(ids => ids)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        matching = matching
            .Where(node => !node.Model.IsEmbedded || !parentedEmbedded.Contains(node.Id))
            .ToList();

        var actionableIssueCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var issue in _issues)
        {
            if (issue.Severity is not (IssueSeverity.Warning or IssueSeverity.Error)) continue;
            var ids = new[] { issue.NodeId, issue.RelatedNodeId }
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
                actionableIssueCounts[id] = actionableIssueCounts.GetValueOrDefault(id) + 1;
        }
        int IssueCountFor(string id) => actionableIssueCounts.GetValueOrDefault(id);

        _suppressSelectionSync = true;
        _suppressCheckedSync = true;
        NodeItems.Clear();
        OptionalNodeItems.Clear();
        MissingNodeItems.Clear();
        var index = 0;
        foreach (var node in matching)
        {
            var issueCount = IssueCountFor(node.Id);
            _updateStates.TryGetValue(node.Id, out var updateState);
            var item = new NodeItemViewModel(node.Model, _graph, ++index, issueCount,
                updateState?.Available == true, updateState?.LatestVersion)
            {
                IsChecked = _checkedNodeIds.Contains(node.Id),
            };
            item.PropertyChanged += OnNodeItemPropertyChanged;

            if (embeddedByParent.TryGetValue(node.Id, out var childIds))
            {
                var children = childIds
                    .Select(id => _graph?.FindNode(id))
                    .Where(child => child is not null)
                    .Select(child => new NodeItemViewModel(child!, _graph, 0, IssueCountFor(child!.Id)))
                    .ToList();
                item.SetEmbeddedChildren(children,
                    _expandedEmbeddedParents.Contains(node.Id) || ShowEmbedded);
            }

            NodeItems.Add(item);
        }

        foreach (var node in missing)
            MissingNodeItems.Add(new NodeItemViewModel(node.Model, _graph, 0, IssueCountFor(node.Id)));
        foreach (var node in optional)
            OptionalNodeItems.Add(new NodeItemViewModel(node.Model, _graph, 0, IssueCountFor(node.Id)));

        HasMissingNodeItems = MissingNodeItems.Count > 0;
        HasOptionalNodeItems = OptionalNodeItems.Count > 0;
        OnPropertyChanged(nameof(ShowMissingSection));
        OnPropertyChanged(nameof(MissingToggleText));
        OnPropertyChanged(nameof(ShowOptionalSection));
        var visibleIds = NodeItems.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _checkedNodeIds.RemoveWhere(id => !visibleIds.Contains(id));
        _suppressCheckedSync = false;
        UpdateCheckedState();

        SelectedNodeItem = selectedId is null
            ? null
            : NodeItems.FirstOrDefault(n => string.Equals(n.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        _suppressSelectionSync = false;
    }

    private static int CompareNodes(GraphNodeVm left, GraphNodeVm right)
    {
        var leftRank = left.Model.Status is ModStatus.Ok or ModStatus.Unknown ? 1 : 0;
        var rightRank = right.Model.Status is ModStatus.Ok or ModStatus.Unknown ? 1 : 0;
        var rank = leftRank.CompareTo(rightRank);
        return rank != 0 ? rank : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
    }

    private bool MatchesSearch(ModNode node, string term)
    {
        if (term.Length == 0) return true;

        foreach (var token in term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var exclude = token.Length > 1 && token[0] is '-' or '!';
            var needle = exclude ? token[1..] : token;
            if (needle.Length == 0) continue;
            var hit = NodeTextContains(node, needle);
            if (exclude ? hit : !hit) return false;
        }

        return true;
    }

    private static bool NodeTextContains(ModNode node, string needle) =>
        node.Id.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        (node.Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (node.NameZh?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (node.Slug?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (node.ProjectTitle?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
        GraphPalette.StatusText(node.Status).Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        GraphPalette.LoaderText(node.Loader).Contains(needle, StringComparison.OrdinalIgnoreCase);

    private bool MatchesSideFilter(ModSide side) => SelectedSideFilter.Value switch
    {
        ModSideFilter.Both => side == ModSide.Both,
        ModSideFilter.Client => side == ModSide.Client,
        ModSideFilter.Server => side == ModSide.Server,
        ModSideFilter.Unknown => side == ModSide.Unknown,
        _ => true,
    };

    private bool MatchesStatusFilter(ModStatus status) => SelectedStatusFilter.Value switch
    {
        ModStatusFilter.Problems => status is ModStatus.Missing or ModStatus.Conflict or ModStatus.VersionMismatch,
        ModStatusFilter.Missing => status == ModStatus.Missing,
        ModStatusFilter.Conflict => status == ModStatus.Conflict,
        ModStatusFilter.VersionMismatch => status == ModStatus.VersionMismatch,
        ModStatusFilter.Ok => status == ModStatus.Ok,
        _ => true,
    };

    private bool MatchesCategoryFilter(ModCategory category) =>
        SelectedCategoryFilter.Value is null || SelectedCategoryFilter.Value == category;

    private Func<LoaderKind, bool> LoaderVisible() => loader => loader switch
    {
        LoaderKind.Fabric => ShowFabric,
        LoaderKind.Quilt => ShowQuilt,
        LoaderKind.Forge => ShowForge,
        LoaderKind.NeoForge => ShowNeoForge,
        _ => ShowUnknown,
    };

    private bool IsRelationEdgeVisible(EdgeType type) => type switch
    {
        EdgeType.Requires => ShowRequires,
        EdgeType.Incompatible => ShowIncompatible,
        EdgeType.Optional => ShowOptional,
        EdgeType.Recommends => ShowRecommends,
        EdgeType.Embedded => ShowEmbedded,
        EdgeType.Manual => ShowManual,
        _ => true,
    };

    private Func<EdgeType, bool> EdgeTypeVisible() => type => type switch
    {
        EdgeType.Requires => ShowRequires,
        EdgeType.Incompatible => ShowIncompatible,
        EdgeType.Optional => ShowOptional,
        EdgeType.Embedded => ShowEmbedded,
        EdgeType.Recommends => ShowRecommends,
        EdgeType.Manual => ShowManual,
        _ => true,
    };

    private void RefreshAnalysis()
    {
        if (_graph is null) return;

        MissingCount = _graph.Nodes.Count(n => n.Status == ModStatus.Missing);
        HasMissingDependencies = _graph.Nodes.Any(n =>
            n.Status == ModStatus.Missing && !PlatformModIds.IsPlatform(n.Id));
        MismatchCount = _graph.Nodes.Count(n => n.Status == ModStatus.VersionMismatch);
        ConflictCount = _graph.Nodes.Count(n => n.Status == ModStatus.Conflict);
        OnPropertyChanged(nameof(ProblemSummary));
        OnPropertyChanged(nameof(ShowLoaderFilters));
        OnPropertyChanged(nameof(ShowFabricFilter));
        OnPropertyChanged(nameof(ShowQuiltFilter));
        OnPropertyChanged(nameof(ShowForgeFilter));
        OnPropertyChanged(nameof(ShowNeoForgeFilter));
        OnPropertyChanged(nameof(ShowUnknownFilter));
        _suppressSelectionSync = true;
        AvailableModIds.Clear();
        foreach (var id in _graph.Nodes.Select(n => n.Id).OrderBy(id => id, StringComparer.Ordinal))
            AvailableModIds.Add(id);

        RefreshIssueView();

        ManualLinks.Clear();
        foreach (var link in _manualLinks
                     .OrderBy(l => l.From, StringComparer.Ordinal)
                     .ThenBy(l => l.To, StringComparer.Ordinal))
            ManualLinks.Add(new ManualLinkItemViewModel(link));

        RefreshLocalRules();

        SelectedNodeItem = null;
        SelectedManualLink = null;
        SelectedIssue = null;
        _suppressSelectionSync = false;
    }

    private bool IssueVisibleForFilters(AnalysisIssue issue)
    {
        if (_graph is null || string.IsNullOrWhiteSpace(issue.EdgeId)) return true;
        var edge = _graph.Edges.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, issue.EdgeId, StringComparison.OrdinalIgnoreCase));
        if (edge is null) return true;
        return edge.Type switch
        {
            EdgeType.Optional => ShowOptional,
            EdgeType.Recommends => ShowRecommends,
            EdgeType.Embedded => ShowEmbedded,
            EdgeType.Manual => ShowManual,
            _ => true,
        };
    }

    private void RefreshIssueView()
    {
        var visibleIssues = _issues.Where(IssueVisibleForFilters).ToList();
        ErrorCount = visibleIssues.Count(issue => issue.Severity == IssueSeverity.Error);
        WarningCount = visibleIssues.Count(issue => issue.Severity == IssueSeverity.Warning);
        InfoCount = visibleIssues.Count(issue => issue.Severity == IssueSeverity.Info);
        OnPropertyChanged(nameof(IssueSummary));
        OnPropertyChanged(nameof(ProblemSummary));

        var selected = SelectedIssue?.Model;
        Issues.Clear();
        foreach (var issue in visibleIssues
                     .OrderByDescending(i => i.Severity)
                     .ThenBy(i => i.Kind)
                     .ThenBy(i => i.Title, StringComparer.Ordinal))
            Issues.Add(new IssueItemViewModel(issue));

        ReportIssues.Clear();
        foreach (var issue in Issues.Where(item => ShowOptionalInReport ||
                                                   item.Model.Kind != IssueKind.OptionalMissing))
            ReportIssues.Add(issue);

        SelectedIssue = selected is null
            ? null
            : Issues.FirstOrDefault(item => Signature(item.Model) == Signature(selected));
    }

    private void RefreshLocalRules()
    {
        var selectedId = SelectedLocalRule?.Model.Id;
        _suppressSelectionSync = true;
        LocalRules.Clear();
        foreach (var rule in _localRules
                     .OrderByDescending(rule => rule.Priority)
                     .ThenBy(rule => rule.Type)
                     .ThenBy(rule => rule.From, StringComparer.Ordinal))
        {
            LocalRules.Add(new LocalRuleItemViewModel(rule, OnLocalRuleToggled));
        }

        EnabledRuleCount = _localRules.Count(rule => rule.Enabled);
        RulesSummary = $"本地规则 {_localRules.Count} 条 · 启用 {EnabledRuleCount} 条";
        SelectedLocalRule = selectedId is null
            ? null
            : LocalRules.FirstOrDefault(item =>
                string.Equals(item.Model.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        _suppressSelectionSync = false;
    }

    private void MergeIssues(IEnumerable<AnalysisIssue> additional)
    {
        var known = _issues
            .Select(Signature)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var issue in additional)
        {
            if (known.Add(Signature(issue)))
                _issues.Add(issue);
        }
    }

    private static string Signature(AnalysisIssue issue) =>
        $"{issue.Kind}|{issue.NodeId}|{issue.EdgeId}|{issue.Message}";

    private async Task PersistManualLinksAsync()
    {
        if (_scanResult is null) return;
        try
        {
            await _services.ManualLinks.SaveAsync(_scanResult.RootPath, _manualLinks);
        }
        catch (Exception ex)
        {
            StatusText = $"保存手动关系失败：{ex.Message}";
        }
    }

    private async Task SaveManualLinkAsync(ManualLink link)
    {
        if (_scanResult is null)
        {
            StatusText = "请先扫描整合包";
            return;
        }

        link.From = link.From.Trim().ToLowerInvariant();
        link.To = link.To.Trim().ToLowerInvariant();
        if (link.From.Length == 0 || link.To.Length == 0 || link.From == link.To)
        {
            StatusText = "手动关系无效：起点与终点不能为空或相同";
            return;
        }

        if (string.IsNullOrWhiteSpace(link.Id)) link.Id = Guid.NewGuid().ToString("N");
        link.UpdatedAt = DateTimeOffset.UtcNow;

        _manualLinks.RemoveAll(existing =>
            existing.Id == link.Id ||
            (string.Equals(existing.From, link.From, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(existing.To, link.To, StringComparison.OrdinalIgnoreCase)));
        _manualLinks.Add(link);

        try
        {
            await _services.ManualLinks.SaveAsync(_scanResult.RootPath, _manualLinks);
        }
        catch (Exception ex)
        {
            StatusText = $"保存手动关系失败：{ex.Message}";
            return;
        }

        RebuildGraph();
        SelectEdge($"manual:{link.Id}", center: false, fromGraph: false);
        StatusText = $"已保存手动关系：{link.From} → {link.To}";
    }

    private ManualLink? GetSelectedManualLink()
    {
        if (SelectedManualLink is not null) return SelectedManualLink.Model;
        if (_selectedEdgeId is null) return null;

        var edge = _graph?.Edges.FirstOrDefault(e => string.Equals(e.Id, _selectedEdgeId, StringComparison.OrdinalIgnoreCase));
        if (edge?.ManualLinkId is null) return null;
        return _manualLinks.FirstOrDefault(l => string.Equals(l.Id, edge.ManualLinkId, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ export

    private async Task ExportAsync(ExportFormat format)
    {
        if (_graph is null)
        {
            StatusText = "请先扫描整合包";
            return;
        }

        var baseName = SanitizeFileName(string.IsNullOrWhiteSpace(PackName) ? "pack" : PackName);
        var (extension, label, suffix) = format switch
        {
            ExportFormat.Json => ("json", "JSON 图数据", "-dependencies"),
            ExportFormat.Dot => ("dot", "Graphviz DOT", "-dependencies"),
            ExportFormat.GraphMl => ("graphml", "GraphML", "-dependencies"),
            ExportFormat.Csv => ("csv", "CSV 表格", "-dependencies"),
            ExportFormat.MarkdownReport => ("md", "Markdown 报告", "-report"),
            _ => ("json", "JSON", "-dependencies"),
        };

        var path = await _dialogs.PickSaveFileAsync(
            "导出", $"{baseName}{suffix}.{extension}", extension, label);
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var written = await GraphExporter.ExportAsync(
                _graph, _issues, path, format, _scanResult?.Profile, _localRules);
            StatusText = format == ExportFormat.Csv
                ? $"已导出节点与关系 CSV：{written.Replace(Environment.NewLine, "，")}"
                : $"已导出：{written}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "pack" : cleaned;
    }

    // ------------------------------------------------------------------ external actions

    private static bool HasProjectLink(ModNode node) =>
        !string.IsNullOrWhiteSpace(node.ProjectIds.Modrinth) ||
        !string.IsNullOrWhiteSpace(node.ProjectIds.CurseForge);

    private void OpenProjectForNode(string nodeId)
    {
        var node = _graph?.FindNode(nodeId);
        if (node is null) return;

        string? url = null;
        if (!string.IsNullOrWhiteSpace(node.ProjectIds.Modrinth))
        {
            var slug = string.IsNullOrWhiteSpace(node.Slug) ? node.Id : node.Slug;
            url = $"https://modrinth.com/mod/{Uri.EscapeDataString(slug)}";
        }
        else if (!string.IsNullOrWhiteSpace(node.ProjectIds.CurseForge))
        {
            var slug = string.IsNullOrWhiteSpace(node.Slug) ? node.Id : node.Slug;
            url = $"https://www.curseforge.com/minecraft/mc-mods/{Uri.EscapeDataString(slug)}";
        }

        if (url is null)
        {
            StatusText = "该项目没有可打开的 CurseForge / Modrinth 页面";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            StatusText = $"已打开：{url}";
        }
        catch (Exception ex)
        {
            StatusText = $"无法打开浏览器：{ex.Message}";
        }
    }

    public void OpenCurseForgeForNode(string nodeId)
    {
        var node = _graph?.FindNode(nodeId);
        if (node is null) return;

        if (!string.IsNullOrWhiteSpace(node.Slug))
        {
            OpenUrl($"https://www.curseforge.com/minecraft/mc-mods/{Uri.EscapeDataString(node.Slug)}");
            return;
        }

        var query = GetSiteSearchQuery(node);
        OpenUrl($"https://www.curseforge.com/minecraft/search?page=1&pageSize=20&sortBy=relevancy&class=mc-mods&search={Uri.EscapeDataString(query)}");
    }

    public void OpenModrinthForNode(string nodeId)
    {
        var node = _graph?.FindNode(nodeId);
        if (node is null) return;

        var slug = !string.IsNullOrWhiteSpace(node.Slug)
            ? node.Slug
            : node.ProjectIds.Modrinth;
        if (!string.IsNullOrWhiteSpace(slug))
        {
            OpenUrl($"https://modrinth.com/mod/{Uri.EscapeDataString(slug)}");
            return;
        }

        var query = GetSiteSearchQuery(node);
        OpenUrl($"https://modrinth.com/mods?q={Uri.EscapeDataString(query)}");
    }

    private static string GetSiteSearchQuery(ModNode node) =>
        !string.IsNullOrWhiteSpace(node.Name)
            ? node.Name
            : (!string.IsNullOrWhiteSpace(node.NameZh) ? node.NameZh! : node.Id);

    public void OpenMcmodForNode(string nodeId)
    {
        var node = _graph?.FindNode(nodeId);
        if (node is null) return;

        if (node.McmodId is > 0)
        {
            OpenUrl($"https://www.mcmod.cn/class/{node.McmodId}.html");
            return;
        }

        var query = !string.IsNullOrWhiteSpace(node.NameZh)
            ? node.NameZh
            : (!string.IsNullOrWhiteSpace(node.Name) ? node.Name : node.Id);
        OpenUrl($"https://www.mcmod.cn/s?key={Uri.EscapeDataString(query)}");
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            StatusText = $"已打开：{url}";
        }
        catch (Exception ex)
        {
            StatusText = $"无法打开浏览器：{ex.Message}";
        }
    }

    private void OpenFileForNode(string nodeId)
    {
        var node = _graph?.FindNode(nodeId);
        if (node is null || string.IsNullOrWhiteSpace(node.FilePath))
        {
            StatusText = "该节点没有本地 JAR 文件";
            return;
        }

        try
        {
            var root = Path.GetFullPath(_scanResult?.RootPath ?? PackPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, node.FilePath.Replace('/', Path.DirectorySeparatorChar)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(root, comparison) || !File.Exists(full))
            {
                StatusText = "找不到该模组的 JAR 文件";
                return;
            }

            var arguments = OperatingSystem.IsWindows() ? $"/select,\"{full}\"" : $"\"{Path.GetDirectoryName(full)}\"";
            var executable = OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open";
            Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"无法定位文件：{ex.Message}";
        }
    }

    private async Task CopyIdAsync(string nodeId)
    {
        if (_window?.Clipboard is null)
        {
            StatusText = "剪贴板不可用";
            return;
        }

        await _window.Clipboard.SetTextAsync(nodeId);
        StatusText = $"已复制 modId：{nodeId}";
    }

    // ------------------------------------------------------------------ helpers

    private CancellationTokenSource BeginOperation()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        return _operationCts;
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_operationCts, cts))
        {
            _operationCts = null;
            cts.Dispose();
        }
    }

    private void RefreshRecentPacks()
    {
        _suppressRecent = true;
        RecentPacks.Clear();
        foreach (var path in _services.Settings.RecentPacks.Where(Directory.Exists).Take(10))
            RecentPacks.Add(path);
        SelectedRecentPack = null;
        _suppressRecent = false;
    }

    private void AddRecentPack(string path)
    {
        var list = _services.Settings.RecentPacks;
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > 10) list.RemoveRange(10, list.Count - 10);
        RefreshRecentPacks();
    }

    private void UpdateProfileChips(PackProfile profile)
    {
        var loader = string.IsNullOrWhiteSpace(profile.DisplayLoader) ? "unknown" : profile.DisplayLoader;
        ProfileSummary = $"{loader} · Minecraft {profile.McVersion}" + (profile.IsInferred ? "（推断）" : string.Empty);
        PackName = profile.PackName ?? PackName;
    }

    private void ApplyTheme()
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant =
            string.Equals(_services.Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        _graphView?.RefreshTheme();
    }

    private static string SourceText(EdgeSource source) => source switch
    {
        EdgeSource.Local => "本地元数据",
        EdgeSource.CurseForge => "CurseForge",
        EdgeSource.Modrinth => "Modrinth",
        EdgeSource.Manual => "手动",
        _ => source.ToString(),
    };

    // ------------------------------------------------------------------ filter notifications

    partial void OnPackPathChanged(string value) => OnPropertyChanged(nameof(IsPackLoaded));

    partial void OnSearchTextChanged(string value) => RefreshView();
    partial void OnShowRequiresChanged(bool value) => RefreshView();
    partial void OnShowIncompatibleChanged(bool value) => RefreshView();
    partial void OnShowOptionalChanged(bool value)
    {
        RefreshIssueView();
        RefreshView();
        RefreshCategoryChips();
    }
    partial void OnShowEmbeddedChanged(bool value)
    {
        if (!value) _expandedEmbeddedParents.Clear();
        RefreshIssueView();
        RefreshView();
        RefreshCategoryChips();
    }
    partial void OnShowRecommendsChanged(bool value)
    {
        RefreshIssueView();
        RefreshView();
        RefreshCategoryChips();
    }
    partial void OnShowManualChanged(bool value) => RefreshView();
    partial void OnShowFabricChanged(bool value) => RefreshView();
    partial void OnShowQuiltChanged(bool value) => RefreshView();
    partial void OnShowForgeChanged(bool value) => RefreshView();
    partial void OnShowNeoForgeChanged(bool value) => RefreshView();
    partial void OnShowUnknownChanged(bool value) => RefreshView();
    partial void OnOnlyProblemsChanged(bool value) => RefreshView();
    partial void OnHideIsolatedChanged(bool value) => RefreshView();
    partial void OnShowLabelsChanged(bool value) => RefreshView();

    partial void OnShowOptionalInReportChanged(bool value) => RefreshIssueView();

    partial void OnShowMissingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMissingSection));
        RefreshView();
    }

    partial void OnIsBasicInfoExpandedChanged(bool value) =>
        OnPropertyChanged(nameof(BasicInfoToggleText));

    partial void OnIsDescriptionExpandedChanged(bool value) =>
        OnPropertyChanged(nameof(DescriptionToggleText));

    partial void OnIsDependenciesExpandedChanged(bool value) =>
        OnPropertyChanged(nameof(DependenciesToggleText));

    partial void OnShowUninstalledOptionalChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOptionalSection));
        RefreshView();
    }

    partial void OnFocusEdgesOnlyChanged(bool value)
    {
        if (Scene is not null) Scene.FocusEdgesOnly = value;
        _graphView?.InvalidateVisual();
    }
    partial void OnIsOfflineChanged(bool value) => OnPropertyChanged(nameof(NetworkStatusText));

    private IReadOnlyList<ModNode> CheckedNodes() =>
        NodeItems.Where(item => item.IsChecked).Select(item => item.Model).ToList();

    private void OnNodeItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not NodeItemViewModel item) return;

        if (e.PropertyName == nameof(NodeItemViewModel.IsEmbeddedExpanded))
        {
            if (item.IsEmbeddedExpanded) _expandedEmbeddedParents.Add(item.Id);
            else _expandedEmbeddedParents.Remove(item.Id);
            return;
        }

        if (_suppressCheckedSync || e.PropertyName != nameof(NodeItemViewModel.IsChecked)) return;
        if (item.IsChecked) _checkedNodeIds.Add(item.Id);
        else _checkedNodeIds.Remove(item.Id);
        UpdateCheckedState();
    }

    private void UpdateCheckedState()
    {
        CheckedCount = _checkedNodeIds.Count;
        HasChecked = CheckedCount > 0;

        var all = NodeItems.Count > 0 && NodeItems.All(item => item.IsChecked);
        if (AllVisibleChecked == all) return;

        _suppressCheckedSync = true;
        AllVisibleChecked = all;
        _suppressCheckedSync = false;
    }

    partial void OnAllVisibleCheckedChanged(bool value)
    {
        if (_suppressCheckedSync) return;

        _suppressCheckedSync = true;
        foreach (var item in NodeItems)
            item.IsChecked = value;
        _checkedNodeIds.Clear();
        if (value)
        {
            foreach (var item in NodeItems)
                _checkedNodeIds.Add(item.Id);
        }
        _suppressCheckedSync = false;
        UpdateCheckedState();
    }

    partial void OnSelectedStatusFilterChanged(StatusFilterOption value) => RefreshView();
    partial void OnSelectedSideFilterChanged(SideFilterOption value) => RefreshView();
    partial void OnIsMissingSectionExpandedChanged(bool value) =>
        OnPropertyChanged(nameof(MissingToggleText));

    [RelayCommand]
    private void ToggleMissingSection() => IsMissingSectionExpanded = !IsMissingSectionExpanded;

    [RelayCommand]
    private void ToggleBasicInfo() => IsBasicInfoExpanded = !IsBasicInfoExpanded;

    [RelayCommand]
    private void ToggleDescription() => IsDescriptionExpanded = !IsDescriptionExpanded;

    [RelayCommand]
    private void ToggleDependencies() => IsDependenciesExpanded = !IsDependenciesExpanded;

    partial void OnSelectedSortOptionChanged(ModSortOption value) => RefreshView();
    partial void OnSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortDirectionText));
        OnPropertyChanged(nameof(SortDirectionTooltip));
        RefreshView();
    }

    partial void OnOnlyUpdatableChanged(bool value)
    {
        if (value && !HasUpdateCheck)
            StatusText = "请先点击「检查更新」，再筛选可更新模组";
        RefreshView();
    }

    partial void OnSelectedCategoryFilterChanged(CategoryFilterOption value)
    {
        if (_suppressCategoryFilterChange) return;
        UpdateCategoryChipSelection();
        RefreshView();
    }

    private void RefreshCategoryChips()
    {
        var selected = SelectedCategoryFilter.Value;
        CategoryChips.Clear();

        var categoryNodes = _graph?.Nodes
            .Where(node =>
                (!node.IsOptionalDependency || ShowOptional) &&
                (!node.IsRecommendedDependency || ShowRecommends) &&
                !node.IsEmbedded)
            .ToList() ?? [];
        var groups = categoryNodes
            .GroupBy(node => node.Category)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => ModCategoryText.Name(group.Key), StringComparer.CurrentCultureIgnoreCase)
            .ToList() ?? [];

        var allCount = categoryNodes.Count;
        CategoryChips.Add(new CategoryChipItem(null, "全部", allCount, SelectCategory));
        foreach (var group in groups.Where(group => group.Key != ModCategory.Other || group.Count() > 0))
            CategoryChips.Add(new CategoryChipItem(
                group.Key, ModCategoryText.Name(group.Key), group.Count(), SelectCategory));

        if (selected is not null && groups.All(group => group.Key != selected))
        {
            _suppressCategoryFilterChange = true;
            SelectedCategoryFilter = CategoryFilterOption.All[0];
            _suppressCategoryFilterChange = false;
        }

        UpdateCategoryChipSelection();
    }

    private void SelectCategory(ModCategory? category)
    {
        SelectedCategoryFilter = CategoryFilterOption.All
            .FirstOrDefault(option => option.Value == category) ?? CategoryFilterOption.All[0];
    }

    private void UpdateCategoryChipSelection()
    {
        foreach (var chip in CategoryChips)
            chip.IsActive = chip.Value == SelectedCategoryFilter.Value;
    }

    [RelayCommand]
    private void ToggleSortDirection() => SortDescending = !SortDescending;

    [RelayCommand]
    private void ResetFilters()
    {
        SearchText = string.Empty;
        ShowRequires = true;
        ShowIncompatible = true;
        ShowOptional = true;
        ShowEmbedded = true;
        ShowRecommends = true;
        ShowManual = true;
        ShowFabric = true;
        ShowQuilt = true;
        ShowForge = true;
        ShowNeoForge = true;
        ShowUnknown = true;
        OnlyProblems = false;
        OnlyUpdatable = false;
        HideIsolated = false;
        ShowMissing = true;
        ShowUninstalledOptional = false;
        SelectedStatusFilter = StatusFilterOption.All[0];
        SelectedSideFilter = SideFilterOption.All[0];
        SelectedSortOption = ModSortOption.All[0];
        SelectedCategoryFilter = CategoryFilterOption.All[0];
        RefreshView();
    }

    [RelayCommand]
    private Task BatchDownloadAsync() => RunBatchAsync(BatchModAction.Download);

    [RelayCommand]
    private Task BatchUpgradeAsync() => RunBatchAsync(BatchModAction.Upgrade);

    [RelayCommand]
    private Task BatchDisableAsync() => RunBatchAsync(BatchModAction.Disable);

    [RelayCommand]
    private Task BatchDeleteAsync() => RunBatchAsync(BatchModAction.Delete);

    private async Task RunBatchAsync(BatchModAction action)
    {
        if (IsBusy || _scanResult is null || _graph is null) return;

        var nodes = CheckedNodes();
        if (nodes.Count == 0)
        {
            StatusText = "请先勾选要处理的模组。";
            return;
        }

        var title = action switch
        {
            BatchModAction.Download => "批量下载",
            BatchModAction.Upgrade => "批量升级",
            BatchModAction.Disable => "批量禁用",
            BatchModAction.Delete => "批量删除",
            _ => "批量操作",
        };

        var disableNodes = action == BatchModAction.Disable
            ? nodes.Concat(FindOrphanDependents(nodes))
                .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList()
            : nodes;
        var orphanCount = disableNodes.Count - nodes.Count;
        var confirm = action switch
        {
            BatchModAction.Download =>
                $"下载选中的 {nodes.Count} 个未安装模组？已安装的会自动跳过，仅下载与当前 MC / 加载器兼容的版本。",
            BatchModAction.Upgrade =>
                $@"检查并升级选中的 {nodes.Count} 个模组？

只使用与当前 MC / 加载器兼容的最新版本；旧 JAR 会移到 .packdep-trash。",
            BatchModAction.Disable =>
                $"禁用选中的 {nodes.Count} 个模组？" +
                (orphanCount > 0 ? $@"

检测到 {orphanCount} 个依赖它们的模组，将一并禁用。" : string.Empty) +
                @"

将 JAR 重命名为 .jar.disabled，可在「更多 → 管理已禁用模组」恢复。",
            BatchModAction.Delete =>
                $@"删除选中的 {nodes.Count} 个模组？

不会永久删除：文件会移到 .packdep-trash，可手动恢复。",
            _ => $"处理选中的 {nodes.Count} 个模组？",
        };
        if (!await _dialogs.ConfirmAsync(title, confirm)) return;

        if (action is BatchModAction.Disable or BatchModAction.Delete)
        {
            BatchModResult result;
            try
            {
                result = action == BatchModAction.Disable
                    ? _services.Batch.Disable(disableNodes, _scanResult.RootPath)
                    : _services.Batch.Delete(nodes, _scanResult.RootPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                StatusText = $"{title}失败：{ex.Message}";
                await _dialogs.ShowMessageAsync($"{title}失败", ex.Message);
                return;
            }

            await ShowBatchResultAsync(title, result);
            StatusText = $"{title}完成：成功 {result.SuccessCount}，失败 {result.FailedCount}，正在重新扫描…";
            await ScanAsync();
            return;
        }

        var cts = BeginOperation();
        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = $"{title}…";
        StatusText = $"{title}：准备…";

        var detail = new DownloadProgressViewModel(
            title, $"{nodes.Count} 个模组 · {_scanResult.Profile.McVersion} · {_scanResult.Profile.DisplayLoader}");
        DownloadProgressWindow.ShowFor(detail);
        BatchModResult? batchResult = null;

        try
        {
            var progress = new Progress<DownloadProgress>(report =>
            {
                detail.Update(report);
                ProgressValue = report.Fraction;
                ProgressText = report.Total > 0 ? $"{report.Phase} {report.Done}/{report.Total}" : report.Phase;
                if (!string.IsNullOrWhiteSpace(report.Message))
                    StatusText = $"{report.Phase} · {report.Message}";
            });

            batchResult = action == BatchModAction.Download
                ? await _services.Batch.DownloadAsync(
                    nodes, _scanResult.Profile, _scanResult.RootPath, progress, cts.Token,
                    maxConcurrency: _services.Settings.MaxParallelDownloads, graph: _graph)
                : await _services.Batch.UpgradeAsync(
                    nodes, _scanResult.Profile, _scanResult.RootPath, progress, cts.Token,
                    maxConcurrency: _services.Settings.MaxParallelDownloads, graph: _graph);
            detail.Finish(batchResult.FailedCount == 0,
                $"成功 {batchResult.SuccessCount} · 跳过 {batchResult.SkippedCount} · 失败 {batchResult.FailedCount}");
        }
        catch (OperationCanceledException)
        {
            detail.Finish(false, "已取消");
        }
        catch (Exception ex)
        {
            detail.Finish(false, ex.Message);
            StatusText = $"{title}失败：{ex.Message}";
        }
        finally
        {
            EndOperation(cts);
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }

        if (batchResult is null) return;
        await ShowBatchResultAsync(title, batchResult);
        StatusText = $"{title}完成：成功 {batchResult.SuccessCount}，失败 {batchResult.FailedCount}，正在重新扫描…";
        await ScanAsync();
    }

    private async Task ShowBatchResultAsync(string title, BatchModResult result)
    {
        var message = $"成功 {result.SuccessCount}，跳过 {result.SkippedCount}，失败 {result.FailedCount}。";
        var details = result.Items
            .Where(item => !item.Success || item.Skipped)
            .Take(14)
            .Select(item => $"· {item.Name}：{item.Message}")
            .ToList();
        if (details.Count > 0)
            message += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, details);

        await _dialogs.ShowMessageAsync(title, message);
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        var selectAll = NodeItems.Any(item => !item.IsChecked);
        _suppressCheckedSync = true;
        _checkedNodeIds.Clear();
        foreach (var item in NodeItems)
        {
            item.IsChecked = selectAll;
            if (selectAll) _checkedNodeIds.Add(item.Id);
        }
        _suppressCheckedSync = false;
        UpdateCheckedState();
    }

    [RelayCommand]
    private async Task OpenPackToolsAsync()
    {
        if (_scanResult is null || _graph is null)
        {
            StatusText = "请先扫描整合包。";
            return;
        }

        var report = _services.Audit.Audit(_scanResult, _graph);
        var result = await _dialogs.ShowPackToolsAsync(report);
        if (result is null) return;

        switch (result.Action)
        {
            case PackToolsAction.ExportModList:
                await ExportModListAsync();
                break;
            case PackToolsAction.ExportCsv:
                await ExportModListCsvAsync();
                break;
            case PackToolsAction.ExportMissing:
                await ExportMissingAsync();
                break;
            case PackToolsAction.ExportAudit:
                await ExportAuditAsync(report);
                break;
            case PackToolsAction.OpenMods:
                OpenPackSubfolder("mods");
                break;
            case PackToolsAction.OpenConfig:
                OpenPackSubfolder("config");
                break;
            case PackToolsAction.OpenResourcePacks:
                OpenPackSubfolder("resourcepacks");
                break;
            case PackToolsAction.CleanDuplicateFiles:
                await CleanDuplicateJarsAsync(result.ExtraFiles);
                break;
            case PackToolsAction.CleanDuplicateModFiles:
                await CleanDuplicateModJarsAsync(result.ExtraFiles);
                break;
        }
    }

    [RelayCommand]
    private async Task ExportModListAsync()
    {
        if (_scanResult is null || _graph is null) return;
        var path = await _dialogs.PickSaveFileAsync(
            "导出模组清单", ExportFileName("modlist", "md"), "md", "Markdown");
        if (string.IsNullOrWhiteSpace(path)) return;

        PackListExporter.ExportModListMarkdown(_scanResult, _graph, path);
        StatusText = $"模组清单已导出：{Path.GetFileName(path)}";
        await _dialogs.ShowMessageAsync("导出完成", $"模组清单已写入：{Environment.NewLine}{path}");
    }

    [RelayCommand]
    private async Task ExportMissingAsync()
    {
        if (_scanResult is null || _graph is null) return;
        var path = await _dialogs.PickSaveFileAsync(
            "导出缺失依赖清单", ExportFileName("missing-dependencies", "md"), "md", "Markdown");
        if (string.IsNullOrWhiteSpace(path)) return;

        PackListExporter.ExportMissingMarkdown(_scanResult, _graph, path);
        StatusText = $"缺失依赖清单已导出：{Path.GetFileName(path)}";
        await _dialogs.ShowMessageAsync("导出完成", $"缺失依赖清单已写入：{Environment.NewLine}{path}");
    }

    [RelayCommand]
    private async Task ExportModListCsvAsync()
    {
        if (_scanResult is null || _graph is null) return;
        var path = await _dialogs.PickSaveFileAsync(
            "导出模组清单 CSV", ExportFileName("modlist", "csv"), "csv", "CSV");
        if (string.IsNullOrWhiteSpace(path)) return;

        PackListExporter.ExportModListCsv(_scanResult, _graph, path);
        StatusText = $"模组清单 CSV 已导出：{Path.GetFileName(path)}";
        await _dialogs.ShowMessageAsync("导出完成", $"模组清单 CSV 已写入：{Environment.NewLine}{path}");
    }

    private async Task ExportAuditAsync(PackAuditReport report)
    {
        var path = await _dialogs.PickSaveFileAsync(
            "导出整合包检查报告", ExportFileName("audit", "md"), "md", "Markdown");
        if (string.IsNullOrWhiteSpace(path)) return;

        PackListExporter.ExportAuditMarkdown(report, path);
        StatusText = $"检查报告已导出：{Path.GetFileName(path)}";
        await _dialogs.ShowMessageAsync("导出完成", $"检查报告已写入：{Environment.NewLine}{path}");
    }

    private async Task CleanDuplicateModJarsAsync(IReadOnlyList<string> paths)
    {
        if (_scanResult is null || paths.Count == 0) return;
        if (!await _dialogs.ConfirmAsync(
                "清理重复 modId",
                $"将把 {paths.Count} 个重复 modId 的额外 JAR 移到 .packdep-trash，" +
                $"{Environment.NewLine}每个重复组按路径保留第一个文件。继续吗？",
                "清理"))
            return;

        var moved = 0;
        var failures = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                _services.ModFiles.MoveFileToTrash(path, _scanResult.RootPath);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failures.Add($"{Path.GetFileName(path)}：{ex.Message}");
            }
        }

        StatusText = $"已清理 {moved} 个重复 modId 副本，正在重新扫描…";
        if (failures.Count > 0)
            await _dialogs.ShowMessageAsync("部分重复文件未清理",
                string.Join(Environment.NewLine, failures.Take(10)));
        await ScanAsync();
    }

    private async Task CleanDuplicateJarsAsync(IReadOnlyList<string> paths)
    {
        if (_scanResult is null || paths.Count == 0) return;
        if (!await _dialogs.ConfirmAsync(
                "清理重复 JAR",
                $"将把 {paths.Count} 个 SHA1 完全相同的副本移到 .packdep-trash，每组保留第一个文件。继续吗？"))
            return;

        var moved = 0;
        foreach (var path in paths)
        {
            try
            {
                _services.ModFiles.MoveFileToTrash(path, _scanResult.RootPath);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                StatusText = $"清理失败：{ex.Message}";
            }
        }

        await _dialogs.ShowMessageAsync("清理完成", $"已移走 {moved} 个重复 JAR。");
        await ScanAsync();
    }

    private void OpenPackSubfolder(string name)
    {
        if (_scanResult is null) return;
        var path = Path.Combine(_scanResult.RootPath, name);
        if (!Directory.Exists(path))
        {
            StatusText = $"{name} 目录不存在";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"无法打开目录：{ex.Message}";
        }
    }

    private string ExportFileName(string suffix, string extension)
    {
        var name = string.IsNullOrWhiteSpace(PackName) ? "pack" : PackName;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '-');
        return $"{name}-{suffix}.{extension}";
    }

    [RelayCommand]
    private void FocusSelectedRelations()
    {
        if (_selectedNodeId is null || _graph is null) return;
        var node = _graph.FindNode(_selectedNodeId);
        if (node is null) return;

        _relationFocusId = node.Id;
        _relationFocusIds.Clear();
        _relationFocusIds.Add(node.Id);
        foreach (var edge in _graph.Edges)
        {
            var outgoing = string.Equals(edge.From, node.Id, StringComparison.OrdinalIgnoreCase);
            var incoming = string.Equals(edge.To, node.Id, StringComparison.OrdinalIgnoreCase);
            if (!outgoing && !incoming) continue;
            if (!IsRelationEdgeVisible(edge.Type)) continue;

            var otherId = outgoing ? edge.To : edge.From;
            var other = _graph.FindNode(otherId);
            if (other is { IsSynthetic: true } &&
                edge.Type is EdgeType.Optional or EdgeType.Recommends &&
                !ShowUninstalledOptional)
                continue;

            _relationFocusIds.Add(otherId);
        }

        // A stale search term would dim every related node whose name does not
        // match it. Relation focus is already a scope, so start from a clean
        // search state.
        SearchText = string.Empty;
        RelationFocusTitle = $"只看 {node.Name} 的相关关系";
        IsRelationFocus = true;
        SelectedViewOption = ViewOption.All.First(option => option.Value == "graph");
        RefreshView();
    }

    [RelayCommand]
    private void ClearRelationFocus()
    {
        if (!IsRelationFocus) return;
        _relationFocusId = null;
        _relationFocusIds.Clear();
        IsRelationFocus = false;
        RelationFocusTitle = string.Empty;
        RefreshView();
    }

    private async Task ScheduleCompactLayoutAsync()
    {
        _filterLayoutCts?.Cancel();
        _filterLayoutCts?.Dispose();
        var cts = new CancellationTokenSource();
        _filterLayoutCts = cts;
        try
        {
            await Task.Delay(180, cts.Token);
            if (cts.IsCancellationRequested || Scene is null || _graph is null) return;

            var visibleIds = Scene.Nodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (visibleIds.Count == 0) return;
            var subgraph = new GraphDocument
            {
                Nodes = _graph.Nodes.Where(node => visibleIds.Contains(node.Id)).ToList(),
                Edges = _graph.Edges
                    .Where(edge => visibleIds.Contains(edge.From) && visibleIds.Contains(edge.To))
                    .ToList(),
            };
            var algorithm = _services.Settings.LayoutAlgorithm;
            var positions = await Task.Run(
                () => _services.Layout.Compute(subgraph, new LayoutOptions { Algorithm = algorithm }, cts.Token),
                cts.Token);
            if (cts.IsCancellationRequested) return;

            _positions = new Dictionary<string, LayoutPoint>(positions, StringComparer.OrdinalIgnoreCase);
            BuildScene();
            _graphView?.FitToView();
        }
        catch (OperationCanceledException)
        {
            // Superseded by another filter change.
        }
        finally
        {
            if (ReferenceEquals(_filterLayoutCts, cts))
                _filterLayoutCts = null;
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingUpdates) return;
        var feed = _services.Settings.UpdateFeedUrl;
        if (string.IsNullOrWhiteSpace(feed))
        {
            UpdateStatusText = "未配置更新源，请先在设置中填写 update.json 地址。";
            StatusText = UpdateStatusText;
            return;
        }

        IsCheckingUpdates = true;
        UpdateStatusText = "正在检查更新…";
        StatusText = UpdateStatusText;
        try
        {
            var version = typeof(MainWindowViewModel).Assembly.GetName().Version;
            var current = version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
            var result = await _services.Updater.CheckAsync(feed, current);
            if (!result.Succeeded)
            {
                UpdateStatusText = result.Error ?? "检查更新失败。";
                StatusText = UpdateStatusText;
                return;
            }

            if (!result.HasUpdate)
            {
                UpdateStatusText = $"已是最新版本（{current}）。";
                StatusText = UpdateStatusText;
                return;
            }

            _lastUpdateUrl = string.IsNullOrWhiteSpace(result.ReleaseUrl)
                ? result.DownloadUrl
                : result.ReleaseUrl;
            UpdateStatusText = $"发现新版本 {result.LatestVersion}。";
            StatusText = UpdateStatusText;

            if (_services.Settings.AutoDownloadUpdates)
            {
                UpdateStatusText = "正在下载并校验更新…";
                StatusText = UpdateStatusText;
                ProgressValue = 0;
                var progress = new Progress<double>(value => ProgressValue = Math.Clamp(value * 100, 0, 100));
                var download = await _services.Installer.DownloadAsync(
                    result, result.LatestVersion ?? "latest", progress);
                if (!download.Succeeded || string.IsNullOrWhiteSpace(download.FilePath))
                {
                    UpdateStatusText = download.Error ?? "更新下载失败。";
                    StatusText = UpdateStatusText;
                    return;
                }

                if (_services.Settings.AutoInstallUpdates)
                {
                    if (!_services.Installer.StartSelfUpdate(download.FilePath))
                    {
                        UpdateStatusText = "更新已下载，但无法启动替换程序。";
                        StatusText = UpdateStatusText;
                        return;
                    }

                    UpdateStatusText = "更新已下载并校验，正在重启…";
                    StatusText = UpdateStatusText;
                    if (Avalonia.Application.Current?.ApplicationLifetime is
                        Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
                        lifetime.Shutdown();
                    return;
                }

                UpdateStatusText = "更新已下载并校验。";
                StatusText = UpdateStatusText;
                await _dialogs.ShowMessageAsync(
                    "更新已下载",
                    $"新版本：{result.LatestVersion}{Environment.NewLine}" +
                    $"已校验 SHA256：{download.FilePath}{Environment.NewLine}{Environment.NewLine}" +
                    "可关闭程序后自行替换，或在设置中开启自动重启更新。");
                return;
            }

            await _dialogs.ShowMessageAsync(
                "发现新版本",
                $"当前版本：{current}{Environment.NewLine}" +
                $"最新版本：{result.LatestVersion}{Environment.NewLine}{Environment.NewLine}" +
                $"{result.Notes ?? "可在 Release 页面查看更新内容。"}{Environment.NewLine}{Environment.NewLine}" +
                $"下载页：{_lastUpdateUrl ?? "未提供"}");
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"检查更新失败：{ex.Message}";
            StatusText = UpdateStatusText;
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand]
    private void OpenUpdatePage()
    {
        if (string.IsNullOrWhiteSpace(_lastUpdateUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_lastUpdateUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = $"无法打开更新页面：{ex.Message}";
        }
    }

    private void RefreshView()
    {
        if (_graph is null) return;
        BuildScene();
        if (IsGraphView)
            _ = ScheduleCompactLayoutAsync();
    }
}
