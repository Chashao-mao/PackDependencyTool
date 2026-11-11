using Avalonia.Controls;
using Avalonia.Threading;
using PackDepTool.App.ViewModels;

namespace PackDepTool.App.Views;

public partial class LogTailDialog : Window
{
    private LogTailViewModel? _viewModel;
    private DispatcherTimer? _timer;

    public LogTailDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => RefreshLog();
            _timer.Start();
        };
        Closed += (_, _) => _timer?.Stop();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel = DataContext as LogTailViewModel;
        if (_viewModel is not null)
            _viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested() => Close();

    private void RefreshLog()
    {
        if (_viewModel is null) return;
        _viewModel.Refresh();
        if (_viewModel.IsAutoScroll)
            Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}
