using Avalonia.Controls;
using Avalonia.Interactivity;
using PackDepTool.App.ViewModels;

namespace PackDepTool.App.Views;

public partial class HealthWizardDialog : Window
{
    public HealthWizardDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(HealthWizardAction.Close);
    private void OnMissingClick(object? sender, RoutedEventArgs e) => Close(HealthWizardAction.Missing);
    private void OnConflictClick(object? sender, RoutedEventArgs e) => Close(HealthWizardAction.Conflict);
    private void OnBackupClick(object? sender, RoutedEventArgs e) => Close(HealthWizardAction.Backup);
    private void OnWorkspaceClick(object? sender, RoutedEventArgs e) => Close(HealthWizardAction.Workspace);
}
