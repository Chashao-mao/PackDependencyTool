using System.IO.Compression;
using System.Text;
using PackDepTool.Core.Net;
using PackDepTool.Core.Persistence;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class DownloadImportTests
{
    [Fact]
    public async Task ImportsModrinthPackIndexAndOverridesWithoutNetwork()
    {
        using var temp = new TempDir();
        var archivePath = temp.Combine("pack.mrpack");
        await CreateArchiveAsync(archivePath, new Dictionary<string, string>
        {
            ["modrinth.index.json"] = """
            {
              "formatVersion": 1,
              "game": "minecraft",
              "name": "Imported Pack",
              "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.15.11" },
              "files": []
            }
            """,
            ["overrides/config/example.toml"] = "enabled = true\n",
            ["client-overrides/config/client-only.txt"] = "client\n",
            ["overrides/../../evil.txt"] = "should be skipped\n",
        });

        var importer = CreateImporter(temp);
        var result = await importer.ImportAsync(archivePath, temp.Combine("packs"));

        Assert.True(File.Exists(Path.Combine(result.PackRoot, "modrinth.index.json")));
        Assert.True(File.Exists(Path.Combine(result.PackRoot, "config", "example.toml")));
        Assert.True(File.Exists(Path.Combine(result.PackRoot, "config", "client-only.txt")));
        Assert.False(File.Exists(Path.Combine(temp.Combine("packs"), "evil.txt")));
        Assert.False(File.Exists(Path.Combine(result.PackRoot, "evil.txt")));
        Assert.Equal(2, result.ExtractedFiles);
    }

    [Fact]
    public async Task ImportsCurseForgePackWithoutApiKeyAsPartial()
    {
        using var temp = new TempDir();
        var archivePath = temp.Combine("cfpack.zip");
        await CreateArchiveAsync(archivePath, new Dictionary<string, string>
        {
            ["manifest.json"] = """
            {
              "manifestType": "minecraftModpack",
              "name": "CF Demo",
              "minecraft": { "version": "1.20.1", "modLoaders": [{ "id": "forge-47.2.0", "primary": true }] },
              "files": [{ "projectID": 123, "fileID": 456, "required": true }],
              "overrides": "overrides"
            }
            """,
            ["overrides/config/demo.cfg"] = "value\n",
        });

        var importer = CreateImporter(temp);
        var result = await importer.ImportAsync(archivePath, temp.Combine("packs"));

        Assert.True(File.Exists(Path.Combine(result.PackRoot, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(result.PackRoot, "config", "demo.cfg")));
        Assert.Contains(result.Warnings, warning => warning.Contains("CurseForge API Key", StringComparison.Ordinal));
        Assert.Single(result.MissingFiles);
        Assert.Equal(0, result.DownloadedFiles);
    }

    [Fact]
    public async Task GenericZipIsExtractedAndWarned()
    {
        using var temp = new TempDir();
        var archivePath = temp.Combine("plain.zip");
        await CreateArchiveAsync(archivePath, new Dictionary<string, string>
        {
            ["mods/example.jar"] = "not a real jar",
            ["config/settings.txt"] = "x",
        });

        var importer = CreateImporter(temp);
        var result = await importer.ImportAsync(archivePath, temp.Combine("packs"));

        Assert.True(File.Exists(Path.Combine(result.PackRoot, "mods", "example.jar")));
        Assert.True(File.Exists(Path.Combine(result.PackRoot, "config", "settings.txt")));
        Assert.Contains(result.Warnings, warning => warning.Contains("普通压缩包", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TranslationOverrideStoreRoundTrips()
    {
        using var temp = new TempDir();
        var store = new TranslationOverrideStore(new AppPaths(temp.Combine("data")));

        await store.SaveAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sodium"] = "钠（自定义）",
            ["unknown-mod"] = "未知模组",
        });

        var loaded = await store.LoadAsync();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("钠（自定义）", loaded["sodium"]);
    }

    private static ModpackImporter CreateImporter(TempDir temp)
    {
        var http = new HttpClient();
        var modrinth = new ModrinthClient(http, "PackDependencyTool/1.0.0 (tests)");
        var keys = new ApiKeyProvider(new EmptySecretStore());
        var curseForge = new CurseForgeClient(http, keys, "PackDependencyTool/1.0.0 (tests)");
        var downloader = new ModDownloadService(http, modrinth, curseForge, "PackDependencyTool/1.0.0 (tests)");
        return new ModpackImporter(downloader, curseForge, keys);
    }

    private static async Task CreateArchiveAsync(string path, IReadOnlyDictionary<string, string> entries)
    {
        await using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            await writer.WriteAsync(content);
        }
    }

    private sealed class EmptySecretStore : ISecretStore
    {
        public string Description => "test";
        public Task<bool> HasCurseForgeKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> GetCurseForgeKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetCurseForgeKeyAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
