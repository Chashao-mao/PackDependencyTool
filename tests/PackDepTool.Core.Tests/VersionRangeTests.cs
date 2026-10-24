using PackDepTool.Core.Analysis;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class VersionRangeTests
{
    [Theory]
    [InlineData("[1.20,1.21)", "1.20.1", true)]
    [InlineData("[1.20,1.21)", "1.21", false)]
    [InlineData("[1.20,1.21)", "1.19.4", false)]
    [InlineData("[1.20.1]", "1.20.1", true)]
    [InlineData("[1.20.1]", "1.20.2", false)]
    [InlineData("(,1.20]", "1.19.2", true)]
    [InlineData("(,1.20]", "1.20.1", false)]
    [InlineData("[1.20,)", "1.20.1", true)]
    [InlineData("[1.20,1.21),[1.22,1.23)", "1.22.5", true)]
    [InlineData("[1.20,1.21),[1.22,1.23)", "1.21.5", false)]
    public void MavenRanges(string range, string version, bool expected) =>
        Assert.Equal(expected, VersionRange.Satisfies(range, version));

    [Theory]
    [InlineData(">=1.20 <1.21", "1.20.4", true)]
    [InlineData(">=1.20 <1.21", "1.21", false)]
    [InlineData("~1.2.3", "1.2.9", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    [InlineData("^1.2.3", "1.9.9", true)]
    [InlineData("^1.2.3", "2.0.0", false)]
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    [InlineData("1.2.x", "1.2.77", true)]
    [InlineData("1.2.x", "1.3.0", false)]
    [InlineData("1.20.x || 1.21.x", "1.21.1", true)]
    [InlineData("1.20.x || 1.21.x", "1.19.4", false)]
    [InlineData(">=0.15", "0.15.11", true)]
    [InlineData("1.20.1", "1.20.1-47.2.0", true)]
    [InlineData("1.20", "1.20.1", true)]
    [InlineData("1.20.1", "1.20.2", false)]
    public void FabricRanges(string range, string version, bool expected) =>
        Assert.Equal(expected, VersionRange.Satisfies(range, version));

    [Theory]
    [InlineData("[1.21,1.21.1]", "1.21.1", true)]
    [InlineData("[1.21, 1.21.1)", "1.21.1", true)]
    [InlineData("[1.21, 1.21.1)", "1.21.2", false)]
    [InlineData("[1.20, 1.20.1)", "1.20.2", false)]
    public void MinecraftPatchExclusiveUpperIsLenient(string range, string version, bool expected) =>
        Assert.Equal(expected, VersionRange.SatisfiesMinecraft(range, version));

    [Fact]
    public void StrictDependencyRangeStillUsesExclusiveUpper()
    {
        Assert.False(VersionRange.Satisfies("[1.21, 1.21.1)", "1.21.1"));
    }

    [Fact]
    public void WildcardAndEmptyMeansAny()
    {
        Assert.True(VersionRange.Satisfies("*", "anything"));
        Assert.True(VersionRange.Satisfies("", "1.0"));
        Assert.True(VersionRange.Satisfies(null, "1.0"));
    }

    [Fact]
    public void UnknownVersionOrUnparsableRangeReturnsNull()
    {
        Assert.Null(VersionRange.Satisfies(">=1.0", "unknown"));
        Assert.Null(VersionRange.Satisfies("TODO", "1.0"));
        Assert.Null(VersionRange.Satisfies("${file.jarVersion}", "1.0"));
        Assert.Null(VersionRange.Satisfies("[1.0", "1.2"));
        Assert.Null(VersionRange.Satisfies("[[[", "1.2"));
    }

    [Fact]
    public void VersionComparerHandlesSegments()
    {
        Assert.True(ModVersion.Compare("1.20.1", "1.20.0") > 0);
        Assert.True(ModVersion.Compare("1.20", "1.20.0") < 0);
        Assert.Equal(0, ModVersion.Compare("1.0.0", "1.0.0"));
    }

    [Fact]
    public void PrereleaseSortsBelowRelease()
    {
        Assert.True(ModVersion.Compare("1.0.0", "1.0.0-beta") > 0);
        Assert.True(ModVersion.Compare("1.0.0-beta", "1.0.0") < 0);
        Assert.True(ModVersion.Compare("1.0.0-beta.2", "1.0.0-beta.1") > 0);
        Assert.Equal(0, ModVersion.Compare("1.0.0+build", "1.0.0"));
    }

    [Theory]
    [InlineData("[0,0.8.12-beta.1)", "0.6.0", true)]
    [InlineData("[0,0.8.12-beta.1)", "0.8.12", false)]
    [InlineData("[0,0.8.12-beta.1)", "0.8.12-beta.1", false)]
    [InlineData("1.0.0", "1.0.0-beta", false)]
    [InlineData("1.20.1", "1.20.1-47.2.0", true)]
    public void PrereleaseRangesAreStrict(string range, string version, bool expected) =>
        Assert.Equal(expected, VersionRange.Satisfies(range, version));
}
