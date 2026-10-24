using PackDepTool.Core.Persistence;
using Xunit;

namespace PackDepTool.Core.Tests;

public class ApiCacheStoreTests
{
    private sealed class Payload
    {
        public string Value { get; set; } = string.Empty;
    }

    [Fact]
    public async Task RoundTripsValue()
    {
        using var temp = new TempDir();
        var cache = new ApiCacheStore(temp.Path);
        await cache.SetAsync("modrinth", "project:abc", new Payload { Value = "hello" }, TimeSpan.FromMinutes(5));

        var result = await cache.GetAsync<Payload>("modrinth", "project:abc", TimeSpan.FromMinutes(5));

        Assert.NotNull(result);
        Assert.Equal("hello", result!.Value);
    }

    [Fact]
    public async Task ExpiredEntryIsIgnored()
    {
        using var temp = new TempDir();
        var cache = new ApiCacheStore(temp.Path);
        await cache.SetAsync("curseforge", "versions:1", new Payload { Value = "old" }, TimeSpan.FromMilliseconds(1));
        await Task.Delay(20);

        Assert.Null(await cache.GetAsync<Payload>("curseforge", "versions:1", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task CorruptEntryIsRemoved()
    {
        using var temp = new TempDir();
        var cache = new ApiCacheStore(temp.Path);
        await cache.SetAsync("modrinth", "broken", new Payload { Value = "x" }, TimeSpan.FromMinutes(5));
        var file = Directory.GetFiles(temp.Path, "*.json", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(file, "{ not json");

        Assert.Null(await cache.GetAsync<Payload>("modrinth", "broken", TimeSpan.FromMinutes(5)));
        Assert.False(File.Exists(file));
    }
}
