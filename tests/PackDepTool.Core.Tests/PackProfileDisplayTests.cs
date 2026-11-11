using PackDepTool.Core.Models;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class PackProfileDisplayTests
{
    [Fact]
    public void LoaderDisplayIncludesDetectedVersion()
    {
        var profile = new PackProfile
        {
            Loader = LoaderKind.NeoForge,
            LoaderVersion = "21.1.93",
        };

        Assert.Equal("NeoForge 21.1.93", profile.DisplayLoaderWithVersion);
    }

    [Fact]
    public void LoaderDisplayFallsBackWhenVersionIsUnknown()
    {
        var profile = new PackProfile { Loader = LoaderKind.Fabric };
        Assert.Equal("Fabric", profile.DisplayLoaderWithVersion);
    }
}
