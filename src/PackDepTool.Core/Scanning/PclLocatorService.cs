using System.Text.Json;
using System.Text.RegularExpressions;

namespace PackDepTool.Core.Scanning;

public sealed class PclGameDirectoryCandidate
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public bool IsSelected { get; init; }
    public int VersionCount { get; init; }
    public int ModCount { get; init; }

    public string Detail => $"{VersionCount} 个版本 · {ModCount} 个模组";
    public string Display => string.IsNullOrWhiteSpace(Source) ? Name : $"{Name}（{Source}）";
}

public sealed class PclScanResult
{
    public List<PclGameDirectoryCandidate> Candidates { get; } = [];
    public List<string> PclDirectories { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Finds the official PCL (Plain Craft Launcher 2) installation and its configured Minecraft game folders
/// (LaunchFolderSelect / LaunchFolders), so installs can reuse existing libraries/assets.
/// </summary>
public sealed class PclLocatorService
{
    private static readonly string[] ExecutableNames =
    [
        "PCL.exe", "PCL2.exe", "Plain Craft Launcher 2.exe",
    ];

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin",
        "System Volume Information", "node_modules", ".git", "AppData",
    };

    public Task<PclScanResult> ScanAsync(
        IReadOnlyCollection<string>? roots = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(roots, cancellationToken), cancellationToken);

    private static PclScanResult Scan(
        IReadOnlyCollection<string>? roots,
        CancellationToken cancellationToken)
    {
        var result = new PclScanResult();
        var directories = FindPclDirectories(roots, result.Warnings, cancellationToken);
        result.PclDirectories.AddRange(directories);

        var candidates = new List<PclGameDirectoryCandidate>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? path, string? name, string source, bool selected)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var full = Path.GetFullPath(path);
                if (!Directory.Exists(full)) return;
                var key = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (seenPaths.Contains(key)) return;
                seenPaths.Add(key);

                var versions = CountVersions(full);
                var mods = CountMods(full);
                candidates.Add(new PclGameDirectoryCandidate
                {
                    Name = string.IsNullOrWhiteSpace(name) ? DirectoryName(full) : name!,
                    Path = key,
                    Source = source,
                    IsSelected = selected,
                    VersionCount = versions,
                    ModCount = mods,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                result.Warnings.Add($"{path}：{ex.Message}");
            }
        }

        foreach (var directory in directories)
        {
            var config = ReadPclConfig(directory);
            foreach (var custom in config.Folders)
                AddCandidate(custom.Path, custom.Name, "PCL 官方自定义目录", false);

            if (!string.IsNullOrWhiteSpace(config.SelectedFolder))
                AddCandidate(config.SelectedFolder, null, "PCL 官方当前目录", true);

            AddCandidate(Path.Combine(directory, ".minecraft"), null, "PCL 官方默认目录", false);

            // Official PCL also treats the executable folder itself and any direct
            // child containing versions/ (or named .minecraft) as game folders.
            if (HasGameData(directory))
                AddCandidate(directory, "PCL 当前文件夹", "PCL 官方当前目录", false);
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (HasGameData(child) ||
                        string.Equals(Path.GetFileName(child), ".minecraft", StringComparison.OrdinalIgnoreCase))
                        AddCandidate(child, null, "PCL 官方目录", false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Warnings.Add($"{directory}：{ex.Message}");
            }
        }

        AddCandidate(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"),
            null, "官方 .minecraft", false);
        AddCandidate(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft"),
            null, "用户 .minecraft", false);

        var ordered = candidates
            .OrderByDescending(candidate => candidate.IsSelected)
            .ThenByDescending(candidate => candidate.ModCount)
            .ThenByDescending(candidate => candidate.VersionCount)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.Candidates.AddRange(ordered);
        return result;
    }

    private static List<string> FindPclDirectories(
        IReadOnlyCollection<string>? roots,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var searchRoots = roots?.Where(Directory.Exists).ToList() ?? DefaultSearchRoots();

        foreach (var root in searchRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (HasPclExecutable(root)) found.Add(Path.GetFullPath(root));

                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 2,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                };

                var checkedDirectories = 0;
                foreach (var directory in Directory.EnumerateDirectories(root, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++checkedDirectories > 4000) break;
                    if (SkipDirectories.Contains(Path.GetFileName(directory))) continue;

                    if (HasPclExecutable(directory))
                        found.Add(Path.GetFullPath(directory));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                warnings.Add($"扫描 {root} 失败：{ex.Message}");
            }
        }

        // Also recognize config-only portable folders even if the exe is named differently.
        foreach (var root in searchRoots)
        {
            try
            {
                var pclFolder = Path.Combine(root, "PCL");
                if (Directory.Exists(pclFolder) &&
                    File.Exists(Path.Combine(pclFolder, "Setup.ini")))
                    found.Add(Path.GetFullPath(root));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"检查 {root} 失败：{ex.Message}");
            }
        }

        return found.ToList();
    }

    private static bool HasGameData(string directory)
    {
        try
        {
            if (Directory.Exists(Path.Combine(directory, "versions"))) return true;
            if (Directory.Exists(Path.Combine(directory, "mods"))) return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore.
        }

        return false;
    }

    private static bool HasPclExecutable(string directory)
    {
        try
        {
            foreach (var name in ExecutableNames)
            {
                if (File.Exists(Path.Combine(directory, name))) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore inaccessible directories.
        }

        return false;
    }

    private static IEnumerable<string> DefaultSearchRoots()
    {
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        try
        {
            roots.AddRange(DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
                .Select(drive => drive.RootDirectory.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Drive enumeration is best effort.
        }

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static PclConfig ReadPclConfig(string directory)
    {
        var config = new PclConfig();
        var candidates = new[]
        {
            Path.Combine(directory, "PCL", "Setup.ini"),
            Path.Combine(directory, "Setup.ini"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            var parsed = ReadConfigFile(path, directory);
            if (config.SelectedFolder is null && parsed.SelectedFolder is not null)
                config.SelectedFolder = parsed.SelectedFolder;
            config.Folders.AddRange(parsed.Folders);
        }

        return config;
    }

    private static PclConfig ReadConfigFile(string path, string baseDirectory)
    {
        var config = new PclConfig();
        if (!File.Exists(path)) return config;

        try
        {
            var text = File.ReadAllText(path);
            var values = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? ReadJsonValues(text)
                : ReadFlatValues(text);

            if (values.TryGetValue("LaunchFolderSelect", out var selected))
                config.SelectedFolder = ResolvePath(selected, baseDirectory);

            if (values.TryGetValue("LaunchFolders", out var folders))
            {
                foreach (var entry in folders.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = entry.Trim();
                    if (trimmed.Length == 0) continue;

                    string name = string.Empty;
                    var pathPart = trimmed;
                    var separator = trimmed.IndexOf('>');
                    if (separator > 0)
                    {
                        name = trimmed[..separator].Trim();
                        pathPart = trimmed[(separator + 1)..].Trim();
                    }

                    var resolved = ResolvePath(pathPart, baseDirectory);
                    if (resolved is not null)
                        config.Folders.Add(new PclFolder(name, resolved));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Ignore malformed PCL configs.
        }

        return config;
    }

    private static Dictionary<string, string> ReadFlatValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*(LaunchFolderSelect|LaunchFolders)\s*[:=]\s*(.*)$",
                RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            var value = match.Groups[2].Value.Trim();
            if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
                value = value[1..^1].Replace("\\\"", "\"");
            else if (value.StartsWith('\'') && value.EndsWith('\'') && value.Length >= 2)
                value = value[1..^1];
            values[match.Groups[1].Value] = value;
        }

        return values;
    }

    private static Dictionary<string, string> ReadJsonValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(text);
        Visit(doc.RootElement, values);
        return values;

        static void Visit(JsonElement element, Dictionary<string, string> output)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "LaunchFolderSelect" or "LaunchFolders" &&
                        property.Value.ValueKind == JsonValueKind.String)
                        output[property.Name] = property.Value.GetString() ?? string.Empty;
                    else
                        Visit(property.Value, output);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Visit(item, output);
            }
        }
    }

    private static string? ResolvePath(string? raw, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim();
        if (value.Length >= 2 &&
            ((value.StartsWith('"') && value.EndsWith('"')) ||
             (value.StartsWith('\'') && value.EndsWith('\''))))
            value = value[1..^1];

        value = Environment.ExpandEnvironmentVariables(value);
        if (value.StartsWith('$'))
        {
            var suffix = value[1..].TrimStart('\\', '/');
            value = suffix.Length == 0
                ? baseDirectory
                : Path.Combine(baseDirectory, suffix);
        }
        if (!Path.IsPathRooted(value))
            value = Path.Combine(baseDirectory, value);

        try
        {
            var full = Path.GetFullPath(value);
            return Directory.Exists(full)
                ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static int CountVersions(string gameDirectory)
    {
        var versions = Path.Combine(gameDirectory, "versions");
        if (!Directory.Exists(versions)) return 0;

        try
        {
            return Directory.EnumerateDirectories(versions)
                .Count(directory =>
                    File.Exists(Path.Combine(directory, Path.GetFileName(directory) + ".json")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static int CountMods(string gameDirectory)
    {
        var count = CountJars(Path.Combine(gameDirectory, "mods"));
        var versions = Path.Combine(gameDirectory, "versions");
        if (!Directory.Exists(versions)) return count;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(versions))
                count += CountJars(Path.Combine(directory, "mods"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }

        return count;
    }

    private static int CountJars(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        try
        {
            return Directory.EnumerateFiles(directory, "*.jar", SearchOption.TopDirectoryOnly).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string DirectoryName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private sealed class PclConfig
    {
        public string? SelectedFolder { get; set; }
        public List<PclFolder> Folders { get; } = [];
    }

    private sealed record PclFolder(string? Name, string Path);
}
