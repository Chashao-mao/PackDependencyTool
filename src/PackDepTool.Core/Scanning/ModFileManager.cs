using PackDepTool.Core.Models;

namespace PackDepTool.Core.Scanning;

/// <summary>PCL-style mod enable/disable by renaming jar &lt;-&gt; jar.disabled.</summary>
public sealed class ModFileManager
{
    public string ModsDirectory(string packRoot) => Path.Combine(packRoot, "mods");

    public IReadOnlyList<DisabledModFile> ListDisabled(string packRoot)
    {
        var directory = ModsDirectory(packRoot);
        if (!Directory.Exists(directory)) return [];

        try
        {
            return Directory.EnumerateFiles(directory, "*.jar.disabled", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new DisabledModFile
                    {
                        FileName = info.Name,
                        FullPath = info.FullName,
                        Size = info.Length,
                        ModifiedAt = info.LastWriteTimeUtc,
                    };
                })
                .OrderBy(file => file.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public string Disable(ModNode node, string packRoot)
    {
        var path = ResolveLocalFile(node, packRoot);
        if (path is null)
            throw new InvalidOperationException("该模组没有可定位的本地 JAR。");
        if (!path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能禁用 .jar 模组文件。");

        var disabledPath = path + ".disabled";
        if (File.Exists(disabledPath))
            throw new IOException("同名禁用文件已存在。");

        File.Move(path, disabledPath);
        return disabledPath;
    }

    public string MoveToTrash(ModNode node, string packRoot, string? batchDirectory = null)
    {
        var path = ResolveLocalFile(node, packRoot);
        if (path is null)
            throw new InvalidOperationException("该模组没有可定位的本地 JAR。");
        return MoveFileToTrash(path, packRoot, batchDirectory);
    }

    public string MoveFileToTrash(string path, string packRoot, string? batchDirectory = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("文件不存在", path);

        batchDirectory ??= Path.Combine(
            packRoot, ".packdep-trash", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(batchDirectory);

        var destination = Path.Combine(batchDirectory, Path.GetFileName(path));
        var suffix = 1;
        while (File.Exists(destination))
            destination = Path.Combine(batchDirectory, $"{Path.GetFileNameWithoutExtension(path)}-{suffix++}{Path.GetExtension(path)}");

        File.Move(path, destination);
        return destination;
    }

    public string? ResolveLocalFile(ModNode node, string packRoot)
    {
        if (string.IsNullOrWhiteSpace(node.FilePath)) return null;

        var path = Path.IsPathRooted(node.FilePath)
            ? node.FilePath
            : Path.Combine(packRoot, node.FilePath.Replace('/', Path.DirectorySeparatorChar));

        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    public string Enable(string disabledPath)
    {
        if (!File.Exists(disabledPath))
            throw new FileNotFoundException("禁用文件不存在", disabledPath);
        if (!disabledPath.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不是有效的已禁用模组文件。");

        var enabledPath = disabledPath[..^".disabled".Length];
        if (File.Exists(enabledPath))
            throw new IOException("同名启用文件已存在，请先处理冲突。");

        File.Move(disabledPath, enabledPath);
        return enabledPath;
    }

    public int EnableAll(string packRoot)
    {
        var count = 0;
        foreach (var file in ListDisabled(packRoot))
        {
            try
            {
                Enable(file.FullPath);
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Continue enabling the rest; callers can rescan and see what remains.
            }
        }

        return count;
    }
}
