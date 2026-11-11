using PackDepTool.Core.Models;
using PackDepTool.Core.Persistence;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class ModListHistoryServiceTests
{
    [Fact]
    public void SavesAndComparesModListSnapshots()
    {
        using var temp = new TempDir();
        var service = new ModListHistoryService();
        var scan = new ScanResult { RootPath = temp.Path, Profile = new PackProfile { PackName = "Demo" } };
        var first = new GraphDocument
        {
            Nodes =
            [
                new ModNode { Id = "a", Name = "A", Version = "1.0", FilePath = "mods/a.jar" },
                new ModNode { Id = "b", Name = "B", Version = "1.0", FilePath = "mods/b.jar" },
            ],
        };
        var before = service.Capture(scan, first);
        service.Save(temp.Path, before);

        var second = new GraphDocument
        {
            Nodes =
            [
                new ModNode { Id = "a", Name = "A", Version = "2.0", FilePath = "mods/a.jar" },
                new ModNode { Id = "c", Name = "C", Version = "1.0", FilePath = "mods/c.jar" },
            ],
        };
        var changes = service.Compare(before, service.Capture(scan, second));

        Assert.Contains(changes, change => change.Kind == ModListChangeKind.VersionChanged && change.ModId == "a");
        Assert.Contains(changes, change => change.Kind == ModListChangeKind.Added && change.ModId == "c");
        Assert.Contains(changes, change => change.Kind == ModListChangeKind.Removed && change.ModId == "b");
        Assert.Single(service.List(temp.Path));
    }
}
