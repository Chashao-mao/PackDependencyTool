using Avalonia.Controls;
using PackDepTool.App.ViewModels;

namespace PackDepTool.App.Views;

public partial class DiagnosticReportDialog : Window
{
    private DiagnosticReportViewModel? _viewModel;

    public DiagnosticReportDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.CloseRequested -= OnCloseRequested;

        _viewModel = DataContext as DiagnosticReportViewModel;
        if (_viewModel is not null)
            _viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested() => Close();
}
