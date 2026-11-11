using Avalonia.Controls;
using Avalonia.Interactivity;
using PackDepTool.App.Services;
using PackDepTool.App.ViewModels;

namespace PackDepTool.App.Views;

public partial class InstallListDialog : Window
{
    private InstallListViewModel? _viewModel;

    public InstallListDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel = DataContext as InstallListViewModel;
    }

    public static async Task<List<ModInstallCandidate>?> ShowAsync(
        Window owner, InstallListViewModel viewModel)
    {
        var dialog = new InstallListDialog { DataContext = viewModel };
        return await dialog.ShowDialog<List<ModInstallCandidate>?>(owner);
    }

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.Resolver is not { } resolver) return;
        var query = await TextInputDialog.ShowAsync(this, "新增安装项",
            "输入 slug、名称或 Modrinth/CurseForge 项目 URL", string.Empty);
        if (string.IsNullOrWhiteSpace(query)) return;
        var candidate = await resolver(query.Trim());
        _viewModel.Items.Add(candidate);
        _viewModel.Recalculate();
    }

    private void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.Selected.Count == 0) return;
        Close(_viewModel.Selected.ToList());
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
