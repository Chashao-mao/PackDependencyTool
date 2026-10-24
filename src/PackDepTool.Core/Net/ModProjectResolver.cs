using PackDepTool.Core.Models;
using PackDepTool.Core.Persistence;

namespace PackDepTool.Core.Net;

/// <summary>Resolves a mod identity to a Modrinth/CurseForge project using exact matches only.</summary>
public sealed class ModProjectResolver
{
    private readonly ModrinthClient _modrinth;
    private readonly CurseForgeClient _curseForge;
    private readonly ApiKeyProvider _keys;

    public ModProjectResolver(ModrinthClient modrinth, CurseForgeClient curseForge, ApiKeyProvider keys)
    {
        _modrinth = modrinth;
        _curseForge = curseForge;
        _keys = keys;
    }

    public async Task<DownloadProject?> ResolveAsync(ModNode node, CancellationToken cancellationToken = default)
    {
        var queries = new[] { node.Id, node.Name }
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries)
        {
            var hits = await _modrinth.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            var match = hits.FirstOrDefault(hit =>
                Normalize(hit.Slug) == Normalize(node.Id) ||
                Normalize(hit.Title) == Normalize(node.Name) ||
                Normalize(hit.Title) == Normalize(node.Id));
            if (match is null) continue;

            return new DownloadProject
            {
                Source = DownloadSource.Modrinth,
                Type = DownloadProjectType.Mod,
                ProjectId = match.Id,
                Slug = match.Slug,
                Name = match.Title,
                Description = match.Description,
                IconUrl = match.IconUrl,
            };
        }

        var key = await _keys.GetCurseForgeKeyAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key)) return null;

        var curse = await _curseForge.FindByIdentityAsync(node.Id, node.Name, cancellationToken)
            .ConfigureAwait(false);
        if (curse is null) return null;

        return new DownloadProject
        {
            Source = DownloadSource.CurseForge,
            Type = DownloadProjectType.Mod,
            ProjectId = curse.Id.ToString(),
            Slug = curse.Slug,
            Name = curse.Name,
            Description = curse.Summary,
            IconUrl = curse.IconUrl,
        };
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }
}
