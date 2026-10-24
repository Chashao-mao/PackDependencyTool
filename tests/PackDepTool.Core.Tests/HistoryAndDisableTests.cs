using PackDepTool.Core.History;
using PackDepTool.Core.Models;
using PackDepTool.Core.Net;
using PackDepTool.Core.Persistence;
using PackDepTool.Core.Scanning;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class HistoryAndDisableTests
{
    [Fact]
    public async Task HistoryRecordsBaselineAndSubsequentChanges()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Combine("data"));
        paths.EnsureDirectories();
        var recorder = new ModHistoryRecorder(new ModHistoryStore(paths));

        var mods = Path.Combine(temp.Combine("pack"), "mods");
        Directory.CreateDirectory(mods);
        var scan = new ScanResult { RootPath = temp.Combine("pack"), ModsDirectory = mods };
        var graph = new GraphDocument();
        graph.Nodes.Add(new ModNode
        {
            Id = "alpha",
            Name = "Alpha",
            Version = "1.0",
            FilePath = "mods/alpha.jar",
        });

        var first = await recorder.RecordAsync(scan, graph);
        Assert.Single(first.Entries);
        Assert.Empty(first.Entries[0].Changes);

        graph.Nodes[0].Version = "1.1";
        graph.Nodes.Add(new ModNode
        {
            Id = "beta",
            Name = "Beta",
            Version = "2.0",
            FilePath = "mods/beta.jar",
        });

        var second = await recorder.RecordAsync(scan, graph);
        Assert.Equal(2, second.Entries.Count);
        var changes = second.Entries[^1].Changes;
        Assert.Contains(changes, change => change.Kind == ModHistoryChangeKind.VersionChanged);
        Assert.Contains(changes, change => change.Kind == ModHistoryChangeKind.Added);
    }

    [Fact]
    public void DisabledModCanBeEnabledAgain()
    {
        using var temp = new TempDir();
        var pack = temp.Combine("pack");
        var mods = Path.Combine(pack, "mods");
        Directory.CreateDirectory(mods);
        var jar = Path.Combine(mods, "demo.jar");
        File.WriteAllText(jar, "jar");

        var node = new ModNode { Id = "demo", Name = "Demo", FilePath = "mods/demo.jar" };
        var manager = new ModFileManager();

        var disabled = manager.Disable(node, pack);
        Assert.False(File.Exists(jar));
        Assert.True(File.Exists(disabled));

        var listed = manager.ListDisabled(pack);
        var item = Assert.Single(listed);
        Assert.Equal("demo.jar.disabled", item.FileName);

        var enabled = manager.Enable(item.FullPath);
        Assert.True(File.Exists(enabled));
        Assert.Empty(manager.ListDisabled(pack));
    }

    [Fact]
    public void BatchDisableAndDeleteUseJarFilesAndTrash()
    {
        using var temp = new TempDir();
        var pack = temp.Combine("pack");
        var mods = Path.Combine(pack, "mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "a.jar"), "a");
        File.WriteAllText(Path.Combine(mods, "b.jar"), "b");
        File.WriteAllText(Path.Combine(mods, "c.jar"), "c");

        var service = new ModBatchService(null!, null!, new ModFileManager());
        var nodes = new[]
        {
            new ModNode { Id = "a", Name = "A", FilePath = "mods/a.jar" },
            new ModNode { Id = "b", Name = "B", FilePath = "mods/b.jar" },
        };

        var disabled = service.Disable(nodes, pack);
        Assert.Equal(2, disabled.SuccessCount);
        Assert.Equal(2, new ModFileManager().ListDisabled(pack).Count);

        var deleted = service.Delete(
            new[] { new ModNode { Id = "c", Name = "C", FilePath = "mods/c.jar" } }, pack);
        Assert.Equal(1, deleted.SuccessCount);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(pack, ".packdep-trash"), "*", SearchOption.AllDirectories));
    }
}
