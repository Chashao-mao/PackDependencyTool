using System.Security.Cryptography;
using System.Text;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Persistence;

/// <summary>Per-pack manual relation store. One JSON document per pack, written atomically.</summary>
public sealed class ManualLinkStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ManualLinkStore(AppPaths paths) => _paths = paths;

    public string GetStorePath(string packRoot)
    {
        var key = PackKey(packRoot);
        return Path.Combine(_paths.ManualLinksDirectory, $"{key}.json");
    }

    public Task<List<ManualLink>> LoadAsync(
        string packRoot, CancellationToken cancellationToken = default)
    {
        var text = AtomicFile.TryReadText(GetStorePath(packRoot));
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(new List<ManualLink>());

        try
        {
            var file = JsonDefaults.Deserialize<ManualLinkFile>(text);
            return Task.FromResult(NormalizeLinks(file?.Links ?? []));
        }
        catch (Exception)
        {
            return Task.FromResult(new List<ManualLink>());
        }
    }

    public async Task SaveAsync(
        string packRoot,
        IEnumerable<ManualLink> links,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectories();
            var file = new ManualLinkFile
            {
                Version = 1,
                PackPath = packRoot,
                UpdatedAt = DateTimeOffset.UtcNow,
                Links = NormalizeLinks(links),
            };

            var json = JsonDefaults.Serialize(file);
            await AtomicFile.WriteTextAsync(GetStorePath(packRoot), json, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExportAsync(
        string exportPath,
        string packRoot,
        IEnumerable<ManualLink> links,
        CancellationToken cancellationToken = default)
    {
        var file = new ManualLinkFile
        {
            Version = 1,
            PackPath = packRoot,
            UpdatedAt = DateTimeOffset.UtcNow,
            Links = NormalizeLinks(links),
        };

        await AtomicFile.WriteTextAsync(exportPath, JsonDefaults.Serialize(file), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts { version, links: [...] } or a bare array. Invalid rows are collected in
    /// <see cref="ManualLinkImportResult.Errors"/> rather than aborting the whole import.
    /// </summary>
    public async Task<ManualLinkImportResult> ImportAsync(
        string importPath, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(importPath, cancellationToken).ConfigureAwait(false);
        var result = new ManualLinkImportResult();

        List<ManualLink>? links = null;
        try
        {
            var file = JsonDefaults.Deserialize<ManualLinkFile>(text);
            links = file?.Links;
        }
        catch (Exception)
        {
            // Try a bare array.
        }

        if (links is null)
        {
            try
            {
                links = JsonDefaults.Deserialize<List<ManualLink>>(text);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"无法解析 JSON：{ex.Message}");
                return result;
            }
        }

        if (links is null)
        {
            result.Errors.Add("文件中没有 links 数组。");
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (link is null)
            {
                result.Errors.Add("存在 null 关系项。");
                continue;
            }

            var from = NormalizeId(link.From);
            var to = NormalizeId(link.To);
            if (from.Length == 0 || to.Length == 0)
            {
                result.Errors.Add($"关系 {link.Id} 缺少 from/to。");
                continue;
            }

            if (from == to)
            {
                result.Errors.Add($"关系 {link.Id} 的 from 与 to 相同。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(link.Id) || !seen.Add(link.Id))
            {
                link.Id = Guid.NewGuid().ToString("N");
                seen.Add(link.Id);
            }

            link.From = from;
            link.To = to;
            link.UpdatedAt = DateTimeOffset.UtcNow;
            if (link.CreatedAt == default) link.CreatedAt = link.UpdatedAt;
            result.Links.Add(link);
        }

        return result;
    }

    public static string PackKey(string packRoot)
    {
        var normalized = Path.GetFullPath(packRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static List<ManualLink> NormalizeLinks(IEnumerable<ManualLink> links)
    {
        var result = new List<ManualLink>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in links)
        {
            if (link is null) continue;
            var from = NormalizeId(link.From);
            var to = NormalizeId(link.To);
            if (from.Length == 0 || to.Length == 0 || from == to) continue;

            link.From = from;
            link.To = to;
            if (string.IsNullOrWhiteSpace(link.Id) || !ids.Add(link.Id))
                link.Id = Guid.NewGuid().ToString("N");
            else
                ids.Add(link.Id);

            result.Add(link.Clone());
        }

        return result;
    }

    private static string NormalizeId(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
