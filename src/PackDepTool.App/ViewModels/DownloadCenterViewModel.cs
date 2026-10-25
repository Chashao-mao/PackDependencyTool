using System.Collections.ObjectModel;
using System.Net.Http;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Models;
using PackDepTool.Core.Net;
using PackDepTool.App.Views;

namespace PackDepTool.App.ViewModels;

public sealed class DownloadCenterContext
{
    public DownloadProjectType InitialType { get; init; } = DownloadProjectType.Mod;
    public string? PackRoot { get; init; }
    public string? McVersion { get; init; }
    public LoaderKind Loader { get; init; }
    public string? InitialQuery { get; init; }
    public string? DefaultDestination { get; init; }
    public IReadOnlySet<string> InstalledProjectIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class DownloadCenterResult
{
    public string? PackRoot { get; init; }
    public string? ModFilePath { get; init; }
    public string? LastDownloadedProjectId { get; init; }
    public IReadOnlySet<string> DownloadedProjectIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public PackImportResult? ImportResult { get; init; }
}

public sealed record DownloadTypeOption(DownloadProjectType Value, string Label);

public sealed record DownloadSourceOption(DownloadSource Value, string Label);

public sealed record DownloadCategoryItem(DownloadCategory? Category, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class DownloadProjectItem : ObservableObject
{
    public DownloadProjectItem(DownloadProject model, bool isInstalled = false)
    {
        Model = model;
        Initial = (model.Name.Trim().FirstOrDefault() is var ch && ch != default
            ? char.ToUpperInvariant(ch).ToString()
            : "?");
        AvatarBrush = new SolidColorBrush(GraphPalette.LoaderColor(LoaderKind.Unknown));
        var palette = new[]
        {
            GraphPalette.Accent, GraphPalette.EdgeRequires, GraphPalette.EdgeIncompatible,
            GraphPalette.EdgeEmbedded, GraphPalette.Success, GraphPalette.Warning,
        };
        AvatarBrush = new SolidColorBrush(palette[Math.Abs(model.Name.GetHashCode()) % palette.Length]);
        IsInstalled = isInstalled;
    }

    public DownloadProject Model { get; }
    public string Name => Model.Name;
    public string Description => Model.Description ?? string.Empty;
    public string SourceName => Model.SourceName;
    public string TypeName => Model.TypeName;
    public string Initial { get; }
    public IBrush AvatarBrush { get; }
    public bool IsInstalled { get; }
    public string Meta => $"{Model.SourceName} · {(Model.Downloads > 0 ? $"{Model.Downloads:N0} 次下载" : Model.TypeName)}";
    [ObservableProperty] private Bitmap? _icon;
    public bool HasIcon => Icon is not null;
    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));
}

public sealed class DownloadVersionItem
{
    public DownloadVersionItem(
        DownloadVersion model,
        IReadOnlyDictionary<string, string>? dependencyNames = null)
    {
        Model = model;
        CompatibilityText = model.IsCompatible ? "兼容" : "不兼容 · 可强下";
        CompatibilityBrush = new SolidColorBrush(model.IsCompatible ? GraphPalette.Success : GraphPalette.EdgeIncompatible);
        RequiredDependenciesText = FormatDependencies("必需前置", model.RequiredDependencies, dependencyNames);
        OptionalDependenciesText = FormatDependencies("可选前置", model.OptionalDependencies, dependencyNames);
    }

    public DownloadVersion Model { get; }
    public string Title => Model.VersionNumber;
    public string Meta => Model.Meta;
    public string FileName => Model.FileName;
    public string CompatibilityText { get; }
    public IBrush CompatibilityBrush { get; }
    public string RequiredDependenciesText { get; }
    public string OptionalDependenciesText { get; }
    public bool HasRequiredDependencies => Model.RequiredDependencies.Count > 0;
    public bool HasOptionalDependencies => Model.OptionalDependencies.Count > 0;
    public string SizeText => Model.FileSize > 0
        ? $"{Model.FileSize / 1024d / 1024d:0.#} MB"
        : string.Empty;

    private static string FormatDependencies(
        string label,
        IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, string>? names)
    {
        if (ids.Count == 0) return string.Empty;
        var resolved = ids
            .Select(id => names is not null && names.TryGetValue(id, out var name) ? name : id)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (resolved.Count == 0) return string.Empty;
        return $"{label}：{string.Join("、", resolved)}";
    }
}

public sealed partial class DownloadQueueRow : ObservableObject
{
    private readonly DownloadQueueService _queue;
    public DownloadQueueRow(DownloadQueueService queue, DownloadQueueItem item)
    {
        _queue = queue;
        Item = item;
        Refresh();
    }

    public DownloadQueueItem Item { get; }
    public string Title => Item.Title;
    public string Detail => Item.Detail ?? string.Empty;

    [ObservableProperty] private string _stateText = string.Empty;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private bool _canToggle;
    [ObservableProperty] private bool _canRetry;
    public string ToggleText => Item.State == DownloadQueueState.Paused ? "继续" : "暂停";
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public void Refresh()
    {
        StateText = Item.State switch
        {
            DownloadQueueState.Queued => "排队中",
            DownloadQueueState.Running => "下载中",
            DownloadQueueState.Paused => "已暂停",
            DownloadQueueState.Completed => "已完成",
            DownloadQueueState.Failed => "失败",
            DownloadQueueState.Cancelled => "已取消",
            _ => string.Empty,
        };
        var progress = Item.Progress;
        Fraction = progress?.Fraction ?? 0;
        ProgressText = progress is { TotalBytes: > 0 }
            ? $"{FormatBytes(progress.Bytes)} / {FormatBytes(progress.TotalBytes)}"
            : progress is { Bytes: > 0 }
                ? FormatBytes(progress.Bytes)
                : string.Empty;
        ErrorText = Item.Error ?? string.Empty;
        CanToggle = Item.State is DownloadQueueState.Queued or DownloadQueueState.Running or DownloadQueueState.Paused;
        CanRetry = Item.State is DownloadQueueState.Failed or DownloadQueueState.Cancelled;
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(HasError));
    }

    [RelayCommand]
    private void Toggle()
    {
        if (Item.State == DownloadQueueState.Paused) _queue.Resume(Item);
        else _queue.Pause(Item);
    }

    [RelayCommand]
    private void Cancel() => _queue.Cancel(Item);

    [RelayCommand]
    private void Retry() => _queue.Retry(Item);

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d:0.#} MB",
        _ => $"{bytes / 1024d / 1024d / 1024d:0.##} GB",
    };
}

public sealed partial class DownloadCenterViewModel : ObservableObject, IDisposable
{
    private readonly ModDownloadService _downloader;
    private readonly ModpackImporter _importer;
    private readonly HttpClient _http;
    private static readonly Dictionary<string, Bitmap?> IconCache = new(StringComparer.Ordinal);
    private readonly DownloadCenterContext _context;
    private readonly Func<Task<string?>> _pickFolder;
    private readonly DownloadQueueService _queue;
    private string? _lastDownloadedPath;
    private string? _lastDownloadedProjectId;
    private readonly HashSet<string> _downloadedProjectIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string> _queueProjectKeys = [];
    private int _queueRefreshVersion;
    private readonly HashSet<Guid> _sessionQueueIds = [];
    private bool _autoCloseRaised;

    private const int PageSize = 20;
    private CancellationTokenSource? _operationCts;
    private int _versionLoadVersion;

    public DownloadCenterViewModel(
        ModDownloadService downloader,
        ModpackImporter importer,
        HttpClient http,
        DownloadCenterContext context,
        Func<Task<string?>> pickFolder,
        DownloadQueueService queue)
    {
        _downloader = downloader;
        _importer = importer;
        _http = http;
        _context = context;
        _pickFolder = pickFolder;
        _queue = queue;
        _queue.Changed += OnQueueChanged;

        TypeOptions =
        [
            new(DownloadProjectType.Mod, "模组"),
            new(DownloadProjectType.Modpack, "整合包"),
        ];
        SourceOptions =
        [
            new(DownloadSource.All, "双站搜索"),
            new(DownloadSource.Modrinth, "Modrinth"),
            new(DownloadSource.CurseForge, "CurseForge"),
        ];

        _selectedType = TypeOptions.First(option => option.Value == context.InitialType);
        _selectedSource = SourceOptions[0];
        _searchQuery = context.InitialQuery ?? string.Empty;
        _destinationFolder = context.DefaultDestination ?? DefaultDownloadsFolder();
        _isModpack = context.InitialType == DownloadProjectType.Modpack;
        _downloadButtonText = _isModpack ? "下载并导入" : "下载到 mods";
        UpdateTargetText();
    }

    public IReadOnlyList<DownloadTypeOption> TypeOptions { get; }
    public IReadOnlyList<DownloadSourceOption> SourceOptions { get; }

    [ObservableProperty] private DownloadTypeOption _selectedType;
    [ObservableProperty] private DownloadSourceOption _selectedSource;
    [ObservableProperty] private string _searchQuery;
    [ObservableProperty] private DownloadProjectItem? _selectedProject;
    [ObservableProperty] private DownloadVersionItem? _selectedVersion;
    [ObservableProperty] private bool _isModpack;
    [ObservableProperty] private bool _hasSelectedProject;
    [ObservableProperty] private bool _hasVersions;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _statusText = "输入关键词搜索 Modrinth / CurseForge";
    [ObservableProperty] private string _destinationFolder;
    [ObservableProperty] private string _targetText = string.Empty;
    [ObservableProperty] private string _downloadButtonText;

    public string ContextText => IsModpack
        ? "整合包不需要匹配当前实例版本"
        : string.IsNullOrWhiteSpace(_context.McVersion)
            ? "未指定当前实例版本"
            : $"当前实例：{_context.McVersion} · {GraphPalette.LoaderText(_context.Loader)}";

    public ObservableCollection<DownloadProjectItem> Projects { get; } = [];
    public ObservableCollection<DownloadVersionItem> Versions { get; } = [];
    public ObservableCollection<DownloadQueueRow> Queue { get; } = [];
    public bool HasQueue => Queue.Count > 0;
    public ObservableCollection<DownloadCategoryItem> Categories { get; } = [];

    [ObservableProperty] private DownloadCategoryItem? _selectedCategory;
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private bool _hasPreviousPage;
    [ObservableProperty] private bool _hasNextPage;
    [ObservableProperty] private string _pageText = "第 1 页";
    [ObservableProperty] private bool _isLoadingPage;

    public Func<string, string, Task<bool>>? ConfirmChainDownload { get; set; }

    public event Action<DownloadCenterResult?>? Completed;

    public async Task InitializeAsync()
    {
        await LoadCategoriesAsync();
        await SearchAsync();
    }

    partial void OnSelectedTypeChanged(DownloadTypeOption value)
    {
        IsModpack = value?.Value == DownloadProjectType.Modpack;
        DownloadButtonText = IsModpack ? "下载并导入" : "下载到 mods";
        OnPropertyChanged(nameof(ContextText));
        UpdateTargetText();
        Projects.Clear();
        Versions.Clear();
        SelectedProject = null;
        SelectedVersion = null;
        HasSelectedProject = false;
        HasVersions = false;
        ResetPage();
        _ = LoadCategoriesAsync();
    }

    partial void OnSelectedSourceChanged(DownloadSourceOption value)
    {
        Projects.Clear();
        Versions.Clear();
        SelectedProject = null;
        SelectedVersion = null;
        HasSelectedProject = false;
        HasVersions = false;
        ResetPage();
        _ = LoadCategoriesAsync();
    }

    partial void OnSelectedCategoryChanged(DownloadCategoryItem? value) => ResetPage();

    partial void OnSelectedProjectChanged(DownloadProjectItem? value)
    {
        HasSelectedProject = value is not null;
        Versions.Clear();
        SelectedVersion = null;
        HasVersions = false;
        if (value is not null) _ = LoadVersionsAsync(value);
    }

    partial void OnDestinationFolderChanged(string value) => UpdateTargetText();

    [RelayCommand]
    private Task SearchAsync() => LoadPageAsync(1);

    [RelayCommand]
    private Task NextPageAsync() => HasNextPage ? LoadPageAsync(CurrentPage + 1) : Task.CompletedTask;

    [RelayCommand]
    private Task PreviousPageAsync() => HasPreviousPage ? LoadPageAsync(CurrentPage - 1) : Task.CompletedTask;

    private void ResetPage()
    {
        CurrentPage = 1;
        HasPreviousPage = false;
        HasNextPage = false;
        PageText = "第 1 页";
    }

    private bool IsInstalled(DownloadProject project) =>
        _context.InstalledProjectIds.Contains(project.ProjectId) ||
        (!string.IsNullOrWhiteSpace(project.Slug) && _context.InstalledProjectIds.Contains(project.Slug));

    private async Task LoadCategoriesAsync()
    {
        try
        {
            var selected = SelectedCategory?.Category;
            var categories = await _downloader.GetCategoriesAsync(
                SelectedSource.Value, SelectedType.Value);
            Categories.Clear();
            Categories.Add(new DownloadCategoryItem(null, "全部分类"));
            foreach (var category in categories)
                Categories.Add(new DownloadCategoryItem(category, category.Name));

            SelectedCategory = selected is null
                ? Categories[0]
                : Categories.FirstOrDefault(item => item.Category is not null &&
                    string.Equals(item.Category.Name, selected.Name, StringComparison.OrdinalIgnoreCase)) ??
                  Categories[0];
        }
        catch (Exception ex) when (ex is HttpRequestException or CurseForgeApiException or ModrinthApiException)
        {
            Categories.Clear();
            Categories.Add(new DownloadCategoryItem(null, "全部分类"));
            SelectedCategory = Categories[0];
        }
    }

    private async Task LoadPageAsync(int page)
    {
        if (IsBusy) return;
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var cts = _operationCts;
        var requestedPage = Math.Max(1, page);

        IsBusy = true;
        IsLoadingPage = true;
        ProgressValue = 0;
        ProgressText = string.IsNullOrWhiteSpace(SearchQuery) ? "浏览中…" : "搜索中…";
        StatusText = string.IsNullOrWhiteSpace(SearchQuery)
            ? $"正在浏览 {SelectedSource.Label}…"
            : $"正在搜索 {SelectedSource.Label}…";
        Projects.Clear();
        Versions.Clear();
        SelectedProject = null;
        SelectedVersion = null;
        HasVersions = false;
        HasSelectedProject = false;

        try
        {
            var pageResult = await _downloader.SearchPageAsync(
                SelectedSource.Value,
                SelectedType.Value,
                SearchQuery.Trim(),
                SelectedCategory?.Category,
                (requestedPage - 1) * PageSize,
                PageSize,
                cts.Token);

            var items = pageResult.Projects
                .Select(project => new DownloadProjectItem(project, IsInstalled(project)))
                .ToList();
            foreach (var item in items)
                Projects.Add(item);
            _ = LoadProjectIconsAsync(items, cts.Token);

            CurrentPage = requestedPage;
            HasPreviousPage = CurrentPage > 1;
            HasNextPage = pageResult.HasMore;
            PageText = $"第 {CurrentPage} 页";
            StatusText = items.Count == 0
                ? "没有找到结果。"
                : $"找到 {items.Count} 个结果，选择项目查看可用版本。";
            SelectedProject = Projects.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消搜索。";
        }
        catch (MissingApiKeyException)
        {
            StatusText = "CurseForge 需要 API Key，请先在设置中配置。";
        }
        catch (Exception ex) when (ex is CurseForgeApiException or ModrinthApiException or HttpRequestException)
        {
            StatusText = $"搜索失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsLoadingPage = false;
            ProgressText = string.Empty;
            if (ReferenceEquals(_operationCts, cts))
            {
                _operationCts = null;
                cts.Dispose();
            }
        }
    }

    private void OnQueueChanged()
    {
        // Per-file progress can fire several times per second for each queued
        // download. Coalesce updates so the UI thread is not flooded.
        var version = ++_queueRefreshVersion;
        DispatcherTimer.RunOnce(() =>
        {
            if (version != _queueRefreshVersion) return;
            RefreshQueue();
        }, TimeSpan.FromMilliseconds(50), DispatcherPriority.Background);
    }

    private void RefreshQueue()
    {
        var snapshot = _queue.Snapshot();
        foreach (var item in snapshot)
        {
            if (item.State == DownloadQueueState.Completed &&
                File.Exists(item.Request.DestinationPath))
            {
                _lastDownloadedPath = item.Request.DestinationPath;
                if (_queueProjectKeys.TryGetValue(item.Id, out var key))
                {
                    _lastDownloadedProjectId = key;
                    _downloadedProjectIds.Add(key);
                }
            }

            var row = Queue.FirstOrDefault(existing => existing.Item.Id == item.Id);
            if (row is null)
                Queue.Add(new DownloadQueueRow(_queue, item));
            else
                row.Refresh();
        }

        foreach (var row in Queue.Where(existing =>
                     snapshot.All(item => item.Id != existing.Item.Id)).ToList())
            Queue.Remove(row);

        OnPropertyChanged(nameof(HasQueue));

        // Close the download center once every item enqueued in this window
        // has completed. Failed/cancelled items keep the window open so they
        // can be retried.
        if (!_autoCloseRaised && _sessionQueueIds.Count > 0 &&
            _sessionQueueIds.All(id => snapshot.Any(item =>
                item.Id == id && item.State == DownloadQueueState.Completed)) &&
            !string.IsNullOrWhiteSpace(_lastDownloadedPath))
        {
            _autoCloseRaised = true;
            Completed?.Invoke(BuildResult());
        }
    }

    private async Task LoadProjectIconsAsync(IReadOnlyList<DownloadProjectItem> items, CancellationToken token)
    {
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            var url = item.Model.IconUrl;
            if (string.IsNullOrWhiteSpace(url)) continue;

            if (IconCache.TryGetValue(url, out var cached))
            {
                if (cached is not null)
                    await Dispatcher.UIThread.InvokeAsync(() => item.Icon = cached);
                continue;
            }

            try
            {
                var bytes = await _http.GetByteArrayAsync(url, token).ConfigureAwait(false);
                using var stream = new MemoryStream(bytes, writable: false);
                var bitmap = new Bitmap(stream);
                IconCache[url] = bitmap;
                await Dispatcher.UIThread.InvokeAsync(() => item.Icon = bitmap);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException)
            {
                IconCache[url] = null;
            }
        }
    }

    private async Task LoadVersionsAsync(DownloadProjectItem item)
    {
        var loadId = Interlocked.Increment(ref _versionLoadVersion);
        IsBusy = true;
        ProgressValue = 0;
        ProgressText = "读取版本…";
        StatusText = $"正在读取 {item.Name} 的版本…";

        try
        {
            var versions = await _downloader.GetVersionsAsync(
                item.Model, _context.McVersion, _context.Loader, CancellationToken.None);

            if (loadId != _versionLoadVersion || !ReferenceEquals(SelectedProject, item)) return;

            var dependencyIds = versions
                .SelectMany(version => version.RequiredDependencies.Concat(version.OptionalDependencies))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            IReadOnlyDictionary<string, string>? dependencyNames = null;
            if (dependencyIds.Count > 0)
            {
                try
                {
                    dependencyNames = await _downloader.GetDependencyNamesAsync(
                        item.Model.Source, dependencyIds, CancellationToken.None);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or
                                               ArgumentException)
                {
                    // Version list remains usable with project IDs.
                }
            }

            if (loadId != _versionLoadVersion || !ReferenceEquals(SelectedProject, item)) return;
            foreach (var version in versions)
                Versions.Add(new DownloadVersionItem(version, dependencyNames));

            HasVersions = Versions.Count > 0;
            var compatible = Versions.FirstOrDefault(version => version.Model.IsCompatible);
            SelectedVersion = compatible ?? Versions.FirstOrDefault();
            StatusText = Versions.Count == 0
                ? "没有可用版本。"
                : compatible is null
                    ? $"共 {Versions.Count} 个版本；没有与当前实例兼容的版本，其他版本已标红，可强制下载。"
                    : $"共 {Versions.Count} 个版本，已自动选择兼容版本。" +
                      (IsModpack ? string.Empty : $" 当前实例：{_context.McVersion} / {GraphPalette.LoaderText(_context.Loader)}");
        }
        catch (Exception ex) when (ex is CurseForgeApiException or ModrinthApiException or HttpRequestException or
                                       MissingApiKeyException)
        {
            StatusText = $"读取版本失败：{ex.Message}";
        }
        finally
        {
            if (loadId == _versionLoadVersion)
            {
                IsBusy = false;
                ProgressText = string.Empty;
            }
        }
    }

    private async Task QueueModWithDependenciesAsync(
        DownloadVersion root,
        string rootName,
        string rootTitle)
    {
        var modsDir = Path.Combine(_context.PackRoot!, "mods");
        var chain = new List<DownloadVersion>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ProjectKey(root),
        };
        var installed = _context.InstalledProjectIds;
        var queuedKeys = _queueProjectKeys.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<DownloadVersion>();
        pending.Enqueue(root);

        while (pending.Count > 0 && chain.Count < 25)
        {
            var current = pending.Dequeue();
            foreach (var dependencyId in current.RequiredDependencies)
            {
                var key = $"{current.Source}:{dependencyId}";
                if (!seen.Add(key) || installed.Contains(dependencyId) || queuedKeys.Contains(key))
                    continue;

                try
                {
                    var project = new DownloadProject
                    {
                        Source = current.Source,
                        Type = DownloadProjectType.Mod,
                        ProjectId = dependencyId,
                        Name = dependencyId,
                    };
                    var versions = await _downloader.GetVersionsAsync(
                        project, _context.McVersion, _context.Loader, CancellationToken.None);
                    var dependency = versions.FirstOrDefault(version => version.IsCompatible) ??
                                     versions.FirstOrDefault();
                    if (dependency is null)
                    {
                        StatusText = $"未能找到前置 {dependencyId} 的可用版本，继续下载当前模组。";
                        continue;
                    }

                    chain.Add(dependency);
                    pending.Enqueue(dependency);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or
                                               CurseForgeApiException or ModrinthApiException or
                                               MissingApiKeyException)
                {
                    StatusText = $"读取前置 {dependencyId} 失败：{ex.Message}";
                }
            }
        }

        var includeChain = false;
        if (chain.Count > 0)
        {
            try
            {
                var resolvedNames = await _downloader.GetDependencyNamesAsync(
                    root.Source, chain.Select(version => version.ProjectId).ToList(), CancellationToken.None);
                foreach (var pair in resolvedNames)
                    names[pair.Key] = pair.Value;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or
                                           ArgumentException)
            {
                // Fall back to project IDs.
            }

            var text = string.Join(Environment.NewLine, chain.Select(version =>
                $"· {(names.TryGetValue(version.ProjectId, out var name) ? name : version.ProjectName)} ({version.ProjectId})"));
            if (chain.Count > 12)
                text += $"{Environment.NewLine}· …共 {chain.Count} 个必需前置";
            var confirm = ConfirmChainDownload;
            includeChain = confirm is not null &&
                           await confirm(
                               "一并下载必需前置？",
                               $"下载 {rootName} {rootTitle} 需要以下必需前置：{Environment.NewLine}{Environment.NewLine}" +
                               text + $"{Environment.NewLine}{Environment.NewLine}是否一并加入下载队列？");
        }

        var queued = 0;
        try
        {
            if (includeChain)
            {
                foreach (var dependency in chain)
                {
                    await EnqueueVersionAsync(dependency, names.TryGetValue(dependency.ProjectId, out var name)
                        ? name
                        : dependency.ProjectName, dependency.VersionNumber, modsDir);
                    queued++;
                }
            }

            await EnqueueVersionAsync(root, rootName, rootTitle, modsDir);
            queued++;
            StatusText = includeChain
                ? $"已加入 {queued} 个下载（含 {chain.Count} 个必需前置）。"
                : "已加入下载队列，可继续选择其他版本。";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or
                                       InvalidOperationException or CurseForgeApiException or
                                       ModrinthApiException or MissingApiKeyException)
        {
            StatusText = $"加入队列失败：{ex.Message}";
        }
    }

    private async Task EnqueueVersionAsync(
        DownloadVersion version,
        string name,
        string title,
        string modsDirectory)
    {
        var request = await _downloader.CreateFileRequestAsync(version, modsDirectory, CancellationToken.None);
        var queued = _queue.Enqueue(request, name, title);
        _queueProjectKeys[queued.Id] = ProjectKey(version);
        _sessionQueueIds.Add(queued.Id);
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsBusy) return;
        if (SelectedProject is null || SelectedVersion is null)
        {
            StatusText = "请先选择项目和版本。";
            return;
        }

        if (!IsModpack && string.IsNullOrWhiteSpace(_context.PackRoot))
        {
            StatusText = "请先在主界面打开一个整合包，再下载模组。";
            return;
        }

        if (IsModpack && string.IsNullOrWhiteSpace(DestinationFolder))
        {
            StatusText = "请选择整合包导入目录。";
            return;
        }

        var downloadVersion = SelectedVersion.Model;

        if (!IsModpack)
        {
            await QueueModWithDependenciesAsync(downloadVersion, SelectedProject.Name, SelectedVersion.Title);
            return;
        }

        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var cts = _operationCts;

        IsBusy = true;
        ProgressValue = 0;
        ProgressText = "准备下载…";
        StatusText = $"正在下载 {SelectedProject.Name} {SelectedVersion.Title}…";

        var detail = new DownloadProgressViewModel(
            IsModpack ? "下载并导入整合包" : "下载模组",
            $"{SelectedProject?.Name} · {SelectedVersion?.Title}");
        DownloadProgressWindow.ShowFor(detail);
        var progress = new Progress<DownloadProgress>(report =>
        {
            detail.Update(report);
            ProgressValue = report.Fraction;
            ProgressText = string.IsNullOrWhiteSpace(report.Message)
                ? report.Phase
                : $"{report.Phase} · {report.Message}";
            if (report.Total > 0)
                StatusText = $"{report.Phase} {report.Done}/{report.Total}";
        });

        try
        {
            if (!IsModpack)
            {
                var modsDir = Path.Combine(_context.PackRoot!, "mods");
                var download = await _downloader.DownloadAsync(
                    downloadVersion, modsDir, progress, cts.Token);

                StatusText = $"已下载：{download.FileName}";
                detail.Finish(true, $"已下载：{download.FileName}");
                var projectKey = ProjectKey(downloadVersion);
                _lastDownloadedProjectId = projectKey;
                _downloadedProjectIds.Add(projectKey);
                Completed?.Invoke(BuildResult(download.FilePath));
                return;
            }

            var tempRoot = Path.Combine(
                Path.GetTempPath(), "PackDependencyTool", "downloads", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            try
            {
                var archive = await _downloader.DownloadAsync(
                    downloadVersion, tempRoot, progress, cts.Token);

                StatusText = "下载完成，正在导入整合包…";
                ProgressText = "导入整合包…";
                ProgressValue = 0;

                var import = await _importer.ImportAsync(
                    archive.FilePath, DestinationFolder, progress, cts.Token);

                StatusText = $"整合包已导入：{import.PackRoot}";
                detail.Finish(true, $"整合包已导入：{import.PackRoot}");
                Completed?.Invoke(new DownloadCenterResult
                {
                    PackRoot = import.PackRoot,
                    ImportResult = import,
                });
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort.
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消下载。";
            detail.Finish(false, "已取消下载。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or
                                       InvalidOperationException or CurseForgeApiException or ModrinthApiException or
                                       MissingApiKeyException)
        {
            StatusText = $"下载失败：{ex.Message}";
            detail.Finish(false, ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
            if (ReferenceEquals(_operationCts, cts))
            {
                _operationCts = null;
                cts.Dispose();
            }
        }
    }

    [RelayCommand]
    private async Task BrowseDestinationAsync()
    {
        var folder = await _pickFolder();
        if (!string.IsNullOrWhiteSpace(folder))
            DestinationFolder = folder;
    }

    [RelayCommand]
    private void Cancel()
    {
        foreach (var item in _queue.Snapshot())
            if (item.State is DownloadQueueState.Queued or DownloadQueueState.Running or DownloadQueueState.Paused)
                _queue.Cancel(item);
        Completed?.Invoke(string.IsNullOrWhiteSpace(_lastDownloadedPath) ? null : BuildResult());
    }

    private static string ProjectKey(DownloadVersion version) =>
        $"{version.Source}:{version.ProjectId}";

    private DownloadCenterResult BuildResult(string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(path)) _lastDownloadedPath = path;
        return new DownloadCenterResult
        {
            ModFilePath = _lastDownloadedPath,
            LastDownloadedProjectId = _lastDownloadedProjectId,
            DownloadedProjectIds = new HashSet<string>(_downloadedProjectIds,
                StringComparer.OrdinalIgnoreCase),
        };
    }

    public void Dispose()
    {
        _queue.Changed -= OnQueueChanged;
        foreach (var item in _queue.Snapshot())
            if (item.State is DownloadQueueState.Queued or DownloadQueueState.Running or DownloadQueueState.Paused)
                _queue.Cancel(item);
    }

    private void UpdateTargetText()
    {
        TargetText = IsModpack
            ? $"导入到：{DestinationFolder}"
            : string.IsNullOrWhiteSpace(_context.PackRoot)
                ? "尚未打开整合包"
                : $"下载到：{Path.Combine(_context.PackRoot, "mods")}";
    }

    private static string DefaultDownloadsFolder()
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
