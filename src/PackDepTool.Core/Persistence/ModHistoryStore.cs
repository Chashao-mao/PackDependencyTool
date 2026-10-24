using PackDepTool.Core.Models;

namespace PackDepTool.Core.Persistence;

public sealed class ModHistoryStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ModHistoryStore(AppPaths paths) => _paths = paths;

    public string FilePath(string packRoot) =>
        Path.Combine(_paths.HistoryDirectory, $"{ManualLinkStore.PackKey(packRoot)}.json");

    public async Task<ModHistoryFile> LoadAsync(string packRoot, CancellationToken cancellationToken = default)
    {
        var text = AtomicFile.TryReadText(FilePath(packRoot));
        if (string.IsNullOrWhiteSpace(text))
            return new ModHistoryFile { PackPath = packRoot };

        try
        {
            var file = JsonDefaults.Deserialize<ModHistoryFile>(text);
            if (file is null) return new ModHistoryFile { PackPath = packRoot };
            file.PackPath = packRoot;
            file.Entries ??= [];
            return file;
        }
        catch (Exception)
        {
            return new ModHistoryFile { PackPath = packRoot };
        }
    }

    public async Task SaveAsync(
        string packRoot,
        ModHistoryFile file,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectories();
            file.PackPath = packRoot;
            file.UpdatedAt = DateTimeOffset.UtcNow;
            file.Entries = file.Entries
                .OrderBy(entry => entry.Timestamp)
                .TakeLast(100)
                .ToList();

            await AtomicFile.WriteTextAsync(
                    FilePath(packRoot), JsonDefaults.Serialize(file), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ClearAsync(string packRoot, CancellationToken cancellationToken = default) =>
        SaveAsync(packRoot, new ModHistoryFile { PackPath = packRoot }, cancellationToken);
}
