using System.Collections.Concurrent;
using PackDepTool.Core.Models;
using PackDepTool.Core.Persistence;

namespace PackDepTool.Core.Net;

public sealed class AutoCompleteItemResult
{
    public string ModId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? FileName { get; init; }
    public string? Error { get; init; }
}

public sealed class AutoCompleteResult
{
    public List<AutoCompleteItemResult> Items { get; } = [];
    public int DownloadedCount => Items.Count(item => item.Success);
    public int FailedCount => Items.Count(item => !item.Success);
}

/// <summary>
/// One-click resolver for missing required dependencies. Uses exact Modrinth / CurseForge
/// identity matching and downloads only versions compatible with the current pack.
/// </summary>
public sealed class DependencyAutoCompleter
{
    private readonly ModDownloadService _downloader;
    private readonly ModProjectResolver _resolver;

    public DependencyAutoCompleter(ModDownloadService downloader, ModProjectResolver resolver)
    {
        _downloader = downloader;
        _resolver = resolver;
    }

    public async Task<AutoCompleteResult> CompleteAsync(
        GraphDocument graph,
        PackProfile profile,
        string packRoot,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int maxConcurrency = 3)
    {
        var result = new AutoCompleteResult();
        var missing = graph.Nodes
            .Where(node => node.Status == ModStatus.Missing && !PlatformModIds.IsPlatform(node.Id))
            .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (missing.Count == 0) return result;
        if (string.Equals(profile.McVersion, "unknown", StringComparison.OrdinalIgnoreCase) ||
            profile.McVersion.Length == 0)
            throw new InvalidOperationException("无法识别整合包 Minecraft 版本，不能安全自动补全。");

        var modsDirectory = Path.Combine(packRoot, "mods");
        Directory.CreateDirectory(modsDirectory);
        var itemsByIndex = new ConcurrentDictionary<int, AutoCompleteItemResult>();
        var completed = 0;

        await Parallel.ForEachAsync(
            missing.Select((node, index) => (Node: node, Index: index)),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(maxConcurrency, 1, 6),
                CancellationToken = cancellationToken,
            },
            async (candidate, token) =>
            {
                var node = candidate.Node;
                var current = Interlocked.Increment(ref completed);
                progress?.Report(new DownloadProgress(
                    "补全缺失依赖", current, missing.Count, $"{node.Name} ({node.Id})"));

                try
                {
                    var project = await _resolver.ResolveAsync(node, token).ConfigureAwait(false);
                    if (project is null)
                    {
                        itemsByIndex[candidate.Index] = new AutoCompleteItemResult
                        {
                            ModId = node.Id,
                            Name = node.Name,
                            Error = "Modrinth / CurseForge 未找到精确匹配项目",
                        };
                        return;
                    }

                    var versions = await _downloader.GetVersionsAsync(
                            project, profile.McVersion, profile.Loader, token)
                        .ConfigureAwait(false);
                    var version = versions.FirstOrDefault(candidateVersion => candidateVersion.IsCompatible);
                    if (version is null)
                    {
                        itemsByIndex[candidate.Index] = new AutoCompleteItemResult
                        {
                            ModId = node.Id,
                            Name = node.Name,
                            Error = "没有与当前 MC / 加载器兼容的版本",
                        };
                        return;
                    }

                    var download = await _downloader.DownloadAsync(version, modsDirectory, progress, token)
                        .ConfigureAwait(false);
                    itemsByIndex[candidate.Index] = new AutoCompleteItemResult
                    {
                        ModId = node.Id,
                        Name = node.Name,
                        Success = true,
                        FileName = download.FileName,
                    };
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or
                                               InvalidDataException or IOException or CurseForgeApiException or
                                               ModrinthApiException or MissingApiKeyException)
                {
                    itemsByIndex[candidate.Index] = new AutoCompleteItemResult
                    {
                        ModId = node.Id,
                        Name = node.Name,
                        Error = ex.Message,
                    };
                }
            }).ConfigureAwait(false);

        for (var index = 0; index < missing.Count; index++)
            if (itemsByIndex.TryGetValue(index, out var item))
                result.Items.Add(item);

        return result;
    }

}
