using PackDepTool.Core.Scanning;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class AxolotlLauncherServiceTests
{
    [Fact]
    public void ResolvesProfileNameUnderMcProfiles()
    {
        using var temp = new TempDir();
        var launcher = temp.Combine("Axolotl", "AxolotlLauncher");
        Directory.CreateDirectory(launcher);
        var exe = Path.Combine(launcher, "Axolotl Launcher.exe");
        File.WriteAllText(exe, "test");
        var profile = temp.Combine("Axolotl", "mc", "profiles", "测试");
        Directory.CreateDirectory(profile);

        var service = new AxolotlLauncherService();
        service.Detect(exe);
        var resolved = service.ResolvePath(new AxolotlInstance { Path = "测试" });

        Assert.Equal(profile, resolved);
    }
}
