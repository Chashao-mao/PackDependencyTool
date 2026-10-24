using System.Security.Cryptography;
using System.Text;
using PackDepTool.Core.Models;
using PackDepTool.Core.Persistence;

namespace PackDepTool.Core.Net;

public sealed class EnrichmentOptions
{
    public bool UseModrinth { get; set; } = true;
    public bool UseCurseForge { get; set; } = true;
    public bool AddDependencyEdges { get; set; } = true;
    public int MaxModrinthSearches { get; set; } = 80;
    public int MaxCurseForgeSearches { get; set; } = 80;
    public int MaxCurseForgeDependencyLookups { get; set; } = 60;
}

public sealed class EnrichmentStats
{
    public int ModrinthHashMatched { get; set; }
    public int ModrinthSearchMatched { get; set; }
    public int CurseForgeMatched { get; set; }
    public int EdgesAdded { get; set; }
    public int MissingProjectsCreated { get; set; }
    public int Errors { get; set; }
    public bool CurseForgeSkipped { get; set; }
    public bool CurseForgeAuthFailed { get; set; }
    public List<string> Messages { get; } = [];

    public string Summary()
    {
        var parts = new List<string>();
        if (ModrinthHashMatched > 0) parts.Add($"Modrinth 哈希匹配 {ModrinthHashMatched}");
        if (ModrinthSearchMatched > 0) parts.Add($"Modrinth 搜索匹配 {ModrinthSearchMatched}");
        if (CurseForgeMatched > 0) parts.Add($"CurseForge 匹配 {CurseForgeMatched}");
        if (EdgesAdded > 0) parts.Add($"新增联网依赖 {EdgesAdded}");
        if (MissingProjectsCreated > 0) parts.Add($"推断缺失项目 {MissingProjectsCreated}");
        if (Errors > 0) parts.Add($"失败 {Errors}");
        return parts.Count == 0 ? "没有可补全的数据" : string.Join("，", parts);
    }
}

public sealed record EnrichmentProgress(string Phase, int Done, int Total, string Message = "");

/// <summary>
/// Completes project identity (slug / project ids / titles) and dependency edges from
/// Modrinth (exact SHA-1 matching + strict slug/title search) and CurseForge
/// (strict slug/title search). Ambiguous results are ignored instead of guessed.
/// </summary>
public sealed class ModEnrichmentService
{
    private readonly ModrinthClient? _modrinth;
    private readonly CurseForgeClient? _curseForge;
    private readonly ApiKeyProvider _keys;

    public ModEnrichmentService(
        ModrinthClient? modrinth,
        CurseForgeClient? curseForge,
        ApiKeyProvider keys)
    {
        _modrinth = modrinth;
        _curseForge = curseForge;
        _keys = keys;
    }

    public async Task<EnrichmentStats> EnrichAsync(
        GraphDocument graph,
        PackProfile profile,
        EnrichmentOptions options,
        IProgress<EnrichmentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stats = new EnrichmentStats();
        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var manualPairs = graph.Edges
            .Where(e => e.ManualOverride)
            .Select(e => $"{e.From}\u0000{e.To}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Local jar metadata is authoritative for a loaded pair. Online data may
        // fill gaps, but must not add a contradictory second relation (for
        // example optional locally plus required online) for the same pair.
        var edgeKeys = graph.Edges
            .Select(e => $"{e.From}\u0000{e.To}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (options.UseModrinth && _modrinth is not null)
        {
            await EnrichModrinthAsync(graph, profile, options, nodes, manualPairs, edgeKeys, stats,
                progress, cancellationToken).ConfigureAwait(false);
        }

        if (options.UseCurseForge && _curseForge is not null)
        {
            await EnrichCurseForgeAsync(graph, profile, options, nodes, manualPairs, edgeKeys, stats,
                progress, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new EnrichmentProgress("联网补全完成", 1, 1, stats.Summary()));
        return stats;
    }

    // ---------------------------------------------------------------- Modrinth

    private async Task EnrichModrinthAsync(
        GraphDocument graph,
        PackProfile profile,
        EnrichmentOptions options,
        Dictionary<string, ModNode> nodes,
        HashSet<string> manualPairs,
        HashSet<string> edgeKeys,
        EnrichmentStats stats,
        IProgress<EnrichmentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var installed = graph.Nodes
            .Where(n => !n.IsSynthetic && !string.IsNullOrWhiteSpace(n.Sha1))
            .ToList();

        var hashes = installed.Select(n => n.Sha1!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var matchedByHash = new Dictionary<string, ModrinthVersionInfo>(StringComparer.OrdinalIgnoreCase);
        if (hashes.Count > 0)
        {
            progress?.Report(new EnrichmentProgress("Modrinth 哈希匹配", 0, 1, $"{hashes.Count} 个 JAR"));
            try
            {
                matchedByHash = await _modrinth!.MatchBySha1Async(hashes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ModrinthApiException or HttpRequestException)
            {
                stats.Errors++;
                stats.Messages.Add($"Modrinth 哈希匹配失败：{ex.Message}");
                return;
            }
        }

        // A single JAR can expose more than one mod id (multi-mod jar), so the
        // SHA1 -> node mapping is one-to-many. Using ToDictionary here caused
        // "An item with the same key has already been added" and aborted enrichment.
        var nodesByHash = installed
            .Where(n => matchedByHash.ContainsKey(n.Sha1!))
            .GroupBy(n => n.Sha1!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (hash, version) in matchedByHash)
        {
            if (!nodesByHash.TryGetValue(hash, out var matchedNodes)) continue;
            foreach (var node in matchedNodes)
                node.ProjectIds.Modrinth = version.ProjectId;
            stats.ModrinthHashMatched++;
        }

        // Fetch projects for matched nodes and dependency targets in one batch.
        var projectIds = matchedByHash.Values.Select(v => v.ProjectId)
            .Concat(matchedByHash.Values.SelectMany(v => v.Dependencies).Select(d => d.ProjectId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var projects = new Dictionary<string, ModrinthProjectInfo>(StringComparer.OrdinalIgnoreCase);
        if (projectIds.Count > 0)
        {
            progress?.Report(new EnrichmentProgress("Modrinth 项目信息", 0, projectIds.Count));
            try
            {
                foreach (var project in await _modrinth!.GetProjectsAsync(projectIds, cancellationToken)
                             .ConfigureAwait(false))
                    projects[project.Id] = project;
            }
            catch (Exception ex) when (ex is ModrinthApiException or HttpRequestException)
            {
                stats.Errors++;
                stats.Messages.Add($"Modrinth 项目信息获取失败：{ex.Message}");
            }
        }

        foreach (var (_, version) in matchedByHash)
        {
            if (!projects.TryGetValue(version.ProjectId, out var project)) continue;
            var node = graph.Nodes.FirstOrDefault(n =>
                string.Equals(n.ProjectIds.Modrinth, version.ProjectId, StringComparison.OrdinalIgnoreCase));
            if (node is null) continue;

            node.Slug ??= project.Slug;
            node.IconUrl ??= project.IconUrl;
            node.ProjectTitle ??= project.Title;
            if (string.IsNullOrWhiteSpace(node.Description))
                node.Description = project.Description;
            if (string.Equals(node.Name, node.Id, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(project.Title))
                node.Name = project.Title;
        }

        // Dependency edges from the matched Modrinth versions.
        if (options.AddDependencyEdges)
        {
            var processed = 0;
            foreach (var (hash, version) in matchedByHash)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!nodesByHash.TryGetValue(hash, out var matchedNodes)) continue;
                foreach (var fromNode in matchedNodes)
                {
                    foreach (var dependency in version.Dependencies)
                    {
                        if (string.IsNullOrWhiteSpace(dependency.ProjectId)) continue;
                        if (!projects.TryGetValue(dependency.ProjectId, out var targetProject)) continue;

                        var target = FindInstalledNode(graph, targetProject);
                        if (target is null)
                        {
                            if (dependency.Type != EdgeType.Requires) continue;

                            target = CreateSyntheticNode(
                                graph, nodes, targetProject, EdgeSource.Modrinth,
                                $"未安装（由 Modrinth 项目依赖推断）");
                            stats.MissingProjectsCreated++;
                        }

                        var edge = new DependencyEdge
                        {
                            Id = OnlineEdgeId("modrinth", fromNode.Id, target.Id, dependency.Type),
                            From = fromNode.Id,
                            To = target.Id,
                            Type = dependency.Type,
                            Source = EdgeSource.Modrinth,
                            Confidence = 0.75,
                            Note = "来源：Modrinth 项目依赖",
                        };

                        if (TryAddEdge(graph, manualPairs, edgeKeys, edge)) stats.EdgesAdded++;
                    }
                }

                processed++;
                if ((processed & 7) == 0)
                    progress?.Report(new EnrichmentProgress(
                        "Modrinth 依赖", processed, matchedByHash.Count, matchedNodes[0].Name));
            }
        }

        // Strict search for installed mods that were not matched by hash.
        var searchTargets = graph.Nodes
            .Where(n => !n.IsSynthetic && string.IsNullOrWhiteSpace(n.ProjectIds.Modrinth))
            .Take(options.MaxModrinthSearches)
            .ToList();

        var searched = 0;
        foreach (var node in searchTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var hits = await _modrinth!.SearchAsync(node.Id, cancellationToken).ConfigureAwait(false);
                var match = hits.FirstOrDefault(h =>
                    string.Equals(h.Slug, node.Id, StringComparison.OrdinalIgnoreCase) ||
                    Normalize(h.Slug) == Normalize(node.Id) ||
                    Normalize(h.Title) == Normalize(node.Name) ||
                    Normalize(h.Title) == Normalize(node.Id));

                if (match is not null)
                {
                    node.ProjectIds.Modrinth = match.Id;
                    node.Slug ??= match.Slug;
                    node.IconUrl ??= match.IconUrl;
                    node.ProjectTitle ??= match.Title;
                    if (string.Equals(node.Name, node.Id, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(match.Title))
                        node.Name = match.Title;
                    stats.ModrinthSearchMatched++;
                }
            }
            catch (Exception ex) when (ex is ModrinthApiException or HttpRequestException)
            {
                stats.Errors++;
                stats.Messages.Add($"Modrinth 搜索失败（{node.Id}）：{ex.Message}");
                if (ex is ModrinthApiException { StatusCode: 429 }) break;
            }

            searched++;
            progress?.Report(new EnrichmentProgress(
                "Modrinth 搜索匹配", searched, searchTargets.Count, node.Name));
        }
    }

    // ---------------------------------------------------------------- CurseForge

    private async Task EnrichCurseForgeAsync(
        GraphDocument graph,
        PackProfile profile,
        EnrichmentOptions options,
        Dictionary<string, ModNode> nodes,
        HashSet<string> manualPairs,
        HashSet<string> edgeKeys,
        EnrichmentStats stats,
        IProgress<EnrichmentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var targets = graph.Nodes
            .Where(n => !n.IsSynthetic && string.IsNullOrWhiteSpace(n.ProjectIds.CurseForge))
            .Take(options.MaxCurseForgeSearches)
            .ToList();

        var matchedMods = new Dictionary<string, CurseForgeModInfo>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        foreach (var node in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var mod = await _curseForge!.FindByIdentityAsync(node.Id, node.Name, cancellationToken)
                    .ConfigureAwait(false);
                if (mod is not null)
                {
                    node.ProjectIds.CurseForge = mod.Id.ToString();
                    node.Slug ??= mod.Slug;
                    node.IconUrl ??= mod.IconUrl;
                    node.ProjectTitle ??= mod.Name;
                    if (string.IsNullOrWhiteSpace(node.Description))
                        node.Description = mod.Summary;
                    if (string.Equals(node.Name, node.Id, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(mod.Name))
                        node.Name = mod.Name;

                    matchedMods[node.Id] = mod;
                    stats.CurseForgeMatched++;
                }
            }
            catch (MissingApiKeyException)
            {
                stats.CurseForgeSkipped = true;
                return;
            }
            catch (CurseForgeApiException ex) when (ex.IsAuthFailure)
            {
                stats.CurseForgeAuthFailed = true;
                stats.Messages.Add("CurseForge API Key 无效或已过期，已停止补全。");
                return;
            }
            catch (Exception ex) when (ex is CurseForgeApiException or HttpRequestException)
            {
                stats.Errors++;
                stats.Messages.Add($"CurseForge 搜索失败（{node.Id}）：{ex.Message}");
                if (ex is CurseForgeApiException { StatusCode: 429 }) break;
            }

            done++;
            progress?.Report(new EnrichmentProgress(
                "CurseForge 项目匹配", done, targets.Count, node.Name));
        }

        if (!options.AddDependencyEdges || matchedMods.Count == 0) return;

        // Resolve dependency targets in batches of 50 mod ids.
        var unresolved = new HashSet<long>();
        var parsedDeps = new List<(string FromId, CurseForgeDependencyInfo Dependency)>();
        foreach (var (nodeId, mod) in matchedMods)
        {
            var file = SelectCurseForgeFile(mod, profile);
            if (file is null) continue;
            foreach (var dependency in file.Dependencies)
            {
                if (dependency.ToEdgeType() is null) continue;
                if (dependency.ModId == mod.Id) continue;
                parsedDeps.Add((nodeId, dependency));
                if (!graph.Nodes.Any(n => n.ProjectIds.CurseForge == dependency.ModId.ToString()))
                    unresolved.Add(dependency.ModId);
            }
        }

        var targetMods = new Dictionary<long, CurseForgeModInfo>();
        if (unresolved.Count > 0)
        {
            try
            {
                var fetched = await _curseForge!.GetModsAsync(
                        unresolved.Take(options.MaxCurseForgeDependencyLookups).ToList(), cancellationToken)
                    .ConfigureAwait(false);
                foreach (var mod in fetched) targetMods[mod.Id] = mod;
            }
            catch (Exception ex) when (ex is CurseForgeApiException or HttpRequestException or MissingApiKeyException)
            {
                stats.Errors++;
                stats.Messages.Add($"CurseForge 依赖项目查询失败：{ex.Message}");
            }
        }

        foreach (var (fromId, dependency) in parsedDeps)
        {
            var type = dependency.ToEdgeType();
            if (type is null) continue;
            if (!nodes.TryGetValue(fromId, out var fromNode)) continue;

            var target = graph.Nodes.FirstOrDefault(n =>
                n.ProjectIds.CurseForge == dependency.ModId.ToString());

            if (target is null && targetMods.TryGetValue(dependency.ModId, out var targetMod))
                target = FindInstalledNode(graph, targetMod);

            if (target is null && targetMods.TryGetValue(dependency.ModId, out var createFrom))
            {
                if (type != EdgeType.Requires) continue;
                target = CreateSyntheticNode(
                    graph, nodes, createFrom, EdgeSource.CurseForge,
                    "未安装（由 CurseForge 文件依赖推断）");
                stats.MissingProjectsCreated++;
            }

            if (target is null) continue;

            var edge = new DependencyEdge
            {
                Id = OnlineEdgeId("curseforge", fromNode.Id, target.Id, type.Value),
                From = fromNode.Id,
                To = target.Id,
                Type = type.Value,
                Source = EdgeSource.CurseForge,
                Confidence = 0.7,
                Note = "来源：CurseForge 文件依赖",
            };

            if (TryAddEdge(graph, manualPairs, edgeKeys, edge)) stats.EdgesAdded++;
        }
    }

    private static CurseForgeFileInfo? SelectCurseForgeFile(CurseForgeModInfo mod, PackProfile profile)
    {
        if (mod.LatestFiles.Count == 0) return null;

        var loaderId = profile.Loader switch
        {
            LoaderKind.Forge => "1",
            LoaderKind.Fabric => "4",
            LoaderKind.Quilt => "5",
            LoaderKind.NeoForge => "6",
            _ => null,
        };

        var index = mod.LatestFilesIndexes.FirstOrDefault(i =>
            (string.IsNullOrWhiteSpace(i.GameVersion) ||
             string.Equals(i.GameVersion, profile.McVersion, StringComparison.OrdinalIgnoreCase) ||
             profile.McVersion.StartsWith(i.GameVersion + ".", StringComparison.OrdinalIgnoreCase) ||
             i.GameVersion.StartsWith(profile.McVersion + ".", StringComparison.OrdinalIgnoreCase)) &&
            (loaderId is null || string.IsNullOrWhiteSpace(i.ModLoader) || i.ModLoader == loaderId));

        if (index is not null)
        {
            var byId = mod.LatestFiles.FirstOrDefault(f => f.Id == index.FileId);
            if (byId is not null) return byId;
        }

        return mod.LatestFiles.FirstOrDefault(f => f.Dependencies.Count > 0) ?? mod.LatestFiles[0];
    }

    private static ModNode? FindInstalledNode(GraphDocument graph, ModrinthProjectInfo project)
    {
        return graph.Nodes.FirstOrDefault(n =>
            !n.IsSynthetic &&
            (string.Equals(n.ProjectIds.Modrinth, project.Id, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(n.Id, project.Slug, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(n.Slug, project.Slug, StringComparison.OrdinalIgnoreCase)));
    }

    private static ModNode? FindInstalledNode(GraphDocument graph, CurseForgeModInfo project)
    {
        var strict = graph.Nodes.FirstOrDefault(n =>
            !n.IsSynthetic &&
            (string.Equals(n.ProjectIds.CurseForge, project.Id.ToString(), StringComparison.OrdinalIgnoreCase) ||
             string.Equals(n.Id, project.Slug, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(n.Slug, project.Slug, StringComparison.OrdinalIgnoreCase)));
        if (strict is not null) return strict;

        // Display names are not unique. Use them only when exactly one installed
        // mod has that normalized name; otherwise report no match instead of
        // attaching an online dependency to the wrong project.
        var byName = graph.Nodes
            .Where(n => !n.IsSynthetic && Normalize(n.Name) == Normalize(project.Name))
            .ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    private static ModNode CreateSyntheticNode(
        GraphDocument graph,
        Dictionary<string, ModNode> nodes,
        ModrinthProjectInfo project,
        EdgeSource source,
        string reason)
    {
        var id = NormalizeId(string.IsNullOrWhiteSpace(project.Slug) ? project.Id : project.Slug);
        if (nodes.TryGetValue(id, out var existing)) return existing;

        var node = new ModNode
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(project.Title) ? id : project.Title,
            Version = "unknown",
            Loader = LoaderKind.Unknown,
            Source = source,
            Status = ModStatus.Missing,
            StatusReason = reason,
            IsSynthetic = true,
            ProjectIds = new ProjectIds { Modrinth = project.Id, Slug = project.Slug },
            Slug = project.Slug,
            ProjectTitle = project.Title,
            Description = project.Description,
        };

        nodes[id] = node;
        graph.Nodes.Add(node);
        return node;
    }

    private static ModNode CreateSyntheticNode(
        GraphDocument graph,
        Dictionary<string, ModNode> nodes,
        CurseForgeModInfo project,
        EdgeSource source,
        string reason)
    {
        var id = NormalizeId(string.IsNullOrWhiteSpace(project.Slug) ? project.Id.ToString() : project.Slug);
        if (nodes.TryGetValue(id, out var existing)) return existing;

        var node = new ModNode
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(project.Name) ? id : project.Name,
            Version = "unknown",
            Loader = LoaderKind.Unknown,
            Source = source,
            Status = ModStatus.Missing,
            StatusReason = reason,
            IsSynthetic = true,
            ProjectIds = new ProjectIds { CurseForge = project.Id.ToString(), Slug = project.Slug },
            Slug = project.Slug,
            ProjectTitle = project.Name,
            Description = project.Summary,
        };

        nodes[id] = node;
        graph.Nodes.Add(node);
        return node;
    }

    private static bool TryAddEdge(
        GraphDocument graph,
        HashSet<string> manualPairs,
        HashSet<string> edgeKeys,
        DependencyEdge edge)
    {
        if (manualPairs.Contains($"{edge.From}\u0000{edge.To}")) return false;
        if (!edgeKeys.Add($"{edge.From}\u0000{edge.To}")) return false;
        if (graph.Edges.Any(e => e.Id == edge.Id)) return false;
        graph.Edges.Add(edge);
        return true;
    }

    private static string OnlineEdgeId(string platform, string from, string to, EdgeType type)
    {
        var raw = $"{platform}\u0000{from}\u0000{to}\u0000{type}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw))[..4]);
        return $"{platform}:{from}->{to}:{type.ToString().ToLowerInvariant()}:{hash}";
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static string NormalizeId(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0) return "unknown";
        return normalized.Replace(' ', '-');
    }
}
