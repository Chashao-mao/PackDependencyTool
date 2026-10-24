using Avalonia.Controls;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Models;

namespace PackDepTool.App.Views;

public partial class LocalRuleDialog : Window
{
    private LocalRuleDialogViewModel? _viewModel;

    public LocalRuleDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
            _viewModel.Completed -= OnCompleted;

        _viewModel = DataContext as LocalRuleDialogViewModel;

        if (_viewModel is not null)
            _viewModel.Completed += OnCompleted;
    }

    private void OnCompleted(LocalRule? rule) => Close(rule);
}
