using System.IO.Compression;
using System.Text;

namespace PackDepTool.Core.Analysis;

public sealed record CrashLogSource(string Name, string Text);

/// <summary>
/// Reads a crash report, a plain log, or a launcher/log bundle zip.
/// Crash reports and latest.log are preferred, debug.log is skipped to avoid
/// drowning the actual crash in tens of megabytes of noise.
/// </summary>
public static class CrashLogBundleReader
{
    private const int MaxFileChars = 4 * 1024 * 1024;
    private const long MaxEntryBytes = 8 * 1024 * 1024;

    public static async Task<List<CrashLogSource>> ReadAsync(
        string path, CancellationToken cancellationToken = default)
    {
        if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            if (text.Length > MaxFileChars) text = text[..MaxFileChars];
            return [new CrashLogSource(Path.GetFileName(path), text)];
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = archive.Entries
            .Where(entry => entry.Length is > 0 and <= MaxEntryBytes)
            .Where(entry => entry.FullName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                            entry.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .Where(entry => !Path.GetFileName(entry.FullName)
                .Equals("debug.log", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => Path.GetFileName(entry.FullName)
                .StartsWith("crash", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(entry => Path.GetFileName(entry.FullName)
                .Equals("latest.log", StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .ToList();

        var result = new List<CrashLogSource>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8, true);
            var text = await reader.ReadToEndAsync(cancellationToken);
            if (text.Length > MaxFileChars) text = text[..MaxFileChars];
            result.Add(new CrashLogSource(entry.FullName, text));
        }
        return result;
    }
}
