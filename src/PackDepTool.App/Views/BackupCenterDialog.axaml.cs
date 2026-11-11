using Avalonia.Controls;
using PackDepTool.App.ViewModels;

namespace PackDepTool.App.Views;

public partial class BackupCenterDialog : Window
{
    private BackupCenterViewModel? _viewModel;

    public BackupCenterDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.Completed -= OnCompleted;

        _viewModel = DataContext as BackupCenterViewModel;
        if (_viewModel is not null)
            _viewModel.Completed += OnCompleted;
    }

    private void OnCompleted(BackupCenterResult? result) => Close(result);
}
