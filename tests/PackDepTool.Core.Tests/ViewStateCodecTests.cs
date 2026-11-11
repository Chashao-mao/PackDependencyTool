using PackDepTool.Core.Persistence;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class ViewStateCodecTests
{
    [Fact]
    public void RoundTripsPresentationState()
    {
        var snapshot = new ViewStateSnapshot
        {
            Search = "sodium",
            OnlyProblems = true,
            ShowOptional = true,
            StatusFilter = "missing",
            SideFilter = "client",
            SortOption = "status",
            ViewOption = "list",
        };

        var code = ViewStateCodec.Encode(snapshot);
        var decoded = ViewStateCodec.Decode(code);

        Assert.NotNull(decoded);
        Assert.Equal("sodium", decoded!.Search);
        Assert.True(decoded.OnlyProblems);
        Assert.True(decoded.ShowOptional);
        Assert.Equal("missing", decoded.StatusFilter);
        Assert.Equal("client", decoded.SideFilter);
        Assert.Equal("list", decoded.ViewOption);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-state")]
    [InlineData("pdt-view:v1:%%%")]
    public void RejectsInvalidOrMissingState(string? code)
    {
        Assert.Null(ViewStateCodec.Decode(code));
    }
}
