using System.Text.Json;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Persistence;

/// <summary>User overrides for Chinese display names (modid -> 中文名).</summary>
public sealed class TranslationOverrideStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TranslationOverrideStore(AppPaths paths) => _paths = paths;

    public Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var text = AtomicFile.TryReadText(_paths.TranslationsFilePath);
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            return Task.FromResult(map is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    public async Task SaveAsync(
        IReadOnlyDictionary<string, string> overrides,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectories();
            var ordered = overrides
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value);

            await AtomicFile.WriteTextAsync(
                    _paths.TranslationsFilePath,
                    JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
