using Avalonia.Controls;
using Avalonia.Input;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Scanning;

namespace PackDepTool.App.Views;

public partial class InstanceManagerDialog : Window
{
    private InstanceManagerViewModel? _viewModel;
    private bool _initialized;

    public InstanceManagerDialog()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.Completed -= OnCompleted;

        _viewModel = DataContext as InstanceManagerViewModel;
        if (_viewModel is not null)
            _viewModel.Completed += OnCompleted;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_initialized || _viewModel is null) return;
        _initialized = true;
        await _viewModel.InitializeAsync();
    }

    private void OnCompleted(InstanceManagerResult? result) => Close(result);

    private void OnOpenSelectedClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel?.OpenSelected();

    private async void OnCopyInstanceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.CopySelectedAsync();
    }

    private async void OnDeleteInstanceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel?.Selected is not { } selected) return;
        var confirmed = await MessageDialog.ShowAsync(this, "删除实例",
            $"将 {selected.Name} 移到 versions/.packdep-trash，不会永久删除。继续吗？",
            "移到回收目录", "取消");
        if (confirmed) await _viewModel.DeleteSelectedAsync();
    }

    private void OnInstanceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && InstanceList.SelectedItem is MinecraftInstanceInfo instance)
            _viewModel.Selected = instance;
    }

    private void OnInstanceDoubleTapped(object? sender, TappedEventArgs e)
    {
        _viewModel?.OpenSelected();
        e.Handled = true;
    }
}
