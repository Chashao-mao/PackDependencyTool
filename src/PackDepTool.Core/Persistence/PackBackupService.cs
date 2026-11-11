using PackDepTool.Core.Models;

namespace PackDepTool.Core.Persistence;

public sealed class PackBackupInfo
{
    public string Id { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public int FileCount { get; init; }
    public string Directory { get; init; } = string.Empty;

    public string Title => string.IsNullOrWhiteSpace(Operation) ? Id : Operation;
    public string CreatedText => CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string Detail => $"{CreatedText} · {FileCount} 个文件";
}

public sealed class PackBackupRestoreResult
{
    public int RestoredFiles { get; init; }
    public int MissingFiles { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Local, file-level snapshots taken before destructive mod operations.
/// Mirrors the snapshot/restore workflow of ModpackDebuggerKit while keeping
/// the format simple and inspectable (one JSON manifest per snapshot).
/// </summary>
public sealed class PackBackupService
{
    public const string DirectoryName = ".packdep-backups";
    private const string ManifestName = "manifest.json";

    public string BackupRoot(string packRoot) => Path.Combine(packRoot, DirectoryName);

    public PackBackupInfo? Latest(string packRoot) =>
        List(packRoot).OrderByDescending(info => info.CreatedAt).FirstOrDefault();

    public IReadOnlyList<PackBackupInfo> List(string packRoot)
    {
        var root = BackupRoot(packRoot);
        if (!System.IO.Directory.Exists(root)) return [];

        var results = new List<PackBackupInfo>();
        try
        {
            foreach (var directory in System.IO.Directory.EnumerateDirectories(root))
            {
                var manifestPath = Path.Combine(directory, ManifestName);
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var manifest = JsonDefaults.Deserialize<PackBackupManifest>(
                        File.ReadAllText(manifestPath));
                    if (manifest is null) continue;
                    results.Add(new PackBackupInfo
                    {
                        Id = manifest.Id,
                        Operation = manifest.Operation,
                        Kind = manifest.Kind,
                        CreatedAt = manifest.CreatedAt,
                        FileCount = manifest.Files.Count,
                        Directory = directory,
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                               System.Text.Json.JsonException)
                {
                    // Ignore damaged snapshots and show the rest.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return results.OrderByDescending(info => info.CreatedAt).ToList();
    }

    public PackBackupInfo? CreateRepairSnapshot(
        string packRoot,
        string operation,
        IEnumerable<string> absoluteFiles,
        IEnumerable<string>? createdFiles = null)
    {
        var root = Path.GetFullPath(packRoot);
        var created = (createdFiles ?? [])
            .Select(Path.GetFullPath)
            .Where(path => IsUnder(root, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var files = absoluteFiles
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Where(path => IsUnder(root, path))
            .Where(path => !created.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0 && created.Count == 0) return null;

        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{SafeName(operation)}";
        var directory = Path.Combine(BackupRoot(root), id);
        var backupFiles = Path.Combine(directory, "files");
        System.IO.Directory.CreateDirectory(backupFiles);

        var entries = new List<PackBackupEntry>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var destination = Path.Combine(backupFiles, relative.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            entries.Add(new PackBackupEntry
            {
                RelativePath = relative,
                Length = new FileInfo(file).Length,
            });
        }

        foreach (var file in created)
        {
            entries.Add(new PackBackupEntry
            {
                RelativePath = Path.GetRelativePath(root, file).Replace('\\', '/'),
                DeleteOnRestore = true,
            });
        }

        WriteManifest(directory, new PackBackupManifest
        {
            Id = id,
            Operation = operation,
            Kind = "repair",
            CreatedAt = DateTimeOffset.UtcNow,
            Files = entries,
        });
        return new PackBackupInfo
        {
            Id = id,
            Operation = operation,
            Kind = "repair",
            CreatedAt = DateTimeOffset.UtcNow,
            FileCount = entries.Count,
            Directory = directory,
        };
    }

    public PackBackupInfo? CreateConfigSnapshot(string packRoot, string operation = "配置备份")
    {
        var root = Path.GetFullPath(packRoot);
        var config = Path.Combine(root, "config");
        if (!System.IO.Directory.Exists(config)) return null;

        var files = System.IO.Directory.EnumerateFiles(config, "*", SearchOption.AllDirectories)
            .Where(File.Exists)
            .ToList();
        if (files.Count == 0) return null;

        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-config";
        var directory = Path.Combine(BackupRoot(root), id);
        var backupFiles = Path.Combine(directory, "files");
        System.IO.Directory.CreateDirectory(backupFiles);

        var entries = new List<PackBackupEntry>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var destination = Path.Combine(backupFiles, relative.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            entries.Add(new PackBackupEntry
            {
                RelativePath = relative,
                Length = new FileInfo(file).Length,
            });
        }

        WriteManifest(directory, new PackBackupManifest
        {
            Id = id,
            Operation = operation,
            Kind = "config",
            CreatedAt = DateTimeOffset.UtcNow,
            Files = entries,
        });
        return new PackBackupInfo
        {
            Id = id,
            Operation = operation,
            Kind = "config",
            CreatedAt = DateTimeOffset.UtcNow,
            FileCount = entries.Count,
            Directory = directory,
        };
    }

    public PackBackupRestoreResult Restore(string packRoot, PackBackupInfo snapshot)
    {
        var root = Path.GetFullPath(packRoot);
        var manifestPath = Path.Combine(snapshot.Directory, ManifestName);
        var manifest = File.Exists(manifestPath)
            ? JsonDefaults.Deserialize<PackBackupManifest>(File.ReadAllText(manifestPath))
            : null;
        if (manifest is null)
            throw new InvalidOperationException("备份清单不存在或已损坏。");

        var restored = 0;
        var missing = 0;
        var warnings = new List<string>();
        foreach (var entry in manifest.Files)
        {
            var relative = entry.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var original = Path.GetFullPath(Path.Combine(root, relative));
            if (!IsUnder(root, original))
            {
                warnings.Add($"忽略越界路径：{entry.RelativePath}");
                continue;
            }

            if (entry.DeleteOnRestore)
            {
                try
                {
                    if (File.Exists(original)) File.Delete(original);
                    if (File.Exists(original + ".disabled")) File.Delete(original + ".disabled");
                    restored++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"{entry.RelativePath}：{ex.Message}");
                }
                continue;
            }

            var backup = Path.Combine(snapshot.Directory, "files", relative);
            if (!File.Exists(backup))
            {
                missing++;
                continue;
            }

            try
            {
                if (File.Exists(original)) File.Delete(original);
                if (File.Exists(original + ".disabled")) File.Delete(original + ".disabled");
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.Copy(backup, original, overwrite: true);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{entry.RelativePath}：{ex.Message}");
            }
        }

        return new PackBackupRestoreResult
        {
            RestoredFiles = restored,
            MissingFiles = missing,
            Warnings = warnings,
        };
    }

    public void Delete(PackBackupInfo snapshot)
    {
        if (System.IO.Directory.Exists(snapshot.Directory))
            System.IO.Directory.Delete(snapshot.Directory, recursive: true);
    }

    private static void WriteManifest(string directory, PackBackupManifest manifest)
    {
        System.IO.Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ManifestName), JsonDefaults.Serialize(manifest));
    }

    private static bool IsUnder(string root, string path)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                             Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return cleaned.Length == 0 ? "snapshot" : cleaned;
    }

    private sealed class PackBackupManifest
    {
        public string Id { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public List<PackBackupEntry> Files { get; set; } = [];
    }

    private sealed class PackBackupEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public long Length { get; set; }
        public bool DeleteOnRestore { get; set; }
    }
}
