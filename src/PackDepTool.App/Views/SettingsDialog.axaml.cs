using Avalonia.Controls;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Persistence;

namespace PackDepTool.App.Views;

public partial class SettingsDialog : Window
{
    private SettingsViewModel? _viewModel;

    public SettingsDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
            _viewModel.Completed -= OnCompleted;

        _viewModel = DataContext as SettingsViewModel;

        if (_viewModel is not null)
            _viewModel.Completed += OnCompleted;
    }

    private void OnCompleted(AppSettings? settings) => Close(settings);
}
