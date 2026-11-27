using PackDepTool.Core.Persistence;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void PreferredDataDirectoryIsOnEDrive()
    {
        Assert.Equal(@"E:\PackDependencyTool\data", AppPaths.PreferredDataDirectory);
    }

    [Fact]
    public void DerivedDirectoriesStayUnderDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdt-paths-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        Assert.StartsWith(paths.DataDirectory, paths.SettingsFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(paths.DataDirectory, paths.LogsDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
