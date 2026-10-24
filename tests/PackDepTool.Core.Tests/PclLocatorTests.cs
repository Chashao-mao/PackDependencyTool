using PackDepTool.Core.Scanning;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class PclLocatorTests
{
    [Fact]
    public async Task FindsOfficialPclSelectedAndCustomFolders()
    {
        using var temp = new TempDir();
        var pclRoot = temp.Combine("PCL-Official");
        Directory.CreateDirectory(pclRoot);
        await File.WriteAllTextAsync(Path.Combine(pclRoot, "Plain Craft Launcher 2.exe"), "stub");

        var pclConfig = Path.Combine(pclRoot, "PCL");
        Directory.CreateDirectory(pclConfig);
        await File.WriteAllTextAsync(Path.Combine(pclConfig, "Setup.ini"), """
            [Setup]
            LaunchFolderSelect=$.minecraft\
            LaunchFolders=Custom Pack>{CUSTOM}\
            """.Replace("{CUSTOM}", temp.Combine("custom")));

        var selected = Path.Combine(pclRoot, ".minecraft");
        Directory.CreateDirectory(Path.Combine(selected, "versions", "1.20.1"));
        await File.WriteAllTextAsync(Path.Combine(selected, "versions", "1.20.1", "1.20.1.json"), "{}");
        Directory.CreateDirectory(Path.Combine(selected, "mods"));
        await File.WriteAllTextAsync(Path.Combine(selected, "mods", "a.jar"), "jar");

        var custom = temp.Combine("custom");
        Directory.CreateDirectory(Path.Combine(custom, "versions", "1.21"));
        await File.WriteAllTextAsync(Path.Combine(custom, "versions", "1.21", "1.21.json"), "{}");
        Directory.CreateDirectory(Path.Combine(custom, "mods"));
        await File.WriteAllTextAsync(Path.Combine(custom, "mods", "b.jar"), "jar");

        var scan = await new PclLocatorService().ScanAsync([pclRoot]);

        var selectedCandidate = Assert.Single(scan.Candidates.Where(candidate =>
            string.Equals(candidate.Path, selected, StringComparison.OrdinalIgnoreCase)));
        Assert.True(selectedCandidate.IsSelected,
            string.Join(" || ", scan.Candidates.Select(c => $"{c.Path}|{c.Source}|{c.IsSelected}")));
        Assert.Equal(1, selectedCandidate.VersionCount);
        Assert.Equal(1, selectedCandidate.ModCount);

        var customCandidate = Assert.Single(scan.Candidates.Where(candidate =>
            string.Equals(candidate.Path, custom, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("Custom Pack", customCandidate.Name);
        Assert.Equal(1, customCandidate.VersionCount);
        Assert.Equal(1, customCandidate.ModCount);
        Assert.Contains(scan.PclDirectories, directory =>
            string.Equals(directory, pclRoot, StringComparison.OrdinalIgnoreCase));
    }
}
