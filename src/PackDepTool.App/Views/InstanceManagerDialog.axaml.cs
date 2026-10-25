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
