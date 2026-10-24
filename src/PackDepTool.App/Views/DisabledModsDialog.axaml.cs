using Avalonia.Controls;
using PackDepTool.App.ViewModels;

namespace PackDepTool.App.Views;

public partial class DisabledModsDialog : Window
{
    private DisabledModsViewModel? _viewModel;

    public DisabledModsDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
            _viewModel.Completed -= OnCompleted;

        _viewModel = DataContext as DisabledModsViewModel;
        if (_viewModel is not null)
            _viewModel.Completed += OnCompleted;
    }

    private void OnCompleted(DisabledModsResult? result) => Close(result);
}
