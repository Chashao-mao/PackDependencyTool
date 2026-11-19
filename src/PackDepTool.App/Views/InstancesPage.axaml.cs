using Avalonia.Controls;
using Avalonia.Input;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Scanning;

namespace PackDepTool.App.Views;

public partial class InstancesPage : UserControl
{
    private InstanceManagerViewModel? _viewModel;
    private bool _initialized;

    public InstancesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel = DataContext as InstanceManagerViewModel;
        _initialized = false;
        if (IsLoaded) _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (_initialized || _viewModel is null) return;
        _initialized = true;
        await _viewModel.InitializeAsync();
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = InitializeAsync();

    private void OnOpenSelectedClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel?.OpenSelected();

    private async void OnRenameInstanceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel?.Selected is not { } selected) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var name = await TextInputDialog.ShowAsync(owner, "重命名实例", "输入新的实例名", selected.Name);
        if (!string.IsNullOrWhiteSpace(name)) await _viewModel.RenameSelectedAsync(name);
    }

    private async void OnCopyInstanceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.CopySelectedAsync();
    }

    private async void OnDeleteInstanceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel?.Selected is not { } selected) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var confirmed = await MessageDialog.ShowAsync(owner, "删除实例",
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
