using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PackDepTool.Core.Net;
using PackDepTool.Core.Persistence;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class GameInstallTests
{
    [Fact]
    public async Task InstallsVanillaAndWritesPclReadableFilesThenSwitchKeepsMods()
    {
        using var temp = new TempDir();
        var handler = new FakeMinecraftHandler();
        var http = new HttpClient(handler);
        var keys = new ApiKeyProvider(new EmptySecretStore());
        var modrinth = new ModrinthClient(http, "PackDependencyTool/1.0.0 (tests)");
        var curseForge = new CurseForgeClient(http, keys, "PackDependencyTool/1.0.0 (tests)");
        var downloader = new ModDownloadService(http, modrinth, curseForge, "PackDependencyTool/1.0.0 (tests)");
        var installer = new MinecraftInstallService(
            http, downloader, "PackDependencyTool/1.0.0 (tests)", temp.Combine("loader-cache"));

        var request = new GameInstallRequest
        {
            TargetDirectory = temp.Combine("game"),
            MinecraftVersion = "1.20.1",
            Loader = GameLoader.Vanilla,
            DownloadAssets = false,
            UseTargetAsInstance = true,
        };

        var result = await installer.InstallAsync(request);

        var versionDir = Path.Combine(result.InstanceDirectory, "versions", "1.20.1");
        Assert.True(File.Exists(Path.Combine(versionDir, "1.20.1.json")));
        Assert.True(File.Exists(Path.Combine(versionDir, "1.20.1.jar")));
        Assert.True(File.Exists(Path.Combine(result.InstanceDirectory, "launcher_profiles.json")));
        Assert.True(File.Exists(Path.Combine(result.InstanceDirectory, "mmc-pack.json")));

        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(
                   Path.Combine(result.InstanceDirectory, "mmc-pack.json"))))
        {
            var components = doc.RootElement.GetProperty("components");
            Assert.Contains(components.EnumerateArray(), component =>
                component.GetProperty("uid").GetString() == "net.minecraft");
        }

        Directory.CreateDirectory(Path.Combine(result.InstanceDirectory, "mods"));
        await File.WriteAllTextAsync(Path.Combine(result.InstanceDirectory, "mods", "keep.jar"), "keep");

        var switchRequest = new GameInstallRequest
        {
            TargetDirectory = result.InstanceDirectory,
            MinecraftVersion = "1.20.1",
            Loader = GameLoader.Vanilla,
            DownloadAssets = false,
            UseTargetAsInstance = true,
        };
        await installer.SwitchLoaderAsync(result.InstanceDirectory, switchRequest);

        Assert.True(File.Exists(Path.Combine(result.InstanceDirectory, "mods", "keep.jar")));
        Assert.True(File.Exists(Path.Combine(versionDir, "1.20.1.jar")));
    }

    private sealed class FakeMinecraftHandler : HttpMessageHandler
    {
        private static readonly byte[] Client = Encoding.UTF8.GetBytes("fake-client-jar");
        private static readonly string Sha1 = Convert.ToHexStringLower(SHA1.HashData(Client));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("version_manifest_v2.json", StringComparison.OrdinalIgnoreCase))
            {
                var manifest = JsonSerializer.Serialize(new
                {
                    versions = new[]
                    {
                        new
                        {
                            id = "1.20.1",
                            type = "release",
                            url = "https://example.test/version.json",
                            releaseTime = "2023-06-12T00:00:00Z",
                        },
                    },
                });
                return Json(manifest);
            }

            if (url.EndsWith("/version.json", StringComparison.OrdinalIgnoreCase))
            {
                var version = JsonSerializer.Serialize(new
                {
                    id = "1.20.1",
                    type = "release",
                    downloads = new
                    {
                        client = new
                        {
                            url = "https://example.test/client.jar",
                            sha1 = Sha1,
                            size = Client.Length,
                        },
                    },
                    libraries = Array.Empty<object>(),
                });
                return Json(version);
            }

            if (url.EndsWith("/client.jar", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Client),
                });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string content) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class EmptySecretStore : ISecretStore
    {
        public string Description => "test";
        public Task<bool> HasCurseForgeKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> GetCurseForgeKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetCurseForgeKeyAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
