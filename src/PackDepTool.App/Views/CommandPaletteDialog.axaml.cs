using Avalonia.Controls;
using Avalonia.Input;
using PackDepTool.App.ViewModels;

namespace PackDepTool.App.Views;

public partial class CommandPaletteDialog : Window
{
    private CommandPaletteViewModel? _viewModel;

    public CommandPaletteDialog()
    {
        InitializeComponent();
        Opened += (_, _) => PaletteSearch.Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel = DataContext as CommandPaletteViewModel;
        if (_viewModel is not null)
            _viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested() => Close();

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel?.ExecuteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        _viewModel?.ExecuteCommand.Execute(null);
        e.Handled = true;
    }
}
