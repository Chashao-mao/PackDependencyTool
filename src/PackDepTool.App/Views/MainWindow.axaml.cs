using System.Collections.Specialized;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;

namespace PackDepTool.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private string? _startupPath;
    private DispatcherTimer? _pendingDeselectTimer;
    private string? _railStatePath;

    public MainWindow()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        ModuleList.AddHandler(PointerPressedEvent, OnModuleListPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Loaded += (_, _) =>
        {
            LoadRailState();
            QueueDependencyRailOverlayUpdate();
            DispatcherTimer.RunOnce(QueueDependencyRailOverlayUpdate, TimeSpan.FromMilliseconds(300));
            DispatcherTimer.RunOnce(QueueDependencyRailOverlayUpdate, TimeSpan.FromMilliseconds(900));
        };
        ModuleList.SelectionChanged += (_, _) => QueueDependencyRailOverlayUpdate();
        ModuleList.AddHandler(ScrollViewer.ScrollChangedEvent,
            (_, _) => QueueDependencyRailOverlayUpdate(), RoutingStrategies.Bubble, handledEventsToo: true);
        Closing += (_, _) => _viewModel?.SaveWindowState(this);
    }

    public void AttachViewModel(
        MainWindowViewModel viewModel,
        string? startupPath,
        bool openSettings = false,
        string? addLinkFrom = null,
        bool openDownloadPack = false,
        bool openDownloadMod = false,
        string? downloadQuery = null,
        string? selectNodeId = null,
        string? exportPclPath = null,
        bool openGameSetup = false,
        string? installGameSpec = null,
        bool openLoaderSwitch = false,
        bool openGraph = false,
        bool openToolbox = false)
    {
        _viewModel = viewModel;
        _startupPath = startupPath;
        _railStatePath = Path.Combine(viewModel.DataDirectory, "rail-state.json");
        viewModel.AttachGraph(GraphView);
        viewModel.NodeItems.CollectionChanged += OnNodeItemsChanged;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.IsListView))
            {
                Dispatcher.UIThread.Post(ResetModuleListScroll, DispatcherPriority.Loaded);
                QueueDependencyRailOverlayUpdate();
            }
            if (args.PropertyName == nameof(MainWindowViewModel.PackPath))
            {
                LoadRailState();
                QueueDependencyRailOverlayUpdate();
            }
            if (args.PropertyName is nameof(MainWindowViewModel.SelectedNodeItem) or
                nameof(MainWindowViewModel.SelectedConnectionLineMode))
                QueueDependencyRailOverlayUpdate();
        };
        GraphView.QuickLinkToRequested += (fromId, toId, type) =>
            _ = viewModel.CreateManualLinkAsync(fromId, toId, type);
        _ = viewModel.InitializeAsync(
            startupPath, openSettings, addLinkFrom, openDownloadPack, openDownloadMod, downloadQuery,
            selectNodeId, exportPclPath, openGameSetup, installGameSpec, openLoaderSwitch, openGraph, openToolbox);
    }

    private void OnNodeItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _railCacheKey = null;
        QueueDependencyRailOverlayUpdate();
        DispatcherTimer.RunOnce(QueueDependencyRailOverlayUpdate, TimeSpan.FromMilliseconds(180));
        DispatcherTimer.RunOnce(QueueDependencyRailOverlayUpdate, TimeSpan.FromMilliseconds(500));
        Dispatcher.UIThread.Post(ResetModuleListScroll, DispatcherPriority.Loaded);
        DispatcherTimer.RunOnce(
            ResetModuleListScroll,
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background);
    }

    private void ResetModuleListScroll()
    {
        FilterScrollViewer.Offset = new Vector(0, 0);
        var scrollViewer = ModuleList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is not null)
            scrollViewer.Offset = new Vector(0, 0);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            CancelPendingDeselect();
            _viewModel?.ClearSelection();
            e.Handled = true;
        }
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => GraphView.ZoomIn();

    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => GraphView.ZoomOut();

    private void Fit_Click(object? sender, RoutedEventArgs e) => GraphView.FitToView(allowTiny: true);

    private void ModuleList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        CancelPendingDeselect();
        if (_viewModel?.SelectedNodeItem is { } item)
            _viewModel.ShowGraphAndFocus(item.Id);
    }

    private void SchedulePendingDeselect(NodeItemViewModel item)
    {
        CancelPendingDeselect();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_pendingDeselectTimer, timer)) return;
            _pendingDeselectTimer = null;
            if (_viewModel?.SelectedNodeItem is { } current &&
                string.Equals(current.Id, item.Id, StringComparison.OrdinalIgnoreCase))
            {
                _viewModel.ClearSelection();
                ModuleList.SelectedItem = null;
            }
        };
        _pendingDeselectTimer = timer;
        timer.Start();
    }

    private void CancelPendingDeselect()
    {
        _pendingDeselectTimer?.Stop();
        _pendingDeselectTimer = null;
    }

    private void OnModuleListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        var properties = e.GetCurrentPoint(ModuleList).Properties;
        var item = source.FindAncestorOfType<ListBoxItem>();

        if (properties.IsLeftButtonPressed)
        {
            if (source.FindAncestorOfType<CheckBox>() is not null ||
                source.FindAncestorOfType<Button>() is not null)
            {
                CancelPendingDeselect();
                return;
            }
            if (item?.DataContext is NodeItemViewModel clicked &&
                _viewModel?.SelectedNodeItem is { } selected &&
                string.Equals(clicked.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (e.ClickCount >= 2)
                    CancelPendingDeselect();
                else
                    SchedulePendingDeselect(clicked);
                return;
            }
            CancelPendingDeselect();
            return;
        }

        if (!properties.IsRightButtonPressed) return;
        CancelPendingDeselect();
        if (item?.DataContext is not NodeItemViewModel node || _viewModel is null) return;

        ModuleList.SelectedItem = node;
        ShowModuleContextMenu(node);
        e.Handled = true;
    }

    private void ShowModuleContextMenu(NodeItemViewModel node)
    {
        if (_viewModel is null) return;

        var menu = new ContextMenu();
        menu.Items.Add(TargetSubmenu("连接到必需依赖", EdgeType.Requires, node));
        menu.Items.Add(TargetSubmenu("连接到可选依赖", EdgeType.Optional, node));
        menu.Items.Add(TargetSubmenu("声明不兼容", EdgeType.Incompatible, node));
        menu.Items.Add(TargetSubmenu("连接到推荐依赖", EdgeType.Recommends, node));
        menu.Items.Add(new Separator());

        var sites = new MenuItem { Header = "打开站点" };
        sites.Items.Add(MenuItemFor("CurseForge", () => _viewModel.OpenCurseForgeForNode(node.Id)));
        sites.Items.Add(MenuItemFor("Modrinth", () => _viewModel.OpenModrinthForNode(node.Id)));
        sites.Items.Add(MenuItemFor("百科", () => _viewModel.OpenMcmodForNode(node.Id)));
        menu.Items.Add(sites);

        menu.Items.Add(MenuItemFor("设置中文名…", () => _viewModel.EditChineseNameCommand.Execute(null)));
        menu.Items.Add(MenuItemFor("下载此模组…", () => _viewModel.DownloadSelectedModCommand.Execute(null)));
        menu.Items.Add(MenuItemFor("禁用此模组…", () => _viewModel.DisableSelectedModCommand.Execute(null)));
        menu.Items.Add(MenuItemFor("添加手动关系（表单）…", () => _viewModel.AddManualLinkCommand.Execute(node.Id)));
        menu.Items.Add(MenuItemFor("在依赖图中查看", () => _viewModel.ShowGraphAndFocus(node.Id)));
        menu.Items.Add(MenuItemFor("复制 modId", () => _ = _viewModel.CopyNodeIdAsync(node.Id)));
        menu.Open(ModuleList);
    }

    private MenuItem TargetSubmenu(string header, EdgeType type, NodeItemViewModel source)
    {
        var menu = new MenuItem { Header = header };
        var targets = _viewModel?.GetManualLinkTargets(source.Id) ?? [];
        menu.IsEnabled = targets.Count > 0;

        foreach (var target in targets.Take(80))
        {
            var label = string.IsNullOrWhiteSpace(target.Name)
                ? target.Id
                : $"{target.Name}  ·  {target.Id}";
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => _ = _viewModel!.CreateManualLinkAsync(source.Id, target.Id, type);
            menu.Items.Add(item);
        }

        if (targets.Count > 80)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor("目标过多，使用表单选择…", () => _viewModel!.AddManualLinkCommand.Execute(source.Id)));
        }

        return menu;
    }

    private static MenuItem MenuItemFor(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel is null || !e.Data.Contains(DataFormats.Files)) return;

        var paths = e.Data.GetFiles()?
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        var archive = paths?.FirstOrDefault(path =>
            File.Exists(path) &&
            (Path.GetExtension(path).Equals(".mrpack", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)));
        if (archive is not null)
        {
            _ = _viewModel.ImportDroppedModpackAsync(archive);
            e.Handled = true;
            return;
        }

        var target = paths?.FirstOrDefault(path => Directory.Exists(path));
        if (target is not null)
            _ = _viewModel.OpenPathAsync(target);

        e.Handled = true;
    }

    private bool _updatingRailOverlay;
    private int _railUpdateVersion;
    private string? _railCacheKey;
    private IReadOnlyList<DependencyRailGroup> _railGroups = [];
    private readonly HashSet<string> _hiddenRailParents = new(StringComparer.OrdinalIgnoreCase);
    private string? _focusedRailParent;

    private void QueueDependencyRailOverlayUpdate()
    {
        // Scroll and collection changes fire repeatedly. Redraw at most once per
        // short interval instead of rebuilding the visual tree for every event.
        var version = ++_railUpdateVersion;
        DispatcherTimer.RunOnce(() =>
        {
            if (version != _railUpdateVersion) return;
            UpdateDependencyRailOverlay();
        }, TimeSpan.FromMilliseconds(45), DispatcherPriority.Background);
    }

    private void UpdateDependencyRailOverlay()
    {
        if (_updatingRailOverlay) return;
        _updatingRailOverlay = true;
        try
        {
            if (_viewModel is not { IsListView: true } viewModel ||
                GraphView.Scene is not { } scene ||
                viewModel.NodeItems.Count < 2)
            {
                DependencyRailOverlay.Children.Clear();
                return;
            }

            DependencyRailOverlay.Children.Clear();

            var items = viewModel.NodeItems;
            var orderedIds = items.Select(item => item.Id).ToList();
            var containers = ModuleList.GetVisualDescendants()
                .OfType<ListBoxItem>()
                .Where(container => container.DataContext is NodeItemViewModel)
                .Select(container => new
                {
                    Item = (NodeItemViewModel)container.DataContext!,
                    Container = container,
                })
                .GroupBy(value => value.Item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Container, StringComparer.OrdinalIgnoreCase);

            var mode = viewModel.SelectedConnectionLineMode.Value;
            var edgeHash = 17;
            foreach (var edge in scene.Edges)
                edgeHash = unchecked(edgeHash * 31 + edge.Model.Id.GetHashCode(StringComparison.OrdinalIgnoreCase));
            var edgeSignature = scene.Edges.Count + ":" + edgeHash;
            var cacheKey = string.Join('\0', orderedIds) + "|" + mode + "|" + edgeSignature;
            if (!string.Equals(_railCacheKey, cacheKey, StringComparison.Ordinal))
            {
                _railGroups = DependencyRailBuilder.Build(
                    orderedIds,
                    scene.Edges.Select(edge => edge.Model),
                    ToRailRelationMode(mode),
                    edge => viewModel.IsActiveConflict(edge));
                _railCacheKey = cacheKey;
            }

            var groups = _railGroups
                .Where(group => !_hiddenRailParents.Contains(group.ParentId))
                .Where(group => _focusedRailParent is null ||
                                string.Equals(group.ParentId, _focusedRailParent, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var selectedId = viewModel.SelectedNodeItem?.Id;

            foreach (var group in groups)
            {
                if (!containers.TryGetValue(group.ParentId, out var parentContainer)) continue;

                var parentPoint = parentContainer.TranslatePoint(
                    new Point(0, parentContainer.Bounds.Height / 2), DependencyRailOverlay);
                if (parentPoint is not { } parent) continue;

                var visibleChildren = new List<(Control Container, double Y)>();
                foreach (var index in group.ChildIndices)
                {
                    var id = items[index].Id;
                    if (!containers.TryGetValue(id, out var container)) continue;
                    var point = container.TranslatePoint(
                        new Point(0, container.Bounds.Height / 2), DependencyRailOverlay);
                    if (point is { } childPoint) visibleChildren.Add((container, childPoint.Y));
                }
                if (visibleChildren.Count == 0) continue;

                var laneX = parent.X + 66 + (group.VisualLane * 9);
                var endX = parent.X + 104;
                var minY = parent.Y;
                var maxY = parent.Y;
                foreach (var (_, y) in visibleChildren)
                {
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
                var isActive = selectedId is not null &&
                               (string.Equals(group.ParentId, selectedId, StringComparison.OrdinalIgnoreCase) ||
                                group.ChildIndices.Any(index =>
                                    string.Equals(items[index].Id, selectedId, StringComparison.OrdinalIgnoreCase)));
                var opacity = selectedId is null ? 0.9 : isActive ? 1.0 : 0.22;
                var strokeWidth = isActive ? 2.4 : 1.8;
                var brush = group.IsConflict
                    ? new SolidColorBrush(Color.Parse("#EF4444"))
                    : new SolidColorBrush(RailLaneColors[group.VisualLane % RailLaneColors.Length]);

                AddRailLine(laneX, minY, laneX, maxY, brush, strokeWidth, opacity);
                AddRailLine(laneX, parent.Y, endX, parent.Y, brush, strokeWidth, opacity);
                AddRailEndpoint(endX - 2, parent.Y, brush, opacity);

                foreach (var (_, y) in visibleChildren)
                {
                    AddRailLine(laneX, y, endX, y, brush, strokeWidth, opacity);
                    AddRailEndpoint(endX - 2, y, brush, opacity);
                }
            }
        }
        finally
        {
            _updatingRailOverlay = false;
        }
    }

    private void AddRailLine(double x1, double y1, double x2, double y2, IBrush brush, double width, double opacity) =>
        DependencyRailOverlay.Children.Add(new Avalonia.Controls.Shapes.Line
        {
            StartPoint = new Point(x1, y1),
            EndPoint = new Point(x2, y2),
            Stroke = brush,
            StrokeThickness = width,
            Opacity = opacity,
        });

    private void AddRailEndpoint(double x, double y, IBrush brush, double opacity)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 5, Height = 5, Fill = brush, Opacity = opacity };
        Canvas.SetLeft(dot, x - 2.5);
        Canvas.SetTop(dot, y - 2.5);
        DependencyRailOverlay.Children.Add(dot);
    }

    private static readonly Color[] RailLaneColors =
    [
        Color.Parse("#0EA5E9"),
        Color.Parse("#F59E0B"),
        Color.Parse("#8B5CF6"),
        Color.Parse("#10B981"),
    ];

    private static RailRelationMode ToRailRelationMode(ConnectionLineMode mode) => mode switch
    {
        ConnectionLineMode.Required => RailRelationMode.Required,
        ConnectionLineMode.RequiredOptional => RailRelationMode.RequiredOptional,
        _ => RailRelationMode.AllRelations,
    };

    private static IBrush EdgeBrush(EdgeType type) => new SolidColorBrush(type switch
    {
        EdgeType.Requires => GraphPalette.EdgeRequires,
        EdgeType.Incompatible => GraphPalette.EdgeIncompatible,
        EdgeType.Optional => GraphPalette.EdgeOptional,
        EdgeType.Embedded => GraphPalette.EdgeEmbedded,
        EdgeType.Recommends => GraphPalette.EdgeRecommends,
        EdgeType.Manual => GraphPalette.EdgeManual,
        _ => GraphPalette.EdgeOptional,
    });

    private async void IgnoreMissing_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { DataContext: NodeItemViewModel item }) return;
        await _viewModel.IgnoreMissingDependencyAsync(item);
    }

    private async void OptionalDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { DataContext: NodeItemViewModel item }) return;
        await _viewModel.DownloadOptionalNodeAsync(item.Model);
    }

    private void EmbeddedItem_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { DataContext: NodeItemViewModel item }) return;
        _viewModel.SelectNode(item.Id, center: false);
        e.Handled = true;
    }

    private bool _leftPanelVisible = true;
    private bool _rightPanelVisible = true;

    private void ToggleLeftPanel_Click(object? sender, RoutedEventArgs e)
    {
        _leftPanelVisible = !_leftPanelVisible;
        MainContentGrid.ColumnDefinitions[0].Width = new GridLength(_leftPanelVisible ? 300 : 0);
        MainContentGrid.ColumnDefinitions[1].Width = new GridLength(_leftPanelVisible ? 6 : 0);
        LeftPanelBorder.IsVisible = _leftPanelVisible;
        LeftPanelSplitter.IsVisible = _leftPanelVisible;
    }

    private void ToggleRightPanel_Click(object? sender, RoutedEventArgs e)
    {
        _rightPanelVisible = !_rightPanelVisible;
        MainContentGrid.ColumnDefinitions[3].Width = new GridLength(_rightPanelVisible ? 6 : 0);
        MainContentGrid.ColumnDefinitions[4].Width = new GridLength(_rightPanelVisible ? 400 : 0);
        RightPanelBorder.IsVisible = _rightPanelVisible;
        RightPanelSplitter.IsVisible = _rightPanelVisible;
    }

    private void ToggleLegend_Click(object? sender, RoutedEventArgs e)
    {
        LegendPanel.IsVisible = !LegendPanel.IsVisible;
        LegendToggleButton.Content = LegendPanel.IsVisible ? "图例 ▾" : "图例 ▸";
    }

    private void LoadRailState()
    {
        _hiddenRailParents.Clear();
        _focusedRailParent = null;
        if (_viewModel is null || string.IsNullOrWhiteSpace(_viewModel.PackPath) ||
            string.IsNullOrWhiteSpace(_railStatePath) || !File.Exists(_railStatePath))
            return;

        try
        {
            var states = JsonSerializer.Deserialize<Dictionary<string, RailStateDto>>(
                File.ReadAllText(_railStatePath));
            if (states is null) return;
            if (!states.TryGetValue(_viewModel.PackPath, out var state))
                states.TryGetValue(Path.GetFullPath(_viewModel.PackPath), out state);
            if (state is null) return;
            foreach (var id in state.Hidden.Where(id => !string.IsNullOrWhiteSpace(id)))
                _hiddenRailParents.Add(id);
            if (!string.IsNullOrWhiteSpace(state.Focus))
                _focusedRailParent = state.Focus;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A malformed preference file must never prevent the list from opening.
        }
    }

    private void SaveRailState()
    {
        if (_viewModel is null || string.IsNullOrWhiteSpace(_viewModel.PackPath) ||
            string.IsNullOrWhiteSpace(_railStatePath))
            return;

        try
        {
            var states = File.Exists(_railStatePath)
                ? JsonSerializer.Deserialize<Dictionary<string, RailStateDto>>(
                    File.ReadAllText(_railStatePath)) ?? []
                : [];
            states[_viewModel.PackPath] = new RailStateDto
            {
                Hidden = _hiddenRailParents.ToList(),
                Focus = _focusedRailParent,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_railStatePath)!);
            var temp = _railStatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(states,
                new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            File.Move(temp, _railStatePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Rail visibility is a convenience; do not surface save failures.
        }
    }

    private void RailManager_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItemFor("显示全部连线", () =>
        {
            _hiddenRailParents.Clear();
            _focusedRailParent = null;
            SaveRailState();
            QueueDependencyRailOverlayUpdate();
        }));

        var parents = _railGroups
            .Select(group => group.ParentId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => new
            {
                Id = id,
                Name = _viewModel.NodeItems.FirstOrDefault(item =>
                    string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? id,
            })
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (parents.Count > 0) menu.Items.Add(new Separator());
        foreach (var parent in parents)
        {
            var item = new MenuItem { Header = parent.Name };
            var hidden = _hiddenRailParents.Contains(parent.Id);
            var focused = string.Equals(_focusedRailParent, parent.Id, StringComparison.OrdinalIgnoreCase);
            item.Items.Add(MenuItemFor(hidden ? "恢复此线" : "隐藏此线", () =>
            {
                if (hidden) _hiddenRailParents.Remove(parent.Id);
                else _hiddenRailParents.Add(parent.Id);
                if (focused) _focusedRailParent = null;
                SaveRailState();
                QueueDependencyRailOverlayUpdate();
            }));
            item.Items.Add(MenuItemFor(focused ? "显示全部" : "仅显示此线", () =>
            {
                _focusedRailParent = focused ? null : parent.Id;
                SaveRailState();
                QueueDependencyRailOverlayUpdate();
            }));
            menu.Items.Add(item);
        }

        menu.Open(RailManagerButton);
    }

    private void DependencyLocate_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { DataContext: DependencyRowViewModel row }) return;
        var item = _viewModel.NodeItems.FirstOrDefault(node =>
            string.Equals(node.Id, row.TargetId, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        _viewModel.SelectedNodeItem = item;
        ModuleList.SelectedItem = item;
        ModuleList.ScrollIntoView(item);
        ModuleList.Focus();
        e.Handled = true;
    }
}

internal sealed class RailStateDto
{
    public List<string> Hidden { get; set; } = [];
    public string? Focus { get; set; }
}
