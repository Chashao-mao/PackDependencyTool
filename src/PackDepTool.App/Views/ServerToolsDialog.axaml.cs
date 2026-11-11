using Avalonia.Controls;
using PackDepTool.App.ViewModels;

namespace PackDepTool.App.Views;

public partial class ServerToolsDialog : Window
{
    private ServerToolsViewModel? _viewModel;

    public ServerToolsDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.CloseRequested -= OnCloseRequested;

        _viewModel = DataContext as ServerToolsViewModel;
        if (_viewModel is not null)
            _viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested() => Close();
}
