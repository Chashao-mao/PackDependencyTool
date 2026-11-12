using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PackDepTool.App.ViewModels;

namespace PackDepTool.App.Controls;

public partial class RuntimeConsoleView : UserControl
{
    private RuntimeConsoleViewModel? _viewModel;
    private DispatcherTimer? _timer;
    private ScrollViewer? _scroll;
    private bool _stickToBottom = true;

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
        HookScroll();
    }

    private void HookScroll()
    {
        if (_scroll is not null) return;
        _scroll = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is null) return;
        _scroll.ScrollChanged += OnScrollChanged;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll is null) return;
        _stickToBottom = _scroll.Offset.Y + _scroll.Viewport.Height >= _scroll.Extent.Height - 24;
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
        HookScroll();
        _viewModel.RefreshLog();
        if (_viewModel.AutoScroll && _stickToBottom && LogList.ItemCount > 0)
            Dispatcher.UIThread.Post(() => LogList.ScrollIntoView(LogList.ItemCount - 1),
                DispatcherPriority.Background);
    }
}
