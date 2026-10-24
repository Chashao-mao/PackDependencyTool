using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace PackDepTool.Core.Net;

/// <summary>
/// Downloads a vanilla Minecraft client installation and installs a loader into an
/// isolated game directory. It deliberately does not launch the game or handle accounts.
/// </summary>
public sealed class MinecraftInstallService
{
    private const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string ResourcesBase = "https://resources.download.minecraft.net/";
    private const long MaxInstallerBytes = 256L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ModDownloadService _downloader;
    private readonly string _userAgent;
    private readonly string _loaderCacheDirectory;
    private readonly bool _allowMirror;

    public MinecraftInstallService(
        HttpClient http,
        ModDownloadService downloader,
        string userAgent,
        string loaderCacheDirectory,
        bool allowMirror = true)
    {
        _http = http;
        _downloader = downloader;
        _userAgent = userAgent;
        _loaderCacheDirectory = loaderCacheDirectory;
        _allowMirror = allowMirror;
    }

    public async Task<List<MinecraftVersionInfo>> GetMinecraftVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        using var doc = await GetJsonWithFallbackAsync(
                VersionManifestUrl,
                "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
                cancellationToken)
            .ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("versions", out var versions) ||
            versions.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<MinecraftVersionInfo>();
        foreach (var item in versions.EnumerateArray())
        {
            var id = GetString(item, "id");
            var url = GetString(item, "url");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url)) continue;

            result.Add(new MinecraftVersionInfo
            {
                Id = id!,
                Type = GetString(item, "type") ?? "release",
                Url = url!,
                ReleaseTime = GetDateTime(item, "releaseTime"),
            });
        }

        return result
            .OrderByDescending(version => version.ReleaseTime)
            .ToList();
    }

    public async Task<List<LoaderVersionInfo>> GetLoaderVersionsAsync(
        string minecraftVersion,
        GameLoader loader,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (loader == GameLoader.Vanilla) return [];

        var cachePath = LoaderCachePath(loader, minecraftVersion);
        if (!forceRefresh)
        {
            var cached = TryReadLoaderCache(cachePath);
            if (cached is { Count: > 0 }) return cached;
        }

        var versions = await FetchLoaderVersionsAsync(minecraftVersion, loader, cancellationToken)
            .ConfigureAwait(false);
        if (versions.Count > 0) TryWriteLoaderCache(cachePath, versions);
        return versions;
    }

    private async Task<List<LoaderVersionInfo>> FetchLoaderVersionsAsync(
        string minecraftVersion,
        GameLoader loader,
        CancellationToken cancellationToken)
    {
        try
        {
            if (loader is GameLoader.Fabric or GameLoader.Quilt)
            {
                var baseUrl = loader == GameLoader.Fabric
                    ? "https://meta.fabricmc.net/v2"
                    : "https://meta.quiltmc.org/v3";
                var mirrorUrl = loader == GameLoader.Fabric
                    ? $"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}"
                    : null;
                var url = $"{baseUrl}/versions/loader/{Uri.EscapeDataString(minecraftVersion)}";
                using var doc = await GetJsonWithFallbackAsync(url, mirrorUrl, cancellationToken)
                    .ConfigureAwait(false);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

                var versions = new List<LoaderVersionInfo>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("loader", out var loaderElement) &&
                        loaderElement.ValueKind == JsonValueKind.Object)
                    {
                        var version = GetString(loaderElement, "version");
                        if (string.IsNullOrWhiteSpace(version)) continue;
                        versions.Add(new LoaderVersionInfo
                        {
                            Version = version!,
                            Stable = loaderElement.TryGetProperty("stable", out var stable) &&
                                     stable.ValueKind == JsonValueKind.True,
                        });
                    }
                }

                return SortVersions(versions);
            }

            if (loader == GameLoader.Forge)
            {
                const string url = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
                const string mirror = "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/maven-metadata.xml";
                var xml = await GetStringWithFallbackAsync(url, mirror, cancellationToken).ConfigureAwait(false);
                var doc = XDocument.Parse(xml);
                var prefix = minecraftVersion + "-";
                var versions = doc.Descendants("version")
                    .Select(element => element.Value)
                    .Where(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(value => value[prefix.Length..])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(value => new LoaderVersionInfo { Version = value })
                    .ToList();
                return SortVersions(versions);
            }

            if (loader == GameLoader.NeoForge)
            {
                const string url = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
                const string mirror = "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/maven-metadata.xml";
                var xml = await GetStringWithFallbackAsync(url, mirror, cancellationToken).ConfigureAwait(false);
                var doc = XDocument.Parse(xml);
                var prefix = NeoForgePrefix(minecraftVersion);
                var versions = doc.Descendants("version")
                    .Select(element => element.Value)
                    .Where(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(value => new LoaderVersionInfo { Version = value })
                    .ToList();
                return SortVersions(versions);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or System.Xml.XmlException or
                                       OperationCanceledException)
        {
            return [];
        }

        return [];
    }

    private string LoaderCachePath(GameLoader loader, string minecraftVersion)
    {
        var safe = new string($"{loader}-{minecraftVersion}"
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')
            .ToArray());
        return Path.Combine(_loaderCacheDirectory, safe + ".json");
    }

    private static List<LoaderVersionInfo>? TryReadLoaderCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(30)) return null;
            var values = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            return values?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => new LoaderVersionInfo { Version = value })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TryWriteLoaderCache(string path, List<LoaderVersionInfo> versions)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(versions.Select(version => version.Version).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache is best effort.
        }
    }

    public async Task<GameInstallResult> InstallAsync(
        GameInstallRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TargetDirectory))
            throw new InvalidOperationException("请选择实例保存目录。");

        var instanceDirectory = request.UseTargetAsInstance
            ? request.TargetDirectory
            : EnsureUniqueDirectory(request.TargetDirectory, BuildInstanceName(request));

        return await InstallIntoInstanceAsync(instanceDirectory, request, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Installs a new loader into an existing instance without touching mods/configs.
    /// Vanilla files are reused when present; only missing files are downloaded.
    /// </summary>
    public async Task<GameInstallResult> SwitchLoaderAsync(
        string instanceDirectory,
        GameInstallRequest options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(instanceDirectory))
            throw new InvalidOperationException("实例目录不存在。");
        if (string.IsNullOrWhiteSpace(options.MinecraftVersion))
            throw new InvalidOperationException("缺少 Minecraft 版本。");

        return await InstallIntoInstanceAsync(instanceDirectory, options, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GameInstallResult> InstallIntoInstanceAsync(
        string instanceDirectory,
        GameInstallRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.MinecraftVersion))
            throw new InvalidOperationException("请选择 Minecraft 版本。");

        var manifest = await GetMinecraftVersionsAsync(cancellationToken).ConfigureAwait(false);
        var version = manifest.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, request.MinecraftVersion, StringComparison.OrdinalIgnoreCase));
        if (version is null)
            throw new InvalidOperationException($"找不到 Minecraft 版本 {request.MinecraftVersion}。");

        var versionsDirectory = Path.Combine(instanceDirectory, "versions");
        var librariesDirectory = Path.Combine(instanceDirectory, "libraries");
        var assetsDirectory = Path.Combine(instanceDirectory, "assets");
        var modsDirectory = Path.Combine(instanceDirectory, "mods");
        Directory.CreateDirectory(instanceDirectory);
        Directory.CreateDirectory(versionsDirectory);
        Directory.CreateDirectory(librariesDirectory);
        Directory.CreateDirectory(assetsDirectory);
        Directory.CreateDirectory(modsDirectory);

        var result = new GameInstallResult
        {
            InstanceDirectory = instanceDirectory,
            MinecraftVersion = request.MinecraftVersion,
            Loader = request.Loader,
            LoaderVersion = string.IsNullOrWhiteSpace(request.LoaderVersion) ? null : request.LoaderVersion,
            JavaPath = request.JavaPath,
        };

        progress?.Report(new DownloadProgress("读取版本清单", 0, 1, request.MinecraftVersion));
        using var versionDoc = await GetJsonWithFallbackAsync(
                version.Url,
                $"https://bmclapi2.bangbang93.com/version/{Uri.EscapeDataString(request.MinecraftVersion)}/json",
                cancellationToken)
            .ConfigureAwait(false);
        var versionRoot = versionDoc.RootElement;
        var versionId = GetString(versionRoot, "id") ?? request.MinecraftVersion;
        var versionDirectory = Path.Combine(versionsDirectory, versionId);
        Directory.CreateDirectory(versionDirectory);

        var versionJsonPath = Path.Combine(versionDirectory, versionId + ".json");
        await File.WriteAllTextAsync(
                versionJsonPath,
                versionRoot.GetRawText(),
                new UTF8Encoding(false),
                cancellationToken)
            .ConfigureAwait(false);

        await DownloadVanillaFilesAsync(
                versionRoot, versionDirectory, librariesDirectory, assetsDirectory,
                request.DownloadAssets, request.MaxConcurrency, result, progress, cancellationToken)
            .ConfigureAwait(false);

        var finalVersionId = versionId;
        switch (request.Loader)
        {
            case GameLoader.Fabric:
            case GameLoader.Quilt:
                finalVersionId = await InstallFabricLikeAsync(
                        request, versionsDirectory, librariesDirectory, result, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case GameLoader.Forge:
            case GameLoader.NeoForge:
                finalVersionId = await InstallForgeLikeAsync(
                        request, versionsDirectory, result, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }

        WriteMmcPack(instanceDirectory, request, finalVersionId);
        WriteInstallInfo(instanceDirectory, request, result, finalVersionId);
        WriteLauncherProfiles(instanceDirectory, request, finalVersionId);

        progress?.Report(new DownloadProgress("安装完成", 1, 1, instanceDirectory));
        result.VersionId = finalVersionId;
        return result;
    }

    /// <summary>Finds a usable Java executable without launching anything.</summary>
    public static string? DetectJava()
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            var candidate = Path.Combine(home, "bin", "java.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "java.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var candidates = Directory.GetFiles(root, "java.exe", SearchOption.AllDirectories)
                    .Take(20)
                    .ToList();
                var match = candidates.FirstOrDefault(candidate =>
                    candidate.Contains(Path.Combine("bin", "java.exe"), StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore inaccessible installations.
            }
        }

        return null;
    }

    public static string NeoForgePrefix(string minecraftVersion)
    {
        var parts = minecraftVersion.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3) return $"{parts[1]}.{parts[2]}.";
        if (parts.Length == 2) return $"{parts[1]}.0.";
        return string.Empty;
    }

    private async Task DownloadVanillaFilesAsync(
        JsonElement versionRoot,
        string versionDirectory,
        string librariesDirectory,
        string assetsDirectory,
        bool downloadAssets,
        int maxConcurrency,
        GameInstallResult result,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (versionRoot.TryGetProperty("downloads", out var downloads) &&
            downloads.TryGetProperty("client", out var client))
        {
            var clientUrl = GetString(client, "url");
            var clientSha1 = GetString(client, "sha1");
            var clientSize = GetLong(client, "size");
            if (!string.IsNullOrWhiteSpace(clientUrl))
            {
                progress?.Report(new DownloadProgress("下载客户端 JAR", 0, 1, "client.jar"));
                var clientJar = GetString(versionRoot, "id") ?? "client";
                await DownloadFileAsync(
                        clientUrl!, Path.Combine(versionDirectory, clientJar + ".jar"),
                        clientSha1, clientSize, result, progress, cancellationToken,
                        $"https://bmclapi2.bangbang93.com/version/{Uri.EscapeDataString(clientJar)}/client")
                    .ConfigureAwait(false);
            }
        }

        var libraries = new List<JsonElement>();
        if (versionRoot.TryGetProperty("libraries", out var librariesElement) &&
            librariesElement.ValueKind == JsonValueKind.Array)
            libraries.AddRange(librariesElement.EnumerateArray());

        var items = new List<DownloadItem>();
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!LibraryAllowed(library)) continue;

            if (library.TryGetProperty("downloads", out var libraryDownloads) &&
                libraryDownloads.TryGetProperty("artifact", out var artifact))
            {
                var relative = GetString(artifact, "path");
                var url = GetString(artifact, "url");
                if (!string.IsNullOrWhiteSpace(relative) && !string.IsNullOrWhiteSpace(url))
                {
                    var destination = SafeCombine(librariesDirectory, relative);
                    if (destination is not null)
                        items.Add(new DownloadItem(url!, destination, GetString(artifact, "sha1"),
                            GetLong(artifact, "size"), MirrorUrlFor(url!)));
                }
            }

            if (libraryDownloads.TryGetProperty("classifiers", out var classifiers) &&
                classifiers.ValueKind == JsonValueKind.Object)
            {
                var nativeKey = new[] { "natives-windows", "natives-windows-64", "natives-windows-32" }
                    .FirstOrDefault(key => classifiers.TryGetProperty(key, out _));
                if (nativeKey is not null && classifiers.TryGetProperty(nativeKey, out var native))
                {
                    var relative = GetString(native, "path");
                    var url = GetString(native, "url");
                    if (!string.IsNullOrWhiteSpace(relative) && !string.IsNullOrWhiteSpace(url))
                    {
                        var destination = SafeCombine(librariesDirectory, relative);
                        if (destination is not null)
                            items.Add(new DownloadItem(url!, destination, GetString(native, "sha1"),
                                GetLong(native, "size"), MirrorUrlFor(url!)));
                    }
                }
            }
        }

        await DownloadManyAsync("下载运行库", items, result, progress, cancellationToken,
            maxConcurrency).ConfigureAwait(false);

        if (downloadAssets)
            await DownloadAssetsAsync(versionRoot, assetsDirectory, result, progress,
                cancellationToken, maxConcurrency).ConfigureAwait(false);
    }

    private sealed record DownloadItem(string Url, string Destination, string? Sha1, long Size, string? Mirror);

    private async Task DownloadManyAsync(
        string phase,
        IReadOnlyList<DownloadItem> items,
        GameInstallResult result,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        int maxConcurrency)
    {
        if (items.Count == 0) return;
        var done = 0;
        await Parallel.ForEachAsync(items, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(maxConcurrency, 1, 6),
            CancellationToken = cancellationToken,
        }, async (item, token) =>
        {
            var current = Interlocked.Increment(ref done);
            progress?.Report(new DownloadProgress(phase, current, items.Count,
                Path.GetFileName(item.Destination)));
            await DownloadFileAsync(item.Url, item.Destination, item.Sha1, item.Size,
                result, progress, token, item.Mirror).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task DownloadAssetsAsync(
        JsonElement versionRoot,
        string assetsDirectory,
        GameInstallResult result,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        int maxConcurrency)
    {
        if (!versionRoot.TryGetProperty("assetIndex", out var assetIndex))
            return;

        var indexId = GetString(assetIndex, "id") ?? "assets";
        var indexUrl = GetString(assetIndex, "url");
        if (string.IsNullOrWhiteSpace(indexUrl)) return;

        var indexesDirectory = Path.Combine(assetsDirectory, "indexes");
        Directory.CreateDirectory(indexesDirectory);
        var indexPath = Path.Combine(indexesDirectory, indexId + ".json");
        await DownloadFileAsync(
                indexUrl, indexPath, GetString(assetIndex, "sha1"), GetLong(assetIndex, "size"),
                result, progress, cancellationToken)
            .ConfigureAwait(false);

        using var indexDoc = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken)
            .ConfigureAwait(false));
        if (!indexDoc.RootElement.TryGetProperty("objects", out var objects) ||
            objects.ValueKind != JsonValueKind.Object)
            return;

        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in objects.EnumerateObject())
        {
            var hash = GetString(property.Value, "hash");
            if (!string.IsNullOrWhiteSpace(hash)) hashes.Add(hash);
        }

        var objectsDirectory = Path.Combine(assetsDirectory, "objects");
        var items = new List<DownloadItem>();
        foreach (var hash in hashes)
        {
            var prefix = hash[..2];
            var destination = Path.Combine(objectsDirectory, prefix, hash);
            if (File.Exists(destination) && new FileInfo(destination).Length > 0) continue;
            items.Add(new DownloadItem(
                $"{ResourcesBase}{prefix}/{hash}", destination, hash, 0,
                $"https://bmclapi2.bangbang93.com/assets/{prefix}/{hash}"));
        }

        await DownloadManyAsync("下载资源文件", items, result, progress, cancellationToken,
            maxConcurrency).ConfigureAwait(false);
    }

    private async Task<string> InstallFabricLikeAsync(
        GameInstallRequest request,
        string versionsDirectory,
        string librariesDirectory,
        GameInstallResult result,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var loaderVersion = request.LoaderVersion;
        if (string.IsNullOrWhiteSpace(loaderVersion))
            throw new InvalidOperationException($"请选择 {request.Loader} 加载器版本。");

        var profileUrl = request.Loader == GameLoader.Fabric
            ? $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(request.MinecraftVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json"
            : $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(request.MinecraftVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";

        var versionId = request.Loader == GameLoader.Fabric
            ? $"fabric-loader-{loaderVersion}-{request.MinecraftVersion}"
            : $"quilt-loader-{loaderVersion}-{request.MinecraftVersion}";

        progress?.Report(new DownloadProgress("读取加载器配置", 0, 1, request.Loader.ToString()));
        var profileMirror = request.Loader == GameLoader.Fabric
            ? $"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{Uri.EscapeDataString(request.MinecraftVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json"
            : null;
        using var doc = await GetJsonWithFallbackAsync(profileUrl, profileMirror, cancellationToken)
            .ConfigureAwait(false);
        var root = doc.RootElement;

        var versionDirectory = Path.Combine(versionsDirectory, versionId);
        Directory.CreateDirectory(versionDirectory);

        var json = root.GetRawText();
        using (var mutable = JsonDocument.Parse(json))
        {
            var builder = new StringBuilder();
            builder.Append('{');
            var first = true;
            var hasInheritsFrom = false;
            foreach (var property in mutable.RootElement.EnumerateObject())
            {
                if (!first) builder.Append(',');
                first = false;
                if (string.Equals(property.Name, "inheritsFrom", StringComparison.OrdinalIgnoreCase))
                    hasInheritsFrom = true;
                builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase))
                    builder.Append(JsonSerializer.Serialize(versionId));
                else if (string.Equals(property.Name, "inheritsFrom", StringComparison.OrdinalIgnoreCase))
                    builder.Append(JsonSerializer.Serialize(request.MinecraftVersion));
                else
                    builder.Append(property.Value.GetRawText());
            }
            if (!hasInheritsFrom)
            {
                if (!first) builder.Append(',');
                builder.Append(JsonSerializer.Serialize("inheritsFrom")).Append(':')
                    .Append(JsonSerializer.Serialize(request.MinecraftVersion));
            }
            builder.Append('}');
            await File.WriteAllTextAsync(
                    Path.Combine(versionDirectory, versionId + ".json"),
                    builder.ToString(),
                    new UTF8Encoding(false),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
        {
            var list = libraries.EnumerateArray().ToList();
            var done = 0;
            foreach (var library in list)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                var coordinate = GetString(library, "name");
                if (string.IsNullOrWhiteSpace(coordinate)) continue;

                var baseUrl = GetString(library, "url") ?? (request.Loader == GameLoader.Fabric
                    ? "https://maven.fabricmc.net/"
                    : "https://maven.quiltmc.org/repository/release/");
                var relative = MavenPath(coordinate);
                if (relative is null) continue;

                var destination = SafeCombine(librariesDirectory, relative);
                if (destination is null) continue;

                progress?.Report(new DownloadProgress(
                    "下载加载器库", done, list.Count, Path.GetFileName(relative)));
                var url = baseUrl.TrimEnd('/') + "/" + relative.Replace('\\', '/');
                await DownloadFileAsync(
                        url, destination, GetString(library, "sha1"), 0,
                        result, progress, cancellationToken, MirrorUrlFor(url))
                    .ConfigureAwait(false);
            }
        }

        return versionId;
    }

    private async Task<string> InstallForgeLikeAsync(
        GameInstallRequest request,
        string versionsDirectory,
        GameInstallResult result,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var loaderVersion = request.LoaderVersion;
        if (string.IsNullOrWhiteSpace(loaderVersion))
            throw new InvalidOperationException($"请选择 {request.Loader} 加载器版本。");

        var javaPath = request.JavaPath;
        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
            javaPath = DetectJava();
        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
            throw new InvalidOperationException(
                "Forge/NeoForge 安装器需要 Java。请安装 Java 21 或在下载窗口选择 java.exe。");

        result.JavaPath = javaPath;
        var installerUrl = request.Loader == GameLoader.Forge
            ? $"https://maven.minecraftforge.net/net/minecraftforge/forge/{request.MinecraftVersion}-{loaderVersion}/forge-{request.MinecraftVersion}-{loaderVersion}-installer.jar"
            : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar";

        var installerMirror = request.Loader == GameLoader.Forge
            ? $"https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/{request.MinecraftVersion}-{loaderVersion}/forge-{request.MinecraftVersion}-{loaderVersion}-installer.jar"
            : $"https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar";
        var temp = Path.Combine(Path.GetTempPath(), "PackDependencyTool", "installers",
            Guid.NewGuid().ToString("N") + ".jar");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);

        try
        {
            progress?.Report(new DownloadProgress("下载加载器安装器", 0, 1, Path.GetFileName(installerUrl)));
            await DownloadFileAsync(
                    installerUrl, temp, null, 0, result, progress, cancellationToken, installerMirror)
                .ConfigureAwait(false);

            progress?.Report(new DownloadProgress("运行加载器安装器", 0, 1, request.Loader.ToString()));
            await RunInstallerAsync(
                    javaPath!, temp, result.InstanceDirectory, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
        }

        var installed = Directory.EnumerateDirectories(versionsDirectory)
            .Where(directory =>
            {
                var name = Path.GetFileName(directory);
                return request.Loader == GameLoader.Forge
                    ? name.Contains("forge", StringComparison.OrdinalIgnoreCase)
                    : name.Contains("neoforge", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (installed is null)
        {
            result.Warnings.Add("安装器已运行，但没有找到生成的 versions 目录；请检查安装器日志。");
            return request.Loader == GameLoader.Forge
                ? $"{request.MinecraftVersion}-forge-{loaderVersion}"
                : $"neoforge-{loaderVersion}";
        }

        return Path.GetFileName(installed);
    }

    private static void AddJavaProxyArguments(ProcessStartInfo info)
    {
        var proxy = GetSystemProxy();
        if (proxy is null) return;
        info.ArgumentList.Add($"-Dhttp.proxyHost={proxy.Value.Host}");
        info.ArgumentList.Add($"-Dhttp.proxyPort={proxy.Value.Port}");
        info.ArgumentList.Add($"-Dhttps.proxyHost={proxy.Value.Host}");
        info.ArgumentList.Add($"-Dhttps.proxyPort={proxy.Value.Port}");
        info.ArgumentList.Add("-Djava.net.useSystemProxies=true");
        info.ArgumentList.Add("-Dhttp.nonProxyHosts=localhost|127.0.0.1");
    }

    private static (string Host, int Port)? GetSystemProxy()
    {
        foreach (var name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (TryParseProxy(value, out var proxy)) return proxy;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key?.GetValue("ProxyEnable") is not int enabled || enabled == 0) return null;
            var server = key.GetValue("ProxyServer") as string;
            if (string.IsNullOrWhiteSpace(server)) return null;
            foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : part;
                if (TryParseProxy(candidate, out var proxy)) return proxy;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Proxy is optional.
        }

        return null;
    }

    private static bool TryParseProxy(string? value, out (string Host, int Port) proxy)
    {
        proxy = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.Port is <= 0 or > 65535)
            return false;
        proxy = (uri.Host, uri.Port);
        return true;
    }

    private async Task RunInstallerAsync(
        string javaPath,
        string installerPath,
        string instanceDirectory,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(javaPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = instanceDirectory,
        };
        AddJavaProxyArguments(info);
        info.ArgumentList.Add("-jar");
        info.ArgumentList.Add(installerPath);
        info.ArgumentList.Add("--installClient");
        info.ArgumentList.Add(instanceDirectory);

        using var process = new Process { StartInfo = info };
        if (!process.Start())
            throw new InvalidOperationException("无法启动 Java 安装器。");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException("加载器安装器运行超过 15 分钟。");
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var tail = string.Join(Environment.NewLine,
                new[] { output, error }
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .SelectMany(text => text.Split('\n'))
                    .TakeLast(12));
            throw new InvalidOperationException($"加载器安装器失败（退出码 {process.ExitCode}）：{tail}");
        }

        progress?.Report(new DownloadProgress("加载器安装完成", 1, 1, ""));
    }

    private async Task DownloadFileAsync(
        string url,
        string destination,
        string? sha1,
        long expectedSize,
        GameInstallResult result,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        string? mirrorUrl = null)
    {
        if (!_allowMirror) mirrorUrl = null;
        if (File.Exists(destination) && expectedSize > 0)
        {
            var length = new FileInfo(destination).Length;
            if (length == expectedSize && string.IsNullOrWhiteSpace(sha1)) return;
        }

        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromMinutes(5));
            var download = await _downloader.DownloadUrlAsync(
                    url, destination, sha1, null, expectedSize, progress, attempt.Token, mirrorUrl)
                .ConfigureAwait(false);
            lock (result)
            {
                result.FilesDownloaded++;
                result.BytesDownloaded += download.Bytes;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"下载超时：{Path.GetFileName(destination)}");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                   ex is HttpRequestException or IOException or InvalidDataException or
                                       InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"下载失败（官方源/镜像源均不可用）：{Path.GetFileName(destination)}", ex);
        }
    }

    private string? MirrorUrlFor(string url)
    {
        if (!_allowMirror) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.TrimStart('/');
        if (host == "resources.download.minecraft.net")
            return "https://bmclapi2.bangbang93.com/assets/" + path;

        if (host is "libraries.minecraft.net" or "maven.minecraftforge.net" or "maven.fabricmc.net"
            or "maven.quiltmc.org" or "maven.neoforged.net")
        {
            if (host == "maven.neoforged.net")
                path = path.Replace("releases/", "", StringComparison.OrdinalIgnoreCase);
            else if (host == "maven.quiltmc.org")
                path = path.Replace("repository/release/", "", StringComparison.OrdinalIgnoreCase);

            return "https://bmclapi2.bangbang93.com/maven/" + path;
        }

        return null;
    }

    private static void WriteLauncherProfiles(
        string instanceDirectory,
        GameInstallRequest request,
        string versionId)
    {
        var path = Path.Combine(instanceDirectory, "launcher_profiles.json");
        JsonObject root;
        try
        {
            root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing
                ? existing
                : new JsonObject();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            root = new JsonObject();
        }

        if (root["profiles"] is not JsonObject profiles)
        {
            profiles = new JsonObject();
            root["profiles"] = profiles;
        }

        var loaderText = request.Loader == GameLoader.Vanilla ? "Vanilla" : request.Loader.ToString();
        profiles["packdep-" + versionId] = new JsonObject
        {
            ["name"] = $"{request.MinecraftVersion} - {loaderText}" +
                       (string.IsNullOrWhiteSpace(request.LoaderVersion) ? "" : " " + request.LoaderVersion),
            ["type"] = "custom",
            ["lastVersionId"] = versionId,
            ["gameDir"] = instanceDirectory,
        };
        root["settings"] ??= new JsonObject();
        root["version"] ??= JsonValue.Create(3);

        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private void WriteMmcPack(string instanceDirectory, GameInstallRequest request, string versionId)
    {
        var components = new List<object>
        {
            new { uid = "net.minecraft", version = request.MinecraftVersion },
        };

        var loaderUid = request.Loader switch
        {
            GameLoader.Fabric => "net.fabricmc.fabric-loader",
            GameLoader.Quilt => "org.quiltmc.quilt-loader",
            GameLoader.Forge => "net.minecraftforge",
            GameLoader.NeoForge => "net.neoforged",
            _ => null,
        };
        if (loaderUid is not null && !string.IsNullOrWhiteSpace(request.LoaderVersion))
            components.Add(new { uid = loaderUid, version = request.LoaderVersion! });

        var json = JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            components,
        }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(Path.Combine(instanceDirectory, "mmc-pack.json"), json, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(instanceDirectory, "packdep-version.txt"), versionId,
            new UTF8Encoding(false));
    }

    private static void WriteInstallInfo(
        string instanceDirectory,
        GameInstallRequest request,
        GameInstallResult result,
        string versionId)
    {
        var json = JsonSerializer.Serialize(new
        {
            minecraft = request.MinecraftVersion,
            loader = result.LoaderName,
            loaderVersion = request.LoaderVersion,
            versionId,
            assets = request.DownloadAssets,
            installedAt = DateTimeOffset.UtcNow,
        }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(Path.Combine(instanceDirectory, "packdep-install.json"), json,
            new UTF8Encoding(false));
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        var text = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(text);
    }

    private async Task<JsonDocument> GetJsonWithFallbackAsync(
        string primaryUrl,
        string? mirrorUrl,
        CancellationToken cancellationToken)
    {
        if (!_allowMirror) mirrorUrl = null;
        var text = await GetStringWithFallbackAsync(primaryUrl, mirrorUrl, cancellationToken)
            .ConfigureAwait(false);
        return JsonDocument.Parse(text);
    }

    private async Task<string> GetStringWithFallbackAsync(
        string primaryUrl,
        string? mirrorUrl,
        CancellationToken cancellationToken)
    {
        Exception? primaryError = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            return await GetStringAsync(primaryUrl, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && mirrorUrl is not null)
        {
            primaryError = ex;
        }

        if (mirrorUrl is not null)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                return await GetStringAsync(mirrorUrl, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"官方源与镜像源均失败：{primaryError?.Message ?? "超时"} / {ex.Message}", ex);
            }
        }

        if (primaryError is not null) throw primaryError;
        throw new InvalidOperationException("无法获取下载信息。");
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, application/xml, text/xml, */*");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildInstanceName(GameInstallRequest request)
    {
        var loader = request.Loader switch
        {
            GameLoader.Fabric => "fabric",
            GameLoader.Quilt => "quilt",
            GameLoader.Forge => "forge",
            GameLoader.NeoForge => "neoforge",
            _ => null,
        };

        var name = string.IsNullOrWhiteSpace(request.InstanceName)
            ? loader is null
                ? request.MinecraftVersion
                : $"{request.MinecraftVersion}-{loader}-{request.LoaderVersion}"
            : request.InstanceName.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray())
            .Trim()
            .TrimEnd('.');
        return cleaned.Length == 0 || cleaned is "." or ".." ? "Minecraft" : cleaned;
    }

    private static string EnsureUniqueDirectory(string parent, string name)
    {
        Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, name);
        var suffix = 2;
        while (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            root = Path.Combine(parent, $"{name} ({suffix++})");

        Directory.CreateDirectory(root);
        return root;
    }

    private static List<LoaderVersionInfo> SortVersions(List<LoaderVersionInfo> versions) =>
        versions
            .OrderByDescending(version => version, Comparer<LoaderVersionInfo>.Create((left, right) =>
                CompareVersionStrings(left.Version, right.Version)))
            .ToList();

    private static int CompareVersionStrings(string left, string right)
    {
        var leftParts = left.Split('.', '-');
        var rightParts = right.Split('.', '-');
        var count = Math.Max(leftParts.Length, rightParts.Length);
        for (var index = 0; index < count; index++)
        {
            var leftPart = index < leftParts.Length ? leftParts[index] : "0";
            var rightPart = index < rightParts.Length ? rightParts[index] : "0";
            if (int.TryParse(leftPart, out var leftNumber) && int.TryParse(rightPart, out var rightNumber))
            {
                if (leftNumber != rightNumber) return leftNumber.CompareTo(rightNumber);
            }
            else
            {
                var compare = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
                if (compare != 0) return compare;
            }
        }

        return 0;
    }

    private static bool LibraryAllowed(JsonElement library)
    {
        if (!library.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return true;

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (!rule.TryGetProperty("action", out var actionElement)) continue;
            var action = actionElement.GetString();
            if (rule.TryGetProperty("os", out var os) && os.ValueKind == JsonValueKind.Object)
            {
                var name = GetString(os, "name");
                if (!string.IsNullOrWhiteSpace(name) &&
                    !string.Equals(name, "windows", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            allowed = string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static string? MavenPath(string coordinate)
    {
        var parts = coordinate.Split(':');
        if (parts.Length < 3) return null;

        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? parts[3] : null;
        if (group.Any(ch => ch is '/' or '\\' or ':') || artifact.Any(ch => ch is '/' or '\\'))
            return null;

        var fileName = artifact + "-" + version + (string.IsNullOrWhiteSpace(classifier) ? "" : "-" + classifier) + ".jar";
        return Path.Combine(group.Replace('.', '/'), artifact, version, fileName);
    }

    private static string? SafeCombine(string root, string relative)
    {
        var normalized = relative.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment == ".." || segment.Contains(':')))
            return null;

        try
        {
            var rootFull = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var destination = Path.GetFullPath(Path.Combine(rootFull, normalized));
            return destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? destination : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0;

    private static DateTimeOffset? GetDateTime(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
}
