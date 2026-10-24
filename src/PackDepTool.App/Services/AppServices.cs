using System.Net;
using PackDepTool.Core.Net;
using PackDepTool.Core.Persistence;
using PackDepTool.Core.Scanning;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.History;

namespace PackDepTool.App.Services;

/// <summary>Composition root for the desktop app: paths, stores, clients and long-running services.</summary>
public sealed class AppServices : IDisposable
{
    private AppServices(
        AppPaths paths,
        AppSettings settings,
        SettingsStore settingsStore,
        ManualLinkStore manualLinks,
        LocalRuleStore localRules,
        ApiKeyProvider keys,
        HttpClient http,
        HttpClient downloadHttp,
        ModrinthClient modrinth,
        CurseForgeClient curseForge,
        ModEnrichmentService enrichment,
        string userAgent)
    {
        Paths = paths;
        Settings = settings;
        SettingsStore = settingsStore;
        ManualLinks = manualLinks;
        LocalRules = localRules;
        TranslationOverrides = new TranslationOverrideStore(paths);
        Keys = keys;
        Http = http;
        DownloadHttp = downloadHttp;
        Modrinth = modrinth;
        CurseForge = curseForge;
        Enrichment = enrichment;
        var apiCache = new ApiCacheStore(paths.ApiCacheDirectory);
        Downloader = new ModDownloadService(downloadHttp, modrinth, curseForge, userAgent, apiCache);
        Updater = new UpdateService(http, userAgent);
        DownloadQueue = new DownloadQueueService(new ResumableDownloader(downloadHttp, userAgent),
            settings.MaxParallelDownloads);
        Installer = new UpdateInstaller(http, userAgent);
        Importer = new ModpackImporter(Downloader, curseForge, keys);
        Icons = new ModIconService(http, modrinth, curseForge, userAgent, paths.IconsDirectory);
        PclExporter = new PclPackExportService(modrinth);
        GameInstaller = new MinecraftInstallService(http, Downloader, userAgent, paths.LoaderCacheDirectory,
            allowMirror: !string.Equals(settings.GameDownloadSource, "official", StringComparison.OrdinalIgnoreCase));
        PclLocator = new PclLocatorService();
        Audit = new PackAuditService();
        Categories = new ModCategoryClassifier();
        HistoryStore = new ModHistoryStore(paths);
        HistoryRecorder = new ModHistoryRecorder(HistoryStore);
        ModFiles = new ModFileManager();
        Resolver = new ModProjectResolver(modrinth, curseForge, keys);
        AutoCompleter = new DependencyAutoCompleter(Downloader, Resolver);
        Batch = new ModBatchService(Downloader, Resolver, ModFiles);
        Scanner = new ModScanner();
        Builder = new DependencyGraphBuilder();
        Layout = new GraphLayoutEngine();
        Analyzer = new GraphAnalyzer();
    }

    public AppPaths Paths { get; }
    public AppSettings Settings { get; }
    public SettingsStore SettingsStore { get; }
    public ManualLinkStore ManualLinks { get; }
    public LocalRuleStore LocalRules { get; }
    public TranslationOverrideStore TranslationOverrides { get; }
    public ApiKeyProvider Keys { get; }
    public HttpClient Http { get; }
    public HttpClient DownloadHttp { get; }
    public ModrinthClient Modrinth { get; }
    public CurseForgeClient CurseForge { get; }
    public ModEnrichmentService Enrichment { get; }
    public UpdateService Updater { get; }
    public UpdateInstaller Installer { get; }
    public DownloadQueueService DownloadQueue { get; }
    public ModDownloadService Downloader { get; }
    public ModpackImporter Importer { get; }
    public ModIconService Icons { get; }
    public PclPackExportService PclExporter { get; }
    public MinecraftInstallService GameInstaller { get; }
    public PclLocatorService PclLocator { get; }
    public PackAuditService Audit { get; }
    public ModCategoryClassifier Categories { get; }
    public ModHistoryStore HistoryStore { get; }
    public ModHistoryRecorder HistoryRecorder { get; }
    public ModFileManager ModFiles { get; }
    public ModProjectResolver Resolver { get; }
    public DependencyAutoCompleter AutoCompleter { get; }
    public ModBatchService Batch { get; }

    public ModScanner Scanner { get; }
    public DependencyGraphBuilder Builder { get; }
    public GraphLayoutEngine Layout { get; }
    public GraphAnalyzer Analyzer { get; }

    public static AppServices Create()
    {
        var paths = AppPaths.CreateDefault();
        paths.EnsureDirectories();

        var settingsStore = new SettingsStore(paths);
        var settings = settingsStore.Load();
        var secrets = SecretStoreFactory.Create(paths);
        var keys = new ApiKeyProvider(secrets);

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };

        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        var downloadHttp = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var userAgent = BuildUserAgent(settings.ModrinthContact);

        var modrinth = new ModrinthClient(http, userAgent,
            baseUrl: settings.ModrinthApiBaseUrl, cdnBase: settings.ModCdnBaseUrl);
        var cfBase = settings.CurseForgeApiBaseUrl;
        var curseForge = new CurseForgeClient(http, keys, userAgent,
            baseUrl: cfBase, cdnBase: settings.ModCdnBaseUrl,
            sendApiKey: string.IsNullOrWhiteSpace(cfBase));
        var enrichment = new ModEnrichmentService(modrinth, curseForge, keys);

        return new AppServices(
            paths, settings, settingsStore, new ManualLinkStore(paths), new LocalRuleStore(paths), keys, http,
            downloadHttp, modrinth, curseForge, enrichment, userAgent);
    }

    public static string BuildUserAgent(string? contact)
    {
        var suffix = string.IsNullOrWhiteSpace(contact) ? "local desktop app" : contact.Trim();
        return $"PackDependencyTool/1.0.0 ({suffix})";
    }

    public void SaveSettings()
    {
        SettingsStore.Save(Settings);
    }

    public void Dispose()
    {
        DownloadQueue.Dispose();
        DownloadHttp.Dispose();
        Http.Dispose();
    }
}
