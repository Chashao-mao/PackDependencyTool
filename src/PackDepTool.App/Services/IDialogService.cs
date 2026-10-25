using Avalonia.Controls;
using PackDepTool.App.ViewModels;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using PackDepTool.Core.Net;
using PackDepTool.Core.Persistence;
using PackDepTool.Core.Scanning;

namespace PackDepTool.App.Services;

public interface IDialogService
{
    Window? Owner { get; set; }

    Task<string?> PickFolderAsync(string title);
    Task<string?> PickSaveFileAsync(string title, string suggestedFileName, string extension, string typeName);
    Task<string?> PickOpenFileAsync(string title, string extension, string typeName);
    Task<string?> PickOpenFileAsync(string title, string[] extensions, string typeName);

    Task<DownloadCenterResult?> ShowDownloadCenterAsync(DownloadCenterContext context);
    Task<EnrichmentPlanResult?> ShowEnrichmentPlanAsync(EnrichmentPlan plan);
    Task<GameInstallResult?> ShowGameSetupAsync();
    Task<GameInstallResult?> ShowLoaderSwitchAsync(LoaderSwitchContext context);
    Task<InstanceManagerResult?> ShowInstanceManagerAsync(string? versionsDirectory);
    Task<PclGameDirectoryCandidate?> ShowPclDirectoryPickerAsync(
        IReadOnlyList<PclGameDirectoryCandidate> candidates);

    Task<DisabledModsResult?> ShowDisabledModsAsync(IReadOnlyList<DisabledModFile> files);

    Task<PackToolsResult?> ShowPackToolsAsync(PackAuditReport report);

    Task<ManualLink?> ShowManualLinkAsync(
        ManualLink? existing,
        IReadOnlyList<string> modIds,
        string? defaultFrom = null);

    Task<AppSettings?> ShowSettingsAsync(AppSettings current);

    Task<LocalRule?> ShowLocalRuleAsync(LocalRule? existing, IReadOnlyList<string> modIds);

    Task<string?> ShowTextInputAsync(
        string title,
        string message,
        string label,
        string initialValue);

    Task<bool> ConfirmAsync(string title, string message, string okText = "确定", string cancelText = "取消");
    Task ShowMessageAsync(string title, string message);
}
