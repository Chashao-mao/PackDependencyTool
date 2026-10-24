using Avalonia.Controls;
using PackDepTool.App.ViewModels;

namespace PackDepTool.App.Views;

public partial class PackToolsDialog : Window
{
    private PackToolsViewModel? _viewModel;

    public PackToolsDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
            _viewModel.Completed -= OnCompleted;

        _viewModel = DataContext as PackToolsViewModel;
        if (_viewModel is not null)
            _viewModel.Completed += OnCompleted;
    }

    private void OnCompleted(PackToolsResult? result) => Close(result);
}
