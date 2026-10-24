using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PackDepTool.App.Services;
using PackDepTool.App.ViewModels;
using PackDepTool.App.Views;

namespace PackDepTool.App;

public partial class App : Application
{
    private AppServices? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = AppServices.Create();
            CrashLogger.Install(_services.Paths.LogsDirectory);
            var dialogs = new DialogService(
                _services.Paths, _services.Keys, _services.CurseForge, _services.Http,
                _services.Downloader, _services.Importer, _services.GameInstaller, _services.PclLocator,
                _services.Settings, _services.SettingsStore);
            var viewModel = new MainWindowViewModel(_services, dialogs);
            var window = new MainWindow { DataContext = viewModel };

            viewModel.AttachWindow(window);
            var args = desktop.Args ?? [];
            var startupPath = FindStartupPath(args);
            var openSettings = args.Any(arg => arg == "--settings");
            var addLinkFrom = args
                .FirstOrDefault(arg => arg.StartsWith("--add-link=", StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)[1];
            var downloadValue = args
                .FirstOrDefault(arg => arg.StartsWith("--download=", StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)[1];
            var downloadQuery = args
                .FirstOrDefault(arg => arg.StartsWith("--download-query=", StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)[1];
            var selectNodeId = args
                .FirstOrDefault(arg => arg.StartsWith("--select=", StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)[1];
            var exportPclPath = args
                .FirstOrDefault(arg => arg.StartsWith("--export-pcl=", StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)[1];
            var installGameSpec = args
                .FirstOrDefault(arg => arg.StartsWith("--install-game=", StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)[1];
            var openGameSetup = args.Any(arg => arg == "--game-setup");
            var openLoaderSwitch = args.Any(arg => arg == "--switch-loader");
            var openGraph = args.Any(arg => arg == "--graph");
            var openToolbox = args.Any(arg => arg == "--toolbox");
            window.AttachViewModel(
                viewModel, startupPath, openSettings, addLinkFrom,
                string.Equals(downloadValue, "pack", StringComparison.OrdinalIgnoreCase),
                string.Equals(downloadValue, "mod", StringComparison.OrdinalIgnoreCase),
                downloadQuery,
                selectNodeId,
                exportPclPath,
                openGameSetup,
                installGameSpec,
                openLoaderSwitch,
                openGraph,
                openToolbox);

            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) => _services?.Dispose();

        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? FindStartupPath(string[]? args)
    {
        if (args is null) return null;

        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-')) continue;

            var candidate = arg.Trim().Trim('"');
            if (Directory.Exists(candidate)) return candidate;

            if (File.Exists(candidate))
            {
                var directory = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrWhiteSpace(directory)) return directory;
            }
        }

        return null;
    }
}
