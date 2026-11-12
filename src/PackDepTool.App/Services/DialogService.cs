using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PackDepTool.App.ViewModels;
using PackDepTool.App.Views;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using PackDepTool.Core.Net;
using PackDepTool.Core.Persistence;
using PackDepTool.Core.Scanning;

namespace PackDepTool.App.Services;

public sealed class DialogService : IDialogService
{
    private readonly AppPaths _paths;
    private readonly ApiKeyProvider _keys;
    private readonly CurseForgeClient _curseForge;
    private readonly HttpClient _http;
    private readonly ModDownloadService _downloader;
    private readonly ModpackImporter _importer;
    private readonly MinecraftInstallService _gameInstaller;
    private readonly PclLocatorService _pclLocator;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly DownloadQueueService _downloadQueue;

    public DialogService(
        AppPaths paths,
        ApiKeyProvider keys,
        CurseForgeClient curseForge,
        HttpClient http,
        ModDownloadService downloader,
        ModpackImporter importer,
        MinecraftInstallService gameInstaller,
        PclLocatorService pclLocator,
        AppSettings settings,
        SettingsStore settingsStore,
        DownloadQueueService downloadQueue)
    {
        _paths = paths;
        _keys = keys;
        _curseForge = curseForge;
        _http = http;
        _downloader = downloader;
        _importer = importer;
        _gameInstaller = gameInstaller;
        _pclLocator = pclLocator;
        _settings = settings;
        _settingsStore = settingsStore;
        _downloadQueue = downloadQueue;
    }

    public Window? Owner { get; set; }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Owner?.StorageProvider is not { CanPickFolder: true } provider) return null;

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(
        string title, string suggestedFileName, string extension, string typeName)
    {
        if (Owner?.StorageProvider is not { CanSave: true } provider) return null;

        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType(typeName) { Patterns = [$"*.{extension}"] },
            ],
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenFileAsync(string title, string extension, string typeName) =>
        await PickOpenFileAsync(title, [extension], typeName);

    public async Task<string?> PickOpenFileAsync(string title, string[] extensions, string typeName)
    {
        if (Owner?.StorageProvider is not { CanOpen: true } provider) return null;

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(typeName) { Patterns = extensions.Select(extension => $"*.{extension}").ToList() },
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<DownloadCenterResult?> ShowDownloadCenterAsync(DownloadCenterContext context)
    {
        if (Owner is null) return null;

        var viewModel = new DownloadCenterViewModel(
            _downloader, _importer, _http, context, () => PickFolderAsync("选择整合包导入目录"),
            _downloadQueue);
        var dialog = new DownloadCenterDialog { DataContext = viewModel };
        dialog.Closed += (_, _) => viewModel.Dispose();
        return await dialog.ShowDialog<DownloadCenterResult?>(Owner);
    }

    public async Task<EnrichmentPlanResult?> ShowEnrichmentPlanAsync(EnrichmentPlan plan)
    {
        if (Owner is null) return null;

        var viewModel = new EnrichmentPlanViewModel(plan);
        var dialog = new EnrichmentPlanWindow { DataContext = viewModel };
        viewModel.Completed += result => dialog.Close(result);
        return await dialog.ShowDialog<EnrichmentPlanResult?>(Owner);
    }

    public async Task<GameInstallResult?> ShowGameSetupAsync()
    {
        if (Owner is null) return null;

        var viewModel = new GameSetupViewModel(
            _gameInstaller,
            _pclLocator,
            _settings,
            _settingsStore,
            () => PickFolderAsync("选择游戏实例保存目录"),
            () => PickOpenFileAsync("选择 java.exe", ["exe"], "Java 可执行文件"),
            candidates => ShowPclDirectoryPickerAsync(candidates));
        var dialog = new GameSetupDialog { DataContext = viewModel };
        return await dialog.ShowDialog<GameInstallResult?>(Owner);
    }

    public async Task<GameInstallResult?> ShowLoaderSwitchAsync(LoaderSwitchContext context)
    {
        if (Owner is null) return null;

        var viewModel = new LoaderSwitchViewModel(
            _gameInstaller,
            _pclLocator,
            _settings,
            _settingsStore,
            context,
            () => PickFolderAsync("选择要切换加载器的实例目录"),
            () => PickOpenFileAsync("选择 java.exe", ["exe"], "Java 可执行文件"),
            candidates => ShowPclDirectoryPickerAsync(candidates));
        var dialog = new LoaderSwitchDialog { DataContext = viewModel };
        return await dialog.ShowDialog<GameInstallResult?>(Owner);
    }

    public async Task<InstanceManagerResult?> ShowInstanceManagerAsync(
        IReadOnlyList<string> versionsDirectories, string? lastVersionsDirectory)
    {
        if (Owner is null) return null;

        var viewModel = new InstanceManagerViewModel(
            new InstanceCatalogService(),
            _pclLocator,
            versionsDirectories,
            lastVersionsDirectory,
            title => PickFolderAsync(title),
            candidates => ShowPclDirectoryPickerAsync(candidates));
        var dialog = new InstanceManagerDialog { DataContext = viewModel };
        return await dialog.ShowDialog<InstanceManagerResult?>(Owner);
    }

    public async Task<BackupCenterResult?> ShowBackupCenterAsync(
        string packRoot, ScanResult? scan = null, GraphDocument? graph = null)
    {
        if (Owner is null) return null;

        BackupCenterDialog? dialog = null;
        var viewModel = new BackupCenterViewModel(
            new PackBackupService(),
            packRoot,
            (title, message) => MessageDialog.ShowAsync(dialog!, title, message, "继续", "取消"),
            new ModListHistoryService(), scan, graph,
            entry => DownloadHistoryEntryAsync(packRoot, scan, entry));
        dialog = new BackupCenterDialog { DataContext = viewModel };
        return await dialog.ShowDialog<BackupCenterResult?>(Owner);
    }

    private async Task<string?> DownloadHistoryEntryAsync(
        string packRoot, ScanResult? scan, ModListSnapshotEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.FilePath)) return null;
        var destination = Path.GetFullPath(Path.Combine(packRoot, entry.FilePath));
        if (!destination.StartsWith(Path.GetFullPath(packRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return null;

        var query = !string.IsNullOrWhiteSpace(entry.Id) ? entry.Id : entry.Name;
        var projects = await _downloader.SearchAsync(DownloadSource.All, DownloadProjectType.Mod, query);
        var project = projects.FirstOrDefault(project =>
            string.Equals(Normalize(project.Slug), Normalize(query), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Normalize(project.Name), Normalize(entry.Name), StringComparison.OrdinalIgnoreCase));
        if (project is null) return null;

        var versions = await _downloader.GetVersionsAsync(
            project, scan?.Profile.McVersion, scan?.Profile.Loader ?? LoaderKind.Unknown);
        var version = versions.FirstOrDefault(candidate =>
            (!string.IsNullOrWhiteSpace(entry.Sha1) &&
             string.Equals(candidate.Sha1, entry.Sha1, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(entry.Version) &&
             string.Equals(candidate.VersionNumber, entry.Version, StringComparison.OrdinalIgnoreCase)));
        if (version is null) return null;

        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var result = await _downloader.DownloadAsync(version, directory);
        if (string.Equals(result.FilePath, destination, StringComparison.OrdinalIgnoreCase))
            return destination;

        File.Move(result.FilePath, destination, overwrite: true);
        return destination;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public async Task ShowDiagnosticReportAsync(DiagnosticReport report)
    {
        if (Owner is null) return;

        var viewModel = new DiagnosticReportViewModel(report);
        var dialog = new DiagnosticReportDialog { DataContext = viewModel };
        await dialog.ShowDialog(Owner);
    }

    public async Task ShowServerToolsAsync(string packRoot, GraphDocument graph)
    {
        if (Owner is null) return;

        var viewModel = new ServerToolsViewModel(
            packRoot,
            graph,
            Path.Combine(packRoot, "server-pack"),
            () => PickFolderAsync("选择服务端包输出目录"),
            () => PickOpenFileAsync("选择 java.exe", ["exe"], "Java 可执行文件"));
        var dialog = new ServerToolsDialog { DataContext = viewModel };
        await dialog.ShowDialog(Owner);
    }

    public async Task ShowBisectionAsync(GraphDocument graph)
    {
        if (Owner is null) return;
        var viewModel = new BisectionViewModel(graph);
        var dialog = new BisectionDialog { DataContext = viewModel };
        await dialog.ShowDialog(Owner);
    }

    public async Task ShowCommandPaletteAsync(IReadOnlyList<CommandPaletteItem> items)
    {
        if (Owner is null) return;
        var viewModel = new CommandPaletteViewModel(items);
        var dialog = new CommandPaletteDialog { DataContext = viewModel };
        await dialog.ShowDialog(Owner);
    }

    public async Task<string?> ShowTextInputAsync(
        string title, string message, string label, string initialValue)
    {
        if (Owner is null) return null;
        var prompt = string.IsNullOrWhiteSpace(message) ? label : $"{message}\n{label}";
        return await TextInputDialog.ShowAsync(Owner, title, prompt, initialValue);
    }

    public async Task<HealthWizardAction> ShowHealthWizardAsync(PackAuditReport report)
    {
        if (Owner is null) return HealthWizardAction.Close;
        var dialog = new HealthWizardDialog { DataContext = new HealthWizardViewModel(report) };
        return await dialog.ShowDialog<HealthWizardAction>(Owner);
    }

    public async Task<List<ModInstallCandidate>?> ShowInstallListAsync(InstallListViewModel viewModel)
    {
        if (Owner is null) return null;
        return await InstallListDialog.ShowAsync(Owner, viewModel);
    }

    public async Task<string?> ShowModListImportAsync()
    {
        if (Owner is null) return null;
        return await ModListImportDialog.ShowAsync(Owner);
    }

    public async Task ShowLogTailAsync(string logPath)
    {
        if (Owner is null) return;
        var viewModel = new LogTailViewModel(logPath);
        var dialog = new LogTailDialog { DataContext = viewModel };
        await dialog.ShowDialog(Owner);
    }

    public async Task<string?> ReadClipboardTextAsync()
    {
        if (Owner?.Clipboard is null) return null;
        try
        {
            return await Owner.Clipboard.GetTextAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    public async Task WriteClipboardTextAsync(string text)
    {
        if (Owner?.Clipboard is null) throw new InvalidOperationException("剪贴板不可用。");
        await Owner.Clipboard.SetTextAsync(text);
    }

    public async Task<PclGameDirectoryCandidate?> ShowPclDirectoryPickerAsync(
        IReadOnlyList<PclGameDirectoryCandidate> candidates)
    {
        if (Owner is null) return null;

        var viewModel = new PclDirectoryPickerViewModel(candidates);
        var dialog = new PclDirectoryDialog { DataContext = viewModel };
        return await dialog.ShowDialog<PclGameDirectoryCandidate?>(Owner);
    }

    public async Task<DisabledModsResult?> ShowDisabledModsAsync(IReadOnlyList<DisabledModFile> files)
    {
        if (Owner is null) return null;

        var viewModel = new DisabledModsViewModel(files);
        var dialog = new DisabledModsDialog { DataContext = viewModel };
        return await dialog.ShowDialog<DisabledModsResult?>(Owner);
    }

    public async Task<PackToolsResult?> ShowPackToolsAsync(PackAuditReport report)
    {
        if (Owner is null) return null;

        var viewModel = new PackToolsViewModel(report);
        var dialog = new PackToolsDialog { DataContext = viewModel };
        return await dialog.ShowDialog<PackToolsResult?>(Owner);
    }

    public async Task<ManualLink?> ShowManualLinkAsync(
        ManualLink? existing,
        IReadOnlyList<string> modIds,
        string? defaultFrom = null)
    {
        if (Owner is null) return null;

        var viewModel = new ManualLinkDialogViewModel(existing, modIds, defaultFrom);
        var dialog = new ManualLinkDialog { DataContext = viewModel };
        return await dialog.ShowDialog<ManualLink?>(Owner);
    }

    public async Task<AppSettings?> ShowSettingsAsync(AppSettings current)
    {
        if (Owner is null) return null;

        var hasStoredKey = await _keys.HasStoredKeyAsync();
        var viewModel = new SettingsViewModel(current, _paths, _keys, _curseForge, hasStoredKey);
        var dialog = new SettingsDialog { DataContext = viewModel };
        return await dialog.ShowDialog<AppSettings?>(Owner);
    }

    public async Task<LocalRule?> ShowLocalRuleAsync(LocalRule? existing, IReadOnlyList<string> modIds)
    {
        if (Owner is null) return null;

        var viewModel = new LocalRuleDialogViewModel(existing, modIds);
        var dialog = new LocalRuleDialog { DataContext = viewModel };
        return await dialog.ShowDialog<LocalRule?>(Owner);
    }

    public async Task<bool> ConfirmAsync(
        string title, string message, string okText = "确定", string cancelText = "取消")
    {
        if (Owner is null) return false;
        return await MessageDialog.ShowAsync(Owner, title, message, okText, cancelText);
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        if (Owner is null) return;
        await MessageDialog.ShowAsync(Owner, title, message, "确定");
    }
}
