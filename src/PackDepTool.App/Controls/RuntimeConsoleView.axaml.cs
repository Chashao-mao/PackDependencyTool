using Avalonia.Controls;
using Avalonia.Threading;
using PackDepTool.App.ViewModels;

namespace PackDepTool.App.Controls;

public partial class RuntimeConsoleView : UserControl
{
    private RuntimeConsoleViewModel? _viewModel;
    private DispatcherTimer? _timer;

    public RuntimeConsoleView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => StartTimer();
        DetachedFromVisualTree += (_, _) => StopTimer();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel = DataContext as RuntimeConsoleViewModel;
        if (_viewModel is not null) _viewModel.RefreshLog();
    }

    private void StartTimer()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void StopTimer() => _timer?.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.RefreshLog();
        if (_viewModel.AutoScroll && LogList.ItemCount > 0)
            Dispatcher.UIThread.Post(() => LogList.ScrollIntoView(LogList.ItemCount - 1),
                DispatcherPriority.Background);
    }
}
