using PackDepTool.Core.Models;
using PackDepTool.Core.Persistence;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class VersionLockServiceTests
{
    [Fact]
    public void CreatesComparesAndReloadsLock()
    {
        using var temp = new TempDir();
        var service = new VersionLockService();
        var scan = new ScanResult
        {
            RootPath = temp.Path,
            Profile = new PackProfile { PackName = "Demo", McVersion = "1.21.1", Loader = LoaderKind.NeoForge },
        };
        var graph = new GraphDocument
        {
            Nodes =
            [
                new ModNode { Id = "a", Name = "A", Version = "1.0", FilePath = "mods/a.jar", Sha1 = "aaa" },
                new ModNode { Id = "b", Name = "B", Version = "1.0", FilePath = "mods/b.jar", Sha1 = "bbb" },
            ],
        };

        var document = service.Create(scan, graph);
        service.Save(temp.Path, document);
        var reloaded = service.Load(temp.Path);
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded!.Mods.Count);
        Assert.Equal(0, service.Compare(reloaded, graph).ChangedCount);

        graph.Nodes[1].Version = "2.0";
        graph.Nodes[1].Sha1 = "new";
        graph.Nodes.Add(new ModNode { Id = "c", Name = "C", Version = "1.0", FilePath = "mods/c.jar", Sha1 = "ccc" });
        var diff = service.Compare(reloaded, graph);
        Assert.Contains(diff.Entries, entry => entry.ModId == "b" &&
                                               entry.Kind == VersionLockDiffKind.VersionChanged);
        Assert.Contains(diff.Entries, entry => entry.ModId == "c" &&
                                               entry.Kind == VersionLockDiffKind.Added);
    }

    [Fact]
    public void ReportsRemovedAndFileOnlyChanges()
    {
        using var temp = new TempDir();
        var service = new VersionLockService();
        var document = new VersionLockDocument
        {
            Mods =
            [
                new VersionLockEntry { Id = "removed", Name = "Removed", Version = "1.0", File = "mods/r.jar", Sha1 = "r" },
                new VersionLockEntry { Id = "same-version", Name = "Same", Version = "1.0", File = "mods/s.jar", Sha1 = "old" },
            ],
        };
        var graph = new GraphDocument
        {
            Nodes =
            [
                new ModNode
                {
                    Id = "same-version", Name = "Same", Version = "1.0",
                    FilePath = "mods/s.jar", Sha1 = "new",
                },
            ],
        };

        var diff = service.Compare(document, graph);
        Assert.Contains(diff.Entries, entry => entry.Kind == VersionLockDiffKind.Removed &&
                                               entry.ModId == "removed");
        Assert.Contains(diff.Entries, entry => entry.Kind == VersionLockDiffKind.FileChanged &&
                                               entry.ModId == "same-version");
    }
}
