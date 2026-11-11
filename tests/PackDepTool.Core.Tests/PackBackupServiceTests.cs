using PackDepTool.Core.Persistence;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class PackBackupServiceTests
{
    [Fact]
    public void RepairsDisabledAndDeletedModsFromSnapshot()
    {
        using var temp = new TempDir();
        var mods = Path.Combine(temp.Path, "mods");
        Directory.CreateDirectory(mods);
        var first = Path.Combine(mods, "first.jar");
        var second = Path.Combine(mods, "second.jar");
        File.WriteAllText(first, "first-original");
        File.WriteAllText(second, "second-original");

        var service = new PackBackupService();
        var snapshot = service.CreateRepairSnapshot(
            temp.Path, "测试禁用", [first, second]);
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.FileCount);

        File.Move(first, first + ".disabled");
        File.Delete(second);
        var result = service.Restore(temp.Path, snapshot);

        Assert.Equal(2, result.RestoredFiles);
        Assert.False(File.Exists(first + ".disabled"));
        Assert.Equal("first-original", File.ReadAllText(first));
        Assert.Equal("second-original", File.ReadAllText(second));
    }

    [Fact]
    public void RestoreDeletesFilesCreatedByReplacement()
    {
        using var temp = new TempDir();
        var mods = Path.Combine(temp.Path, "mods");
        Directory.CreateDirectory(mods);
        var oldJar = Path.Combine(mods, "old.jar");
        var newJar = Path.Combine(mods, "new.jar");
        File.WriteAllText(oldJar, "old");

        var service = new PackBackupService();
        var snapshot = service.CreateRepairSnapshot(
            temp.Path, "更换模组版本", [oldJar], [newJar])!;
        File.Delete(oldJar);
        File.WriteAllText(newJar, "new");

        var result = service.Restore(temp.Path, snapshot);
        Assert.Equal(2, result.RestoredFiles);
        Assert.True(File.Exists(oldJar));
        Assert.False(File.Exists(newJar));
    }

    [Fact]
    public void DiffReportsDisabledModifiedAndMissingFiles()
    {
        using var temp = new TempDir();
        var mods = Path.Combine(temp.Path, "mods");
        Directory.CreateDirectory(mods);
        var disabled = Path.Combine(mods, "disabled.jar");
        var modified = Path.Combine(mods, "modified.jar");
        var missing = Path.Combine(mods, "missing.jar");
        File.WriteAllText(disabled, "same");
        File.WriteAllText(modified, "before");
        File.WriteAllText(missing, "same");

        var service = new PackBackupService();
        var snapshot = service.CreateRepairSnapshot(
            temp.Path, "差异测试", [disabled, modified, missing])!;

        File.Move(disabled, disabled + ".disabled");
        File.WriteAllText(modified, "after");
        File.Delete(missing);

        var diff = service.Diff(temp.Path, snapshot);
        Assert.Equal(3, diff.ChangedCount);
        Assert.Contains(diff.Entries,
            entry => entry.RelativePath.EndsWith("disabled.jar") &&
                     entry.State == PackBackupDiffState.Disabled);
        Assert.Contains(diff.Entries,
            entry => entry.RelativePath.EndsWith("modified.jar") &&
                     entry.State == PackBackupDiffState.Modified);
        Assert.Contains(diff.Entries,
            entry => entry.RelativePath.EndsWith("missing.jar") &&
                     entry.State == PackBackupDiffState.Missing);
    }

    [Fact]
    public void ConfigSnapshotCanBeRestored()
    {
        using var temp = new TempDir();
        var config = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(config);
        var file = Path.Combine(config, "example.toml");
        File.WriteAllText(file, "before");

        var service = new PackBackupService();
        var snapshot = service.CreateConfigSnapshot(temp.Path);
        Assert.NotNull(snapshot);
        File.WriteAllText(file, "after");

        var result = service.Restore(temp.Path, snapshot!);
        Assert.Equal(1, result.RestoredFiles);
        Assert.Equal("before", File.ReadAllText(file));
        Assert.Equal("config", snapshot.Kind);
    }

    [Fact]
    public void ListsOnlyValidSnapshots()
    {
        using var temp = new TempDir();
        var mods = Path.Combine(temp.Path, "mods");
        Directory.CreateDirectory(mods);
        var file = Path.Combine(mods, "mod.jar");
        File.WriteAllText(file, "content");

        var service = new PackBackupService();
        service.CreateRepairSnapshot(temp.Path, "操作 A", [file]);
        service.CreateRepairSnapshot(temp.Path, "操作 B", [file]);

        var list = service.List(temp.Path);
        Assert.Equal(2, list.Count);
        Assert.NotNull(service.Latest(temp.Path));
        Assert.Contains(list, item => item.Operation == "操作 A");
    }
}
