using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using PackDepTool.Core.Net;
using Xunit;

namespace PackDepTool.Core.Tests;

public class ResumableDownloaderTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(factory(request));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> factory) =>
        new(new Handler(factory));

    private static string Sha1(byte[] bytes) =>
        Convert.ToHexStringLower(SHA1.HashData(bytes));

    private static DownloadFileRequest Request(
        string destination, byte[] bytes, int attempts = 2, params string[] mirrors) => new()
    {
        Url = "https://primary.example.com/file.jar",
        MirrorUrls = mirrors,
        DestinationPath = destination,
        ExpectedSha1 = Sha1(bytes),
        ExpectedSize = bytes.Length,
        MaxAttempts = attempts,
    };

    [Fact]
    public async Task DownloadsAndVerifiesFile()
    {
        using var temp = new TempDir();
        var bytes = Encoding.UTF8.GetBytes("hello downloader");
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
        var destination = temp.Combine("mods", "test.jar");

        var result = await new ResumableDownloader(http, "PackDependencyTool/test")
            .DownloadAsync(Request(destination, bytes));

        Assert.True(File.Exists(destination));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.Equal(Sha1(bytes), result.Sha1);
    }

    [Fact]
    public async Task ResumesPartialFileWithRange()
    {
        using var temp = new TempDir();
        var bytes = Encoding.UTF8.GetBytes("0123456789");
        var destination = temp.Combine("test.jar");
        await File.WriteAllBytesAsync(destination + ".part", bytes[..4]);
        var sawRange = false;
        using var http = Client(request =>
        {
            sawRange = request.Headers.Range?.Ranges.Any(range =>
                range.From == 4 && range.To is null) == true;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes[4..]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4, bytes.Length - 1, bytes.Length);
            return response;
        });

        await new ResumableDownloader(http, "PackDependencyTool/test")
            .DownloadAsync(Request(destination, bytes));

        Assert.True(sawRange);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task RetriesRateLimitThenSucceeds()
    {
        using var temp = new TempDir();
        var bytes = Encoding.UTF8.GetBytes("rate limited");
        var calls = 0;
        using var http = Client(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var tooMany = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                tooMany.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return tooMany;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });

        var result = await new ResumableDownloader(http, "PackDependencyTool/test")
            .DownloadAsync(Request(temp.Combine("test.jar"), bytes, attempts: 2));

        Assert.Equal(2, result.Attempts);
        Assert.True(File.Exists(temp.Combine("test.jar")));
    }

    [Fact]
    public async Task FallsBackToMirror()
    {
        using var temp = new TempDir();
        var bytes = Encoding.UTF8.GetBytes("mirror works");
        using var http = Client(request =>
            request.RequestUri!.Host == "primary.example.com"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

        var result = await new ResumableDownloader(http, "PackDependencyTool/test")
            .DownloadAsync(Request(temp.Combine("test.jar"), bytes, attempts: 1,
                "https://mirror.example.com/file.jar"));

        Assert.True(result.UsedMirror);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(temp.Combine("test.jar")));
    }

    [Fact]
    public async Task RejectsHashMismatchAndLeavesNoDestination()
    {
        using var temp = new TempDir();
        var bytes = Encoding.UTF8.GetBytes("bad content");
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
        var destination = temp.Combine("test.jar");
        var request = new DownloadFileRequest
        {
            Url = "https://primary.example.com/file.jar",
            DestinationPath = destination,
            ExpectedSha1 = new string('0', 40),
            ExpectedSize = bytes.Length,
            MaxAttempts = 1,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ResumableDownloader(http, "PackDependencyTool/test").DownloadAsync(request));
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }
}
