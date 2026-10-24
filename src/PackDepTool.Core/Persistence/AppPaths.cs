namespace PackDepTool.Core.Persistence;

/// <summary>
/// Well-known local data locations.
/// Default is portable: &lt;exe&gt;\data so nothing is written to the C: user profile.
/// Precedence: PACKDEP_DATA_DIR env var, portable.flag, &lt;exe&gt;\data, then %APPDATA% fallback.
/// </summary>
public sealed class AppPaths
{
    public const string PortableFlagName = "portable.flag";
    public const string DataDirectoryEnvironmentVariable = "PACKDEP_DATA_DIR";

    public AppPaths(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }
    public string ManualLinksDirectory => Path.Combine(DataDirectory, "manual-links");
    public string RulesDirectory => Path.Combine(DataDirectory, "rules");
    public string HistoryDirectory => Path.Combine(DataDirectory, "history");
    public string IconsDirectory => Path.Combine(DataDirectory, "icons");
    public string CacheDirectory => Path.Combine(DataDirectory, "cache");
    public string LoaderCacheDirectory => Path.Combine(CacheDirectory, "loader-versions");
    public string ApiCacheDirectory => Path.Combine(CacheDirectory, "api");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");
    public string SecretsFilePath => Path.Combine(DataDirectory, "secrets.bin");
    public string SecretKeyFilePath => Path.Combine(DataDirectory, "secrets.key");
    public string TranslationsFilePath => Path.Combine(DataDirectory, "translations.json");

    public static AppPaths CreateDefault()
    {
        var baseDirectory = AppContext.BaseDirectory;

        var explicitDirectory = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            var expanded = Environment.ExpandEnvironmentVariables(explicitDirectory.Trim().Trim('"'));
            var resolved = Path.IsPathRooted(expanded)
                ? expanded
                : Path.Combine(baseDirectory, expanded);
            return new AppPaths(resolved);
        }

        var portableFlag = Path.Combine(baseDirectory, PortableFlagName);
        var portableDirectory = Path.Combine(baseDirectory, "data");
        if (File.Exists(portableFlag) || IsWritableDirectory(portableDirectory))
        {
            Directory.CreateDirectory(portableDirectory);
            TryMigrateLegacyData(portableDirectory);
            return new AppPaths(portableDirectory);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = baseDirectory;

        return new AppPaths(Path.Combine(appData, "PackDependencyTool"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ManualLinksDirectory);
        Directory.CreateDirectory(RulesDirectory);
        Directory.CreateDirectory(HistoryDirectory);
        Directory.CreateDirectory(IconsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(LoaderCacheDirectory);
        Directory.CreateDirectory(ApiCacheDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    private static bool IsWritableDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// One-time copy of the old %APPDATA%\PackDependencyTool data into the portable
    /// data directory. It never overwrites existing files and never deletes the legacy copy.
    /// </summary>
    private static void TryMigrateLegacyData(string targetDirectory)
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PackDependencyTool");
            if (!Directory.Exists(legacy)) return;

            // If the new directory already has settings, treat it as initialized.
            if (File.Exists(Path.Combine(targetDirectory, "settings.json"))) return;

            Directory.CreateDirectory(targetDirectory);
            foreach (var directory in new[]
                     {
                         "manual-links", "rules", "history", "icons", "cache", "translations",
                     })
            {
                CopyDirectory(Path.Combine(legacy, directory), Path.Combine(targetDirectory, directory));
            }

            foreach (var file in new[] { "settings.json", "translations.json", "secrets.bin", "secrets.key" })
                CopyFile(Path.Combine(legacy, file), Path.Combine(targetDirectory, file));

            // Loader version cache used to live under %LOCALAPPDATA%.
            var legacyLoaderCache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PackDependencyTool", "loader-versions");
            CopyDirectory(legacyLoaderCache, Path.Combine(targetDirectory, "cache", "loader-versions"));

            File.WriteAllText(
                Path.Combine(targetDirectory, "MIGRATED.txt"),
                $"旧数据已从 {legacy} 复制到此处；旧目录不再被写入。{Environment.NewLine}" +
                $"Migration time: {DateTimeOffset.Now:O}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Migration is best effort; the app can start with fresh portable data.
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            CopyFile(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private static void CopyFile(string source, string target)
    {
        try
        {
            if (!File.Exists(source) || File.Exists(target)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
