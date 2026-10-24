using System.IO.Compression;
using System.Text.Json;
using PackDepTool.Core.Models;
using PackDepTool.Core.Net;
using PackDepTool.Core.Persistence;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class PclExportTests
{
    [Fact]
    public async Task ExportsPclReadableMrpackAndCanBeImportedBack()
    {
        using var temp = new TempDir();
        var packRoot = temp.Combine("Atlantis");
        Directory.CreateDirectory(Path.Combine(packRoot, "mods"));
        await File.WriteAllTextAsync(Path.Combine(packRoot, "mods", "foo.jar"), "fake jar");

        var graph = new GraphDocument();
        graph.Nodes.Add(new ModNode
        {
            Id = "foo",
            Name = "Foo Mod",
            Version = "1.2.3",
            Loader = LoaderKind.Fabric,
            FilePath = "mods/foo.jar",
        });

        var profile = new PackProfile
        {
            PackName = "Atlantis Test",
            McVersion = "1.20.1",
            Loader = LoaderKind.Fabric,
            LoaderVersion = "0.15.11",
        };

        var http = new HttpClient();
        var modrinth = new ModrinthClient(http, "PackDependencyTool/1.0.0 (tests)");
        var keys = new ApiKeyProvider(new EmptySecretStore());
        var curseForge = new CurseForgeClient(http, keys, "PackDependencyTool/1.0.0 (tests)");
        var downloader = new ModDownloadService(http, modrinth, curseForge, "PackDependencyTool/1.0.0 (tests)");

        var exporter = new PclPackExportService(modrinth);
        var output = temp.Combine("out.mrpack");
        var result = await exporter.ExportAsync(graph, profile, packRoot, output);

        Assert.True(File.Exists(output));
        Assert.Equal(1, result.OverrideMods);
        Assert.Equal(0, result.ModrinthFiles);

        using (var archive = ZipFile.OpenRead(output))
        {
            var indexEntry = archive.GetEntry("modrinth.index.json");
            Assert.NotNull(indexEntry);
            await using var stream = indexEntry!.Open();
            using var doc = await JsonDocument.ParseAsync(stream);
            Assert.Equal("1.20.1", doc.RootElement.GetProperty("dependencies").GetProperty("minecraft").GetString());
            Assert.Equal("0.15.11", doc.RootElement.GetProperty("dependencies").GetProperty("fabric-loader").GetString());
            Assert.NotNull(archive.GetEntry("overrides/mods/foo.jar"));
        }

        var importer = new ModpackImporter(downloader, curseForge, keys);
        var imported = await importer.ImportAsync(output, temp.Combine("packs"));

        Assert.True(File.Exists(Path.Combine(imported.PackRoot, "mods", "foo.jar")));
        Assert.True(File.Exists(Path.Combine(imported.PackRoot, "modrinth.index.json")));
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
