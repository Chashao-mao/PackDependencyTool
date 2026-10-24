using System.Security.Cryptography;
using System.Text;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Analysis;

public sealed class GraphBuildOptions
{
    public bool IncludeEmbeddedNodes { get; set; } = true;
    public bool IncludePlatformDependencies { get; set; }
    public bool ResolveProvides { get; set; } = true;
}

public sealed class GraphBuildResult
{
    public required GraphDocument Graph { get; init; }
    public required ScanResult Scan { get; init; }
    public List<AnalysisIssue> Issues { get; init; } = [];
}

/// <summary>
/// Turns parsed jar metadata plus persisted manual links into the stable graph document.
/// Manual links win for their directed pair: auto edges for the same
/// (from, to) pair are suppressed. Reverse-direction edges remain separate.
/// </summary>
public sealed class DependencyGraphBuilder
{
    private readonly GraphBuildOptions _options;
    private readonly GraphAnalyzer _analyzer;

    public DependencyGraphBuilder(GraphBuildOptions? options = null, GraphAnalyzer? analyzer = null)
    {
        _options = options ?? new GraphBuildOptions();
        _analyzer = analyzer ?? new GraphAnalyzer();
    }

    public GraphBuildResult Build(
        ScanResult scan,
        IEnumerable<ManualLink>? manualLinks = null,
        IEnumerable<LocalRule>? localRules = null)
    {
        var ruleSet = new LocalRuleSet(localRules);
        var graph = new GraphDocument
        {
            Meta = new GraphMeta
            {
                McVersion = string.IsNullOrWhiteSpace(scan.Profile.McVersion) ? "unknown" : scan.Profile.McVersion,
                Loader = scan.Profile.LoaderKey,
                GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
                PackName = scan.Profile.PackName,
                RootPath = scan.RootPath,
                LoaderVersion = scan.Profile.LoaderVersion,
            },
        };

        var issues = new List<AnalysisIssue>();
        var nodes = new Dictionary<string, ModNode>(StringComparer.OrdinalIgnoreCase);
        var provides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var edgeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parentByJar = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // 1. Nodes from installed jars.
        foreach (var jar in scan.Jars)
        {
            foreach (var parsed in jar.Mods)
            {
                var id = Normalize(parsed.ModId);
                if (id.Length == 0)
                {
                    issues.Add(new AnalysisIssue
                    {
                        Severity = IssueSeverity.Warning,
                        Kind = IssueKind.ParseWarning,
                        Title = "模组缺少 id",
                        Message = $"{jar.FilePath} 中的元数据没有 modId，已跳过。",
                    });
                    continue;
                }

                if (nodes.TryGetValue(id, out var existing))
                {
                    // Same mod id shipped twice (duplicate jar or multi-mod jar).
                    MergeParsed(existing, parsed);
                    issues.Add(new AnalysisIssue
                    {
                        Severity = IssueSeverity.Warning,
                        Kind = IssueKind.DuplicateModId,
                        Title = "重复的 modId",
                        Message = $"modId \"{id}\" 在 {existing.FilePath} 与 {jar.FilePath} 中重复，已合并元数据。",
                        NodeId = id,
                    });
                    continue;
                }

                var node = new ModNode
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(parsed.Name) ? id : parsed.Name,
                    Version = string.IsNullOrWhiteSpace(parsed.Version) ? "unknown" : parsed.Version,
                    Loader = parsed.Loader,
                    McVersions = parsed.McVersions.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    McVersionRange = parsed.McVersionRange,
                    FilePath = jar.FilePath,
                    FileModifiedAt = jar.ModifiedAt,
                    Source = EdgeSource.Local,
                    Status = parsed.Loader == LoaderKind.Unknown ? ModStatus.Unknown : ModStatus.Ok,
                    Description = parsed.Description,
                    Authors = parsed.Authors,
                    Side = parsed.Side,
                    Sha1 = jar.Sha1,
                    Provides = parsed.Provides.Select(Normalize).Where(p => p.Length > 0).Distinct().ToList(),
                    IconData = parsed.IconData,
                };

                nodes[id] = node;
                graph.Nodes.Add(node);

                foreach (var provided in node.Provides)
                {
                    if (!provides.ContainsKey(provided))
                        provides[provided] = id;
                }

                parentByJar.TryAdd(jar.FilePath, []);
                parentByJar[jar.FilePath].Add(id);
            }
        }

        // 2. Dependency edges from jar metadata.
        ApplyAliases(ruleSet, nodes, provides);

        foreach (var jar in scan.Jars)
        {
            foreach (var parsed in jar.Mods)
            {
                var fromId = Normalize(parsed.ModId);
                if (fromId.Length == 0 || !nodes.ContainsKey(fromId)) continue;

                foreach (var dependency in parsed.Dependencies)
                {
                    var rawTarget = Normalize(dependency.ModId);
                    if (rawTarget.Length == 0) continue;

                    if (PlatformModIds.IsPlatform(rawTarget) && !_options.IncludePlatformDependencies)
                        continue;

                    if (rawTarget == fromId)
                    {
                        issues.Add(new AnalysisIssue
                        {
                            Severity = IssueSeverity.Warning,
                            Kind = IssueKind.SelfDependency,
                            Title = "模组依赖自身",
                            Message = $"\"{fromId}\" 声明依赖自身，已忽略。",
                            NodeId = fromId,
                        });
                        continue;
                    }

                    var targetId = rawTarget;
                    if (_options.ResolveProvides && !nodes.ContainsKey(targetId) && provides.TryGetValue(targetId, out var provider))
                        targetId = provider;

                    if (dependency.Type == EdgeType.Incompatible && !nodes.ContainsKey(targetId))
                        continue; // Nothing installed to conflict with.

                    if (!nodes.TryGetValue(targetId, out var target))
                    {
                        target = CreateSyntheticNode(targetId, dependency.Type == EdgeType.Requires ? ModStatus.Missing : ModStatus.Unknown);
                        nodes[targetId] = target;
                        graph.Nodes.Add(target);
                    }

                    if (target.IsSynthetic)
                    {
                        if (dependency.Type == EdgeType.Optional)
                            target.IsOptionalDependency = true;
                        else if (dependency.Type == EdgeType.Recommends)
                            target.IsRecommendedDependency = true;
                        else if (dependency.Type == EdgeType.Requires)
                        {
                            target.IsOptionalDependency = false;
                            target.IsRecommendedDependency = false;
                        }
                    }

                    var overrideRange = ruleSet.OverrideRange(fromId, targetId);
                    var edgeRange = overrideRange ?? dependency.VersionRange;
                    var edgeNote = dependency.Note;
                    if (overrideRange is not null)
                    {
                        edgeNote = string.IsNullOrWhiteSpace(edgeNote)
                            ? "版本范围由本地规则覆盖"
                            : edgeNote + "；版本范围由本地规则覆盖";
                    }

                    AddEdge(graph, nodes, edgeKeys, new DependencyEdge
                    {
                        Id = AutoEdgeId(fromId, targetId, dependency.Type, edgeRange),
                        From = fromId,
                        To = targetId,
                        Type = dependency.Type,
                        VersionRange = edgeRange,
                        Side = dependency.Side,
                        Source = EdgeSource.Local,
                        Confidence = 1.0,
                        Note = edgeNote,
                        ManualOverride = false,
                    });
                }
            }
        }

        // 3. Embedded (Jar-in-Jar) mods.
        if (_options.IncludeEmbeddedNodes)
        {
            foreach (var jar in scan.Jars)
            {
                var parents = parentByJar.GetValueOrDefault(jar.FilePath) ?? [];
                foreach (var embedded in jar.Embedded)
                {
                    var embeddedId = Normalize(embedded.ModId);
                    if (embeddedId.Length == 0) continue;

                    if (!nodes.TryGetValue(embeddedId, out var embeddedNode))
                    {
                        embeddedNode = new ModNode
                        {
                            Id = embeddedId,
                            Name = string.IsNullOrWhiteSpace(embedded.Name) ? embeddedId : embedded.Name,
                            Version = string.IsNullOrWhiteSpace(embedded.Version) ? "unknown" : embedded.Version!,
                            Loader = embedded.Loader,
                            FilePath = jar.FilePath,
                            FileModifiedAt = jar.ModifiedAt,
                            Source = EdgeSource.Local,
                            Status = ModStatus.Ok,
                            IsEmbedded = true,
                        };
                        nodes[embeddedId] = embeddedNode;
                        graph.Nodes.Add(embeddedNode);
                    }
                    else
                    {
                        // A standalone installation wins over an embedded copy with
                        // the same id. Otherwise the real mod is marked embedded and
                        // disappears from the main list (e.g. Sodium bundled by
                        // another jar).
                        if (!embeddedNode.IsSynthetic && !embeddedNode.IsEmbedded)
                            continue;

                        // The embedded jar satisfies a dependency that was previously missing.
                        if (embeddedNode.IsSynthetic)
                        {
                            embeddedNode.IsSynthetic = false;
                            embeddedNode.Status = ModStatus.Ok;
                            embeddedNode.StatusReason = null;
                        }

                        embeddedNode.IsEmbedded = true;
                        embeddedNode.FilePath ??= jar.FilePath;
                        if (embeddedNode.Version == "unknown" && !string.IsNullOrWhiteSpace(embedded.Version))
                            embeddedNode.Version = embedded.Version!;
                    }

                    foreach (var parent in parents)
                    {
                        if (parent == embeddedId) continue;
                        AddEdge(graph, nodes, edgeKeys, new DependencyEdge
                        {
                            Id = AutoEdgeId(parent, embeddedId, EdgeType.Embedded, null),
                            From = parent,
                            To = embeddedId,
                            Type = EdgeType.Embedded,
                            Source = EdgeSource.Local,
                            Confidence = 1.0,
                            Note = $"内嵌 JAR：{embedded.File}",
                            ManualOverride = false,
                        });
                    }
                }
            }
        }

        // 4. Manual links override everything for their (from, to) pair.
        var manualPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = (manualLinks ?? Enumerable.Empty<ManualLink>()).Where(l => l is not null).ToList();
        foreach (var link in links)
        {
            var from = Normalize(link.From);
            var to = Normalize(link.To);
            if (from.Length == 0 || to.Length == 0)
            {
                issues.Add(InvalidManualLink(link, "起点或终点为空"));
                continue;
            }

            if (from == to)
            {
                issues.Add(InvalidManualLink(link, "起点与终点不能相同"));
                continue;
            }

            if (!nodes.TryGetValue(from, out var fromNode))
            {
                fromNode = CreateSyntheticNode(from, ModStatus.Unknown);
                fromNode.Source = EdgeSource.Manual;
                nodes[from] = fromNode;
                graph.Nodes.Add(fromNode);
            }

            if (!nodes.TryGetValue(to, out var toNode))
            {
                toNode = CreateSyntheticNode(to, link.Type == EdgeType.Requires ? ModStatus.Missing : ModStatus.Unknown);
                toNode.Source = EdgeSource.Manual;
                toNode.IsOptionalDependency = link.Type == EdgeType.Optional;
                toNode.IsRecommendedDependency = link.Type == EdgeType.Recommends;
                nodes[to] = toNode;
                graph.Nodes.Add(toNode);
            }

            manualPairs.Add($"{from}\u0000{to}");
        }

        if (manualPairs.Count > 0)
        {
            graph.Edges.RemoveAll(e =>
                !e.ManualOverride && manualPairs.Contains($"{e.From}\u0000{e.To}"));
        }

        foreach (var link in links)
        {
            var from = Normalize(link.From);
            var to = Normalize(link.To);
            if (from.Length == 0 || to.Length == 0 || from == to) continue;

            AddEdge(graph, nodes, edgeKeys, new DependencyEdge
            {
                Id = $"manual:{link.Id}",
                From = from,
                To = to,
                Type = link.Type,
                VersionRange = link.VersionRange,
                Side = link.Side,
                Source = EdgeSource.Manual,
                Confidence = 1.0,
                Note = link.Note,
                ManualOverride = true,
                ManualLinkId = link.Id,
            }, allowDuplicate: true);
        }

        // 5. Obvious structural conflicts.
        foreach (var edge in graph.Edges.Where(e => e.Type == EdgeType.Incompatible))
        {
            if (!nodes.TryGetValue(edge.From, out var fromNode) || !nodes.TryGetValue(edge.To, out var toNode))
                continue;
            if (fromNode.IsSynthetic || toNode.IsSynthetic) continue;
            if (ruleSet.IsConflictIgnored(fromNode.Id, toNode.Id)) continue;

            // A version-scoped declaration only applies when the installed
            // target version is known and currently falls inside that range.
            // An unparsable/unknown version stays a metadata warning only.
            if (VersionRange.Satisfies(edge.VersionRange, toNode.Version) != true) continue;

            Escalate(fromNode, ModStatus.Conflict, $"与 {toNode.Name} 声明不兼容");
            Escalate(toNode, ModStatus.Conflict, $"与 {fromNode.Name} 声明不兼容");
        }

        // 6. Apply exclusions after manual overrides so hidden mods cannot reappear.
        var excludedIds = graph.Nodes
            .Where(node => ruleSet.IsExcluded(node.Id))
            .Select(node => node.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (excludedIds.Count > 0)
        {
            graph.Nodes.RemoveAll(node => excludedIds.Contains(node.Id));
            graph.Edges.RemoveAll(edge => excludedIds.Contains(edge.From) || excludedIds.Contains(edge.To));
            issues.Add(new AnalysisIssue
            {
                Severity = IssueSeverity.Info,
                Kind = IssueKind.Unknown,
                Title = "本地规则排除",
                Message = $"已按本地规则排除 {excludedIds.Count} 个模组：{string.Join(", ", excludedIds.OrderBy(id => id, StringComparer.Ordinal))}",
            });
        }

        // 7. Surface scan-level warnings as informational issues.
        foreach (var warning in scan.Warnings.Take(200))
        {
            issues.Add(new AnalysisIssue
            {
                Severity = IssueSeverity.Info,
                Kind = IssueKind.ParseWarning,
                Title = "扫描提示",
                Message = warning,
            });
        }
        foreach (var warning in scan.Profile.Warnings.Take(50))
        {
            issues.Add(new AnalysisIssue
            {
                Severity = IssueSeverity.Info,
                Kind = IssueKind.ParseWarning,
                Title = "整合包信息",
                Message = warning,
            });
        }

        issues.AddRange(_analyzer.Analyze(graph, scan.Profile, ruleSet));

        if (!ruleSet.IsEmpty)
        {
            issues.Add(new AnalysisIssue
            {
                Severity = IssueSeverity.Info,
                Kind = IssueKind.Unknown,
                Title = "本地规则",
                Message = $"已启用 {ruleSet.Count} 条本地规则，分析结果已按其调整。规则只影响分析，不会修改任何 JAR。",
            });
        }

        InferMissingSides(graph);

        issues = issues
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.Kind)
            .ThenBy(i => i.Title, StringComparer.Ordinal)
            .ToList();

        return new GraphBuildResult
        {
            Graph = graph,
            Scan = scan,
            Issues = issues,
        };
    }

    public static void InferSides(GraphDocument graph) => InferMissingSides(graph);

    private static void InferMissingSides(GraphDocument graph)
    {
        foreach (var node in graph.Nodes.Where(node => !node.IsSynthetic && node.Side == ModSide.Unknown))
        {
            var sides = graph.Edges
                .Where(edge => string.Equals(edge.From, node.Id, StringComparison.OrdinalIgnoreCase))
                .Select(edge => edge.Side)
                .Where(side => side != ModSide.Unknown)
                .Distinct()
                .ToList();

            node.Side = sides.Count == 0
                ? ModSide.Both
                : sides.Contains(ModSide.Both) ||
                  (sides.Contains(ModSide.Client) && sides.Contains(ModSide.Server))
                    ? ModSide.Both
                    : sides[0];
            node.SideInferred = true;
        }
    }

    private static void ApplyAliases(
        LocalRuleSet rules,
        IReadOnlyDictionary<string, ModNode> nodes,
        Dictionary<string, string> provides)
    {
        foreach (var rule in rules.Rules.Where(rule => rule.Type == LocalRuleType.ProvideAlias))
        {
            var alias = Normalize(rule.From);
            var providerId = Normalize(rule.To);
            if (alias.Length == 0 || providerId.Length == 0) continue;
            if (string.Equals(alias, providerId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!nodes.TryGetValue(providerId, out var provider)) continue;
            if (provides.ContainsKey(alias)) continue;

            provides[alias] = providerId;
            if (!provider.Provides.Contains(alias, StringComparer.OrdinalIgnoreCase))
                provider.Provides.Add(alias);
        }
    }

    private static void MergeParsed(ModNode node, ParsedMod parsed)
    {
        foreach (var provided in parsed.Provides)
        {
            var normalized = Normalize(provided);
            if (normalized.Length > 0 && !node.Provides.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                node.Provides.Add(normalized);
        }

        foreach (var mc in parsed.McVersions)
        {
            if (!node.McVersions.Contains(mc, StringComparer.OrdinalIgnoreCase))
                node.McVersions.Add(mc);
        }

        // Dependencies are read from the scan result directly, so only merge display data.
        if (string.IsNullOrWhiteSpace(node.Description)) node.Description = parsed.Description;
        if (string.IsNullOrWhiteSpace(node.Authors)) node.Authors = parsed.Authors;
        if (node.IconData is null && parsed.IconData is not null) node.IconData = parsed.IconData;
    }

    private static ModNode CreateSyntheticNode(string id, ModStatus status) => new()
    {
        Id = id,
        Name = id,
        Version = "unknown",
        Loader = LoaderKind.Unknown,
        Source = EdgeSource.Local,
        Status = status,
        IsSynthetic = true,
        StatusReason = status == ModStatus.Missing ? "未安装" : null,
    };

    private static void AddEdge(
        GraphDocument graph,
        IReadOnlyDictionary<string, ModNode> nodes,
        ISet<string> edgeKeys,
        DependencyEdge edge,
        bool allowDuplicate = false)
    {
        if (edge.From == edge.To) return;

        var key = edge.Id;
        if (!allowDuplicate && !edgeKeys.Add(key)) return;

        // Only keep edges whose endpoints exist (they always should at this point).
        if (!nodes.ContainsKey(edge.From) || !nodes.ContainsKey(edge.To)) return;

        if (graph.Edges.Any(e => e.Id == edge.Id)) return;
        graph.Edges.Add(edge);
    }

    private static string AutoEdgeId(string from, string to, EdgeType type, string? range)
    {
        var raw = $"{from}\u0000{to}\u0000{type}\u0000{range}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw))[..4]);
        return $"auto:{from}->{to}:{type.ToString().ToLowerInvariant()}:{hash}";
    }

    private static AnalysisIssue InvalidManualLink(ManualLink link, string reason) => new()
    {
        Severity = IssueSeverity.Warning,
        Kind = IssueKind.InvalidManualLink,
        Title = "无效的手动关系",
        Message = $"{link.From} → {link.To}：{reason}",
    };

    private static void Escalate(ModNode node, ModStatus status, string reason)
    {
        if (Rank(status) <= Rank(node.Status)) return;
        node.Status = status;
        node.StatusReason = reason;
    }

    internal static int Rank(ModStatus status) => status switch
    {
        ModStatus.Ok => 0,
        ModStatus.Unknown => 1,
        ModStatus.VersionMismatch => 2,
        ModStatus.Missing => 3,
        ModStatus.Conflict => 4,
        _ => 0,
    };

    internal static string Normalize(string? id) => id?.Trim().ToLowerInvariant() ?? string.Empty;
}
