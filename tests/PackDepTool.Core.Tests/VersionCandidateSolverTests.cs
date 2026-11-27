using PackDepTool.Core.Analysis;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class VersionCandidateSolverTests
{
    [Fact]
    public void ChoosesHighestCompatibleVersion()
    {
        var request = new VersionSolverRequest
        {
            Candidates = new Dictionary<string, IReadOnlyList<SolverVersion>>
            {
                ["modA"] = [new("modA", "2.0", [new("modB", ">=2.0")])],
                ["modB"] =
                [
                    new("modB", "1.0", []),
                    new("modB", "2.0", []),
                    new("modB", "3.0", []),
                ],
            },
            Roots = [new("modA", ">=2.0"), new("modB", ">=1.0")],
        };

        var result = VersionCandidateSolver.Solve(request);

        Assert.True(result.Solved);
        Assert.Equal("2.0", result.Selected["modA"].Version);
        Assert.Equal("3.0", result.Selected["modB"].Version);
    }

    [Fact]
    public void BacktracksWhenHigherVersionCannotBeSatisfied()
    {
        var request = new VersionSolverRequest
        {
            Candidates = new Dictionary<string, IReadOnlyList<SolverVersion>>
            {
                ["modA"] =
                [
                    new("modA", "1.0", [new("modB", "1.x")]),
                    new("modA", "2.0", [new("modB", "2.x")]),
                ],
                ["modB"] = [new("modB", "1.5", [])],
            },
            Roots = [new("modA", "*")],
        };

        var result = VersionCandidateSolver.Solve(request);

        Assert.True(result.Solved);
        Assert.Equal("1.0", result.Selected["modA"].Version);
        Assert.Equal("1.5", result.Selected["modB"].Version);
    }

    [Fact]
    public void SkipsOptionalDependencyWhenAbsent()
    {
        var request = new VersionSolverRequest
        {
            Candidates = new Dictionary<string, IReadOnlyList<SolverVersion>>
            {
                ["modA"] = [new("modA", "1.0", [new("optional", ">=1.0", Optional: true)])],
            },
            Roots = [new("modA", "*")],
        };

        var result = VersionCandidateSolver.Solve(request);

        Assert.True(result.Solved);
        Assert.DoesNotContain("optional", result.Selected.Keys);
    }

    [Fact]
    public void ExplainsUnsatisfiedRequirement()
    {
        var request = new VersionSolverRequest
        {
            Candidates = new Dictionary<string, IReadOnlyList<SolverVersion>>
            {
                ["modA"] = [new("modA", "1.0", [new("modB", ">=2.0")])],
                ["modB"] = [new("modB", "1.0", [])],
            },
            Roots = [new("modA", "*")],
        };

        var result = VersionCandidateSolver.Solve(request);

        Assert.False(result.Solved);
        Assert.Contains("modB", result.Explanation);
        Assert.Contains(">=2.0", result.Explanation);
    }

    [Fact]
    public void HonorsPinnedVersion()
    {
        var request = new VersionSolverRequest
        {
            Candidates = new Dictionary<string, IReadOnlyList<SolverVersion>>
            {
                ["modA"] =
                [
                    new("modA", "1.0", []),
                    new("modA", "2.0", []),
                ],
            },
            Pinned = new Dictionary<string, string> { ["modA"] = "1.0" },
            Roots = [new("modA", "*")],
        };

        var result = VersionCandidateSolver.Solve(request);

        Assert.True(result.Solved);
        Assert.Equal("1.0", result.Selected["modA"].Version);
    }
}
