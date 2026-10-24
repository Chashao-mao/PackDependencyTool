using Avalonia.Controls;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Net;

namespace PackDepTool.App.Views;

public partial class LoaderSwitchDialog : Window
{
    private LoaderSwitchViewModel? _viewModel;

    public LoaderSwitchDialog()
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
            _viewModel.Completed -= OnCompleted;

        _viewModel = DataContext as LoaderSwitchViewModel;
        if (_viewModel is not null)
            _viewModel.Completed += OnCompleted;
    }

    private void OnCompleted(GameInstallResult? result) => Close(result);
}
