using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;

namespace PackDepTool.App.ViewModels;

public sealed partial class BisectionViewModel : ObservableObject
{
    private readonly BisectionPlanner _planner;

    public BisectionViewModel(GraphDocument graph)
    {
        _planner = new BisectionPlanner(graph);
        Refresh();
    }

    public ObservableCollection<ModNode> TestGroup { get; } = [];
    public ObservableCollection<ModNode> RequiredDependencies { get; } = [];

    [ObservableProperty] private string _instruction = string.Empty;
    [ObservableProperty] private string _stepText = string.Empty;
    [ObservableProperty] private bool _solved;
    [ObservableProperty] private bool _canReport;
    [ObservableProperty] private string _statusText = "按当前分组启动游戏，然后回报结果。";

    public event Action? CloseRequested;

    private void Refresh()
    {
        TestGroup.Clear();
        RequiredDependencies.Clear();
        foreach (var node in _planner.Current.Candidates)
            TestGroup.Add(node);
        foreach (var node in _planner.Current.RequiredDependencies)
            RequiredDependencies.Add(node);
        Solved = _planner.Current.Solved;
        CanReport = !Solved && TestGroup.Count > 0;
        Instruction = _planner.Current.Instruction;
        StepText = $"第 {_planner.Current.Step + 1} 轮";
        StatusText = Solved
            ? "已定位到单个模组；可查看详情或重新开始。"
            : "只生成分组，不自动移动文件。";
    }

    [RelayCommand]
    private void ReportCrashed()
    {
        _planner.ReportCrashed();
        Refresh();
    }

    [RelayCommand]
    private void ReportWorks()
    {
        _planner.ReportWorks();
        Refresh();
    }

    [RelayCommand]
    private void Reset()
    {
        _planner.Reset();
        Refresh();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
