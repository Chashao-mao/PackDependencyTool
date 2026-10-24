using Avalonia.Controls;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Models;

namespace PackDepTool.App.Views;

public partial class ManualLinkDialog : Window
{
    private ManualLinkDialogViewModel? _viewModel;

    public ManualLinkDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
            _viewModel.Completed -= OnCompleted;

        _viewModel = DataContext as ManualLinkDialogViewModel;

        if (_viewModel is not null)
            _viewModel.Completed += OnCompleted;
    }

    private void OnCompleted(ManualLink? link) => Close(link);
}
