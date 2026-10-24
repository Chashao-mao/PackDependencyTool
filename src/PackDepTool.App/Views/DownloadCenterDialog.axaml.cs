using Avalonia.Controls;
using Avalonia.Input;
using PackDepTool.App.ViewModels;

namespace PackDepTool.App.Views;

public partial class DownloadCenterDialog : Window
{
    private DownloadCenterViewModel? _viewModel;

    public DownloadCenterDialog()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (_viewModel is not null) await _viewModel.InitializeAsync();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.Completed -= OnCompleted;
            SearchBox.KeyDown -= OnSearchKeyDown;
        }

        _viewModel = DataContext as DownloadCenterViewModel;

        if (_viewModel is not null)
        {
            _viewModel.Completed += OnCompleted;
            SearchBox.KeyDown += OnSearchKeyDown;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_viewModel?.SearchCommand.CanExecute(null) == true)
            _viewModel.SearchCommand.Execute(null);
        e.Handled = true;
    }

    private void OnCompleted(DownloadCenterResult? result) => Close(result);
}
