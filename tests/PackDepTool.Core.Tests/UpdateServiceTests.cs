using System.Net;
using System.Text;
using PackDepTool.Core.Net;
using Xunit;

namespace PackDepTool.Core.Tests;

public class UpdateServiceTests
{
    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private static UpdateService Service(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new HttpClient(new StubHandler(body, status)), "PackDependencyTool/test");

    [Fact]
    public async Task NewerVersionIsReported()
    {
        var result = await Service("""{"version":"v1.2.0","releaseUrl":"https://example.com/r","notes":"new","sha256":"abc"}""")
            .CheckAsync("https://example.com/update.json", "1.1.9");

        Assert.True(result.Succeeded);
        Assert.True(result.HasUpdate);
        Assert.Equal("v1.2.0", result.LatestVersion);
        Assert.Equal("abc", result.Sha256);
    }

    [Fact]
    public async Task SameVersionIsNotAnUpdate()
    {
        var result = await Service("""{"version":"1.0.0"}""")
            .CheckAsync("https://example.com/update.json", "1.0.0");

        Assert.True(result.Succeeded);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task InvalidFeedIsHandled()
    {
        var result = await Service("{}").CheckAsync("http://example.com/update.json", "1.0.0");
        Assert.False(result.Succeeded);
        Assert.Contains("HTTPS", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotFoundIsHandled()
    {
        var result = await Service("", HttpStatusCode.NotFound)
            .CheckAsync("https://example.com/update.json", "1.0.0");
        Assert.False(result.Succeeded);
        Assert.Contains("404", result.Error);
    }
}
