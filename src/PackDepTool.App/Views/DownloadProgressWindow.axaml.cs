using System.ComponentModel;
using Avalonia.Controls;
using PackDepTool.App.ViewModels;

namespace PackDepTool.App.Views;

public partial class DownloadProgressWindow : Window
{
    private DownloadProgressViewModel? _viewModel;

    public DownloadProgressWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.CancelRequested -= OnCancelRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as DownloadProgressViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CancelRequested += OnCancelRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnCancelRequested() => Close();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DownloadProgressViewModel.IsRunning)) return;
        if (_viewModel is { IsRunning: false })
            Topmost = false;
    }

    public static DownloadProgressWindow ShowFor(DownloadProgressViewModel viewModel)
    {
        var window = new DownloadProgressWindow
        {
            DataContext = viewModel,
            Topmost = true,
        };
        window.Show();
        window.Activate();
        return window;
    }
}
