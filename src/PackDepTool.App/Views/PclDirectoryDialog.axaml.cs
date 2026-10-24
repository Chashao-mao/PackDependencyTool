using Avalonia.Controls;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Scanning;

namespace PackDepTool.App.Views;

public partial class PclDirectoryDialog : Window
{
    private PclDirectoryPickerViewModel? _viewModel;

    public PclDirectoryDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
            _viewModel.Completed -= OnCompleted;

        _viewModel = DataContext as PclDirectoryPickerViewModel;
        if (_viewModel is not null)
            _viewModel.Completed += OnCompleted;
    }

    private void OnCompleted(PclGameDirectoryCandidate? result) => Close(result);
}
