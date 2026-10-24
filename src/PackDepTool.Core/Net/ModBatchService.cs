using System.Collections.Concurrent;
using PackDepTool.Core.Analysis;
using PackDepTool.Core.Models;
using PackDepTool.Core.Scanning;

namespace PackDepTool.Core.Net;

public enum BatchModAction
{
    Download,
    Upgrade,
    Disable,
    Delete,
}

public sealed class BatchModItem
{
    public string NodeId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Success { get; init; }
    public bool Skipped { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class BatchModResult
{
    public BatchModAction Action { get; init; }
    public List<BatchModItem> Items { get; } = [];
    public int SuccessCount => Items.Count(item => item.Success && !item.Skipped);
    public int SkippedCount => Items.Count(item => item.Skipped);
    public int FailedCount => Items.Count(item => !item.Success);
}

/// <summary>Batch download / upgrade / disable / delete for checked mods.</summary>
public sealed class ModBatchService
{
    private readonly ModDownloadService _downloader;
    private readonly ModProjectResolver _resolver;
    private readonly ModFileManager _files;

    public ModBatchService(ModDownloadService downloader, ModProjectResolver resolver, ModFileManager files)
    {
        _downloader = downloader;
        _resolver = resolver;
        _files = files;
    }

    public async Task<BatchModResult> DownloadAsync(
        IReadOnlyList<ModNode> nodes,
        PackProfile profile,
        string packRoot,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int maxConcurrency = 3,
        GraphDocument? graph = null)
    {
        var result = new BatchModResult { Action = BatchModAction.Download };
        var modsDirectory = Path.Combine(packRoot, "mods");
        Directory.CreateDirectory(modsDirectory);
        var itemsByIndex = new ConcurrentDictionary<int, BatchModItem>();
        var candidates = new List<(ModNode Node, int Index)>();

        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            if (PlatformModIds.IsPlatform(node.Id))
            {
                itemsByIndex[index] = Skip(node, "平台组件，不下载");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(_files.ResolveLocalFile(node, packRoot)))
            {
                itemsByIndex[index] = Skip(node, "已安装，跳过");
                continue;
            }

            candidates.Add((node, index));
        }

        var completed = 0;
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(maxConcurrency, 1, 6),
                CancellationToken = cancellationToken,
            },
            async (candidate, token) =>
            {
                var node = candidate.Node;
                var current = Interlocked.Increment(ref completed);
                progress?.Report(new DownloadProgress("批量下载", current, candidates.Count, node.Name));
                try
                {
                    var project = await _resolver.ResolveAsync(node, token).ConfigureAwait(false);
                    if (project is null)
                    {
                        itemsByIndex[candidate.Index] = Fail(node, "未找到精确匹配项目");
                        return;
                    }

                    var versions = await _downloader.GetVersionsAsync(
                            project, profile.McVersion, profile.Loader, token)
                        .ConfigureAwait(false);
                    var requiredRanges = IncomingRequiredRanges(graph, node.Id);
                    var version = versions.FirstOrDefault(candidateVersion =>
                        candidateVersion.IsCompatible &&
                        requiredRanges.All(range =>
                            VersionRange.Satisfies(range, candidateVersion.VersionNumber) != false));
                    if (version is null)
                    {
                        itemsByIndex[candidate.Index] = Fail(node, "没有兼容版本");
                        return;
                    }

                    var download = await _downloader.DownloadAsync(version, modsDirectory, progress, token)
                        .ConfigureAwait(false);
                    itemsByIndex[candidate.Index] = new BatchModItem
                    {
                        NodeId = node.Id,
                        Name = node.Name,
                        Success = true,
                        Message = $"已下载 {download.FileName}",
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    itemsByIndex[candidate.Index] = Fail(node, ex.Message);
                }
            }).ConfigureAwait(false);

        for (var index = 0; index < nodes.Count; index++)
            if (itemsByIndex.TryGetValue(index, out var item))
                result.Items.Add(item);

        return result;
    }

    public async Task<BatchModResult> UpgradeAsync(
        IReadOnlyList<ModNode> nodes,
        PackProfile profile,
        string packRoot,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int maxConcurrency = 3,
        GraphDocument? graph = null)
    {
        var result = new BatchModResult { Action = BatchModAction.Upgrade };
        var trash = Path.Combine(packRoot, ".packdep-trash", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var stagingRoot = Path.Combine(packRoot, ".packdep-staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        var itemsByIndex = new ConcurrentDictionary<int, BatchModItem>();
        var candidates = new List<(ModNode Node, int Index, string? CurrentPath)>();

        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            var currentPath = _files.ResolveLocalFile(node, packRoot);
            if (currentPath is null)
            {
                itemsByIndex[index] = Skip(node, "没有本地 JAR");
                continue;
            }

            if (PlatformModIds.IsPlatform(node.Id))
            {
                itemsByIndex[index] = Skip(node, "平台组件，不升级");
                continue;
            }

            candidates.Add((node, index, currentPath));
        }

        try
        {
            var completed = 0;
            await Parallel.ForEachAsync(
                candidates,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(maxConcurrency, 1, 6),
                    CancellationToken = cancellationToken,
                },
                async (candidate, token) =>
                {
                    var node = candidate.Node;
                    var current = Interlocked.Increment(ref completed);
                    progress?.Report(new DownloadProgress("检查更新", current, candidates.Count, node.Name));
                    try
                    {
                        var project = await _resolver.ResolveAsync(node, token).ConfigureAwait(false);
                        if (project is null)
                        {
                            itemsByIndex[candidate.Index] = Fail(node, "未找到精确匹配项目");
                            return;
                        }

                        var versions = await _downloader.GetVersionsAsync(
                                project, profile.McVersion, profile.Loader, token)
                            .ConfigureAwait(false);
                        var requiredRanges = IncomingRequiredRanges(graph, node.Id);
                        var latest = versions
                            .Where(version => version.IsCompatible &&
                                              requiredRanges.All(range =>
                                                  VersionRange.Satisfies(range, version.VersionNumber) != false))
                            .OrderByDescending(version => version.PublishedAt ?? DateTimeOffset.MinValue)
                            .FirstOrDefault();
                        if (latest is null)
                        {
                            itemsByIndex[candidate.Index] = Fail(node, "没有兼容版本");
                            return;
                        }

                        if (SameVersion(latest.VersionNumber, node.DisplayVersion) &&
                            string.Equals(latest.FileName, Path.GetFileName(candidate.CurrentPath),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            itemsByIndex[candidate.Index] = Skip(node, "已是最新版本");
                            return;
                        }

                        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(staging);
                        var download = await _downloader.DownloadAsync(latest, staging, progress, token)
                            .ConfigureAwait(false);
                        var newPath = Path.Combine(Path.GetDirectoryName(candidate.CurrentPath)!, download.FileName);
                        var movedOld = false;

                        try
                        {
                            _files.MoveToTrash(node, packRoot, trash);
                            movedOld = true;
                            File.Move(download.FilePath, newPath);
                        }
                        catch
                        {
                            if (movedOld && !File.Exists(candidate.CurrentPath))
                            {
                                var trashed = Path.Combine(trash, Path.GetFileName(candidate.CurrentPath));
                                if (File.Exists(trashed))
                                    File.Move(trashed, candidate.CurrentPath);
                            }

                            throw;
                        }

                        itemsByIndex[candidate.Index] = new BatchModItem
                        {
                            NodeId = node.Id,
                            Name = node.Name,
                            Success = true,
                            Message = $"{node.DisplayVersion} → {latest.VersionNumber}",
                        };
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        itemsByIndex[candidate.Index] = Fail(node, ex.Message);
                    }
                }).ConfigureAwait(false);

            for (var index = 0; index < nodes.Count; index++)
                if (itemsByIndex.TryGetValue(index, out var item))
                    result.Items.Add(item);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }

        return result;
    }

    public BatchModResult Disable(IReadOnlyList<ModNode> nodes, string packRoot)
    {
        var result = new BatchModResult { Action = BatchModAction.Disable };
        foreach (var node in nodes)
        {
            try
            {
                var path = _files.Disable(node, packRoot);
                result.Items.Add(new BatchModItem
                {
                    NodeId = node.Id,
                    Name = node.Name,
                    Success = true,
                    Message = $"已禁用 {Path.GetFileName(path)}",
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Items.Add(Fail(node, ex.Message));
            }
        }

        return result;
    }

    public BatchModResult Delete(IReadOnlyList<ModNode> nodes, string packRoot)
    {
        var result = new BatchModResult { Action = BatchModAction.Delete };
        var trash = Path.Combine(packRoot, ".packdep-trash", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        foreach (var node in nodes)
        {
            try
            {
                var path = _files.MoveToTrash(node, packRoot, trash);
                result.Items.Add(new BatchModItem
                {
                    NodeId = node.Id,
                    Name = node.Name,
                    Success = true,
                    Message = $"已移到回收站 {Path.GetFileName(path)}",
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Items.Add(Fail(node, ex.Message));
            }
        }

        return result;
    }

    private static BatchModItem Skip(ModNode node, string message) => new()
    {
        NodeId = node.Id,
        Name = node.Name,
        Success = true,
        Skipped = true,
        Message = message,
    };

    private static BatchModItem Fail(ModNode node, string message) => new()
    {
        NodeId = node.Id,
        Name = node.Name,
        Success = false,
        Message = message,
    };

    private static bool SameVersion(string left, string right)
    {
        static string Normalize(string value) => new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0) return false;
        return a == b || a.Contains(b, StringComparison.Ordinal) ||
               b.Contains(a, StringComparison.Ordinal);
    }

    private static List<string> IncomingRequiredRanges(GraphDocument? graph, string nodeId)
    {
        if (graph is null) return [];
        return graph.Edges
            .Where(edge => string.Equals(edge.To, nodeId, StringComparison.OrdinalIgnoreCase) &&
                           edge.Type == EdgeType.Requires &&
                           !string.IsNullOrWhiteSpace(edge.VersionRange))
            .Select(edge => edge.VersionRange!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Staging is best effort.
        }
    }
}
