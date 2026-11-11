using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PackDepTool.Core.Net;

namespace PackDepTool.App.ViewModels;

public sealed partial class ModInstallCandidate : ObservableObject
{
    public ModInstallCandidate(string query)
    {
        Query = query;
        DisplayName = query;
        Platform = "未解析";
        StatusText = "等待解析";
    }

    public string Query { get; }
    [ObservableProperty] private bool _include = true;
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _platform;
    [ObservableProperty] private string _versionText = "—";
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private bool _canDownload;

    public DownloadProject? Project { get; set; }
    public DownloadVersion? Version { get; set; }
}

public sealed partial class InstallListViewModel : ObservableObject
{
    private readonly Func<string, Task<ModInstallCandidate>>? _resolve;

    public InstallListViewModel(
        IEnumerable<ModInstallCandidate> candidates,
        Func<string, Task<ModInstallCandidate>>? resolve = null)
    {
        _resolve = resolve;
        foreach (var candidate in candidates)
        {
            candidate.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(ModInstallCandidate.Include) or
                    nameof(ModInstallCandidate.CanDownload))
                    Recalculate();
            };
            Items.Add(candidate);
        }
        Recalculate();
    }

    public ObservableCollection<ModInstallCandidate> Items { get; } = [];
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _statusText = "取消勾选可排除；也可以手动新增一行。";


    public IReadOnlyList<ModInstallCandidate> Selected =>
        Items.Where(item => item.Include && item.CanDownload).ToList();

    public void Recalculate()
    {
        SelectedCount = Selected.Count;
        StatusText = $"{Selected.Count} 个将安装 / 共 {Items.Count} 个";
    }

    public Func<string, Task<ModInstallCandidate>>? Resolver => _resolve;
}

