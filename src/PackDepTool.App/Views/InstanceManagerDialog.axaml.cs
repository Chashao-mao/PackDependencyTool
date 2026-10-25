using Avalonia.Controls;
using Avalonia.Input;
using PackDepTool.App.ViewModels;

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

    private void OnInstanceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel?.Selected is null) return;
        _viewModel.OpenCommand.Execute(null);
        e.Handled = true;
    }
}
