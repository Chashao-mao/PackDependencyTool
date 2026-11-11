using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;

namespace PackDepTool.App.ViewModels;

public sealed class ServerPackEntryItem
{
    public ServerPackEntryItem(ServerPackEntry entry)
    {
        Model = entry;
        StateText = entry.Included ? "保留" : "排除";
        StateBrush = new SolidColorBrush(entry.Included
            ? GraphPalette.Success
            : GraphPalette.EdgeIncompatible);
    }

    public ServerPackEntry Model { get; }
    public string Name => Model.Node.Name;
    public string Id => Model.Node.Id;
    public string FileName => Model.FileName;
    public string SideText => Model.Node.Side switch
    {
        ModSide.Client => "客户端",
        ModSide.Server => "服务端",
        ModSide.Both => "双端",
        _ => "未知",
    };
    public string StateText { get; }
    public IBrush StateBrush { get; }
    public string Reason => Model.Reason;
}

public sealed class ServerGateItem
{
    public ServerGateItem(ServerGateCheck check)
    {
        Model = check;
        StatusText = check.Status switch
        {
            GateStatus.Pass => "通过",
            GateStatus.Warn => "注意",
            _ => "失败",
        };
        StatusBrush = new SolidColorBrush(check.Status switch
        {
            GateStatus.Pass => GraphPalette.Success,
            GateStatus.Warn => GraphPalette.Warning,
            _ => GraphPalette.EdgeIncompatible,
        });
    }

    public ServerGateCheck Model { get; }
    public string Name => Model.Name;
    public string Message => Model.Message;
    public string Fix => Model.Fix;
    public string StatusText { get; }
    public IBrush StatusBrush { get; }
}

public sealed partial class ServerToolsViewModel : ObservableObject
{
    private readonly ServerPackPlan _plan;
    private readonly ServerPackService _service;
    private readonly ServerPreflightService _preflight;
    private readonly string _packRoot;
    private readonly Func<Task<string?>> _pickDestination;
    private readonly Func<Task<string?>> _pickJava;

    public ServerToolsViewModel(
        string packRoot,
        GraphDocument graph,
        string? defaultDestination,
        Func<Task<string?>> pickDestination,
        Func<Task<string?>> pickJava)
    {
        _packRoot = packRoot;
        _service = new ServerPackService();
        _preflight = new ServerPreflightService();
        _pickDestination = pickDestination;
        _pickJava = pickJava;
        _plan = _service.CreatePlan(graph);
        foreach (var entry in _plan.Entries)
            Entries.Add(new ServerPackEntryItem(entry));
        _destination = defaultDestination ?? Path.Combine(packRoot, "server-pack");
        RefreshGate();
    }

    public ObservableCollection<ServerPackEntryItem> Entries { get; } = [];
    public ObservableCollection<ServerGateItem> GateChecks { get; } = [];

    [ObservableProperty] private string _destination;
    [ObservableProperty] private string _javaPath = string.Empty;
    [ObservableProperty] private bool _includeConfigs = true;
    [ObservableProperty] private bool _createZip = true;
    [ObservableProperty] private string _statusText = "准备生成服务端包";
    [ObservableProperty] private bool _isBusy;

    public string SummaryText =>
        $"保留 {_plan.IncludedCount} · 排除客户端 {_plan.ClientOnlyCount} · 未知侧 {_plan.UnknownSideCount}";

    public event Action? CloseRequested;

    [RelayCommand]
    private async Task BrowseDestinationAsync()
    {
        var path = await _pickDestination();
        if (!string.IsNullOrWhiteSpace(path)) Destination = path;
    }

    [RelayCommand]
    private async Task PickJavaAsync()
    {
        var path = await _pickJava();
        if (!string.IsNullOrWhiteSpace(path))
        {
            JavaPath = path;
            RefreshGate();
        }
    }

    [RelayCommand]
    private void RefreshGate()
    {
        GateChecks.Clear();
        foreach (var check in _preflight.Check(_packRoot, JavaPath))
            GateChecks.Add(new ServerGateItem(check));
    }

    [RelayCommand]
    private async Task BuildAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在生成服务端包…";
        try
        {
            var result = await Task.Run(() => _service.Build(
                _plan, _packRoot, Destination, IncludeConfigs,
                maximumRamMb: 4096, createZip: CreateZip));
            StatusText = $"已生成：{result.CopiedMods} 个模组，{result.CopiedConfigFiles} 个配置" +
                         (result.ZipPath is not null ? $"，zip：{Path.GetFileName(result.ZipPath)}" : string.Empty) +
                         (result.Failures.Count > 0 ? $"，{result.Failures.Count} 个失败" : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = $"生成失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
