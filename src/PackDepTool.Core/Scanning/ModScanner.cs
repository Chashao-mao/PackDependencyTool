using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PackDepTool.Core.Models;

namespace PackDepTool.Core.Scanning;

/// <summary>
/// Walks <c>&lt;pack&gt;/mods/*.jar</c>, hashes each jar, extracts loader metadata and
/// nested (JiJ) jars. Runs in parallel but reports progress deterministically.
/// </summary>
public sealed class ModScanner
{
    private static readonly string[] FabricMetadata = ["fabric.mod.json"];
    private static readonly string[] QuiltMetadata = ["quilt.mod.json"];
    private static readonly string[] ForgeMetadata = ["META-INF/mods.toml"];
    private static readonly string[] NeoForgeMetadata = ["META-INF/neoforge.mods.toml"];
    private static readonly string[] LegacyMetadata = ["mcmod.info"];

    private const long MaxMetadataBytes = 8 * 1024 * 1024;
    private const long MaxNestedJarBytes = 64 * 1024 * 1024;
    private const long MaxIconBytes = 1024 * 1024;

    private readonly ModMetadataParser _parser = new();
    private readonly PackProfileDetector _profileDetector = new();

    public async Task<ScanResult> ScanAsync(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("整合包目录不能为空", nameof(rootPath));

        var fullRoot = Path.GetFullPath(rootPath);
        var (packRoot, modsDir) = ResolveDirectories(fullRoot);

        var result = new ScanResult
        {
            RootPath = packRoot,
            ModsDirectory = modsDir,
        };

        var initialProfile = _profileDetector.Detect(packRoot);
        result.Profile = initialProfile;

        if (!Directory.Exists(modsDir))
        {
            result.Warnings.Add($"未找到 mods 目录：{modsDir}");
            return result;
        }

        var files = Directory
            .EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var disabledCount = Directory
            .EnumerateFiles(modsDir, "*.disabled", SearchOption.TopDirectoryOnly)
            .Count(f => f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase));

        if (disabledCount > 0)
            result.Warnings.Add($"发现 {disabledCount} 个已禁用的模组（*.jar.disabled），未纳入分析");

        if (files.Count == 0)
        {
            result.Warnings.Add("mods 目录中没有可分析的 .jar 文件");
            return result;
        }

        var items = new JarScanResult[files.Count];
        var processed = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 6),
            CancellationToken = cancellationToken,
        };

        progress?.Report(new ScanProgress { Processed = 0, Total = files.Count, Phase = "扫描 JAR" });

        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), parallelOptions, async (index, ct) =>
        {
            var file = files[index];
            var jarResult = await ScanJarAsync(packRoot, file, initialProfile.Loader, ct).ConfigureAwait(false);
            items[index] = jarResult;

            var done = Interlocked.Increment(ref processed);
            progress?.Report(new ScanProgress
            {
                Processed = done,
                Total = files.Count,
                CurrentFile = Path.GetFileName(file),
                Phase = "扫描 JAR",
            });
        }).ConfigureAwait(false);

        result.Jars = items.Where(i => i is not null).ToList();

        var parsedMods = result.Jars.SelectMany(j => j.Mods).Where(m => m.ModId.Length > 0).ToList();
        result.Profile = _profileDetector.Detect(packRoot, parsedMods);
        result.Profile.PackName ??= Path.GetFileName(packRoot);

        foreach (var jar in result.Jars)
        {
            foreach (var warning in jar.Warnings)
                result.Warnings.Add($"{Path.GetFileName(jar.FilePath)}：{warning}");
        }

        progress?.Report(new ScanProgress { Processed = files.Count, Total = files.Count, Phase = "扫描完成" });
        return result;
    }

    private async Task<JarScanResult> ScanJarAsync(
        string packRoot,
        string absolutePath,
        LoaderKind preferredLoader,
        CancellationToken cancellationToken)
    {
        var relative = Path.GetRelativePath(packRoot, absolutePath).Replace('\\', '/');
        var jar = new JarScanResult
        {
            AbsolutePath = absolutePath,
            FilePath = relative,
            ModifiedAt = File.GetLastWriteTimeUtc(absolutePath),
        };

        try
        {
            await using var stream = new FileStream(
                absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

            using var sha1 = SHA1.Create();
            var hash = await sha1.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            jar.Sha1 = Convert.ToHexStringLower(hash);

            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

            var texts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in FabricMetadata.Concat(QuiltMetadata).Concat(ForgeMetadata)
                         .Concat(NeoForgeMetadata).Concat(LegacyMetadata))
            {
                texts[name] = await ReadEntryTextAsync(archive, name, cancellationToken).ConfigureAwait(false);
            }

            texts["META-INF/MANIFEST.MF"] =
                await ReadEntryTextAsync(archive, "META-INF/MANIFEST.MF", cancellationToken).ConfigureAwait(false);

            ParseArchive(archive, texts, preferredLoader, jar, cancellationToken);

            if (jar.Mods.Count == 0)
            {
                var manifestText = texts.GetValueOrDefault("META-INF/MANIFEST.MF");
                if (JarManifest.IsLibrary(manifestText) &&
                    TryDescribeLibraryJar(absolutePath, preferredLoader, manifestText, out var libraryMod))
                {
                    jar.Mods.Add(libraryMod);
                }
                else
                {
                    jar.Warnings.Add("未找到可识别的加载器元数据，已按文件名生成占位节点");
                    jar.Mods.Add(new ParsedMod
                    {
                        ModId = Path.GetFileNameWithoutExtension(absolutePath).ToLowerInvariant(),
                        Name = Path.GetFileNameWithoutExtension(absolutePath),
                        Version = "unknown",
                        Loader = LoaderKind.Unknown,
                    });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            jar.Warnings.Add($"读取失败：{ex.Message}");
            jar.Mods.Add(new ParsedMod
            {
                ModId = Path.GetFileNameWithoutExtension(absolutePath).ToLowerInvariant(),
                Name = Path.GetFileNameWithoutExtension(absolutePath),
                Version = "unknown",
                Loader = LoaderKind.Unknown,
            });
        }

        return jar;
    }

    private static bool TryDescribeLibraryJar(
        string absolutePath,
        LoaderKind preferredLoader,
        string? manifestText,
        out ParsedMod mod)
    {
        mod = new ParsedMod();
        var stem = Path.GetFileNameWithoutExtension(absolutePath);
        var match = Regex.Match(stem, @"^(?<name>.+?)-(?<version>\d+(?:\.\d+)*)(?:[-_].*)?$");
        if (!match.Success) return false;

        var name = match.Groups["name"].Value.Trim(' ', '-', '_');
        var version = match.Groups["version"].Value;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var id = new string(name
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')
            .ToArray()).Trim('-', '_', '.');
        if (id.Length == 0) return false;

        mod = new ParsedMod
        {
            ModId = id,
            Name = name,
            Version = FirstNonEmpty(version, JarManifest.ReadVersion(manifestText), "unknown"),
            Loader = preferredLoader == LoaderKind.Unknown ? LoaderKind.Forge : preferredLoader,
            Description = "FML library JAR（FMLModType: LIBRARY）",
        };
        return true;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "unknown";

    private void ParseArchive(
        ZipArchive archive,
        IReadOnlyDictionary<string, string?> texts,
        LoaderKind preferredLoader,
        JarScanResult jar,
        CancellationToken cancellationToken)
    {
        var order = preferredLoader switch
        {
            LoaderKind.Fabric => new[] { "fabric.mod.json", "quilt.mod.json", "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "mcmod.info" },
            LoaderKind.Quilt => new[] { "quilt.mod.json", "fabric.mod.json", "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "mcmod.info" },
            LoaderKind.Forge => new[] { "META-INF/mods.toml", "META-INF/neoforge.mods.toml", "mcmod.info", "fabric.mod.json", "quilt.mod.json" },
            LoaderKind.NeoForge => new[] { "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "mcmod.info", "fabric.mod.json", "quilt.mod.json" },
            _ => new[] { "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "fabric.mod.json", "quilt.mod.json", "mcmod.info" },
        };

        var available = order.Where(name => !string.IsNullOrWhiteSpace(texts.GetValueOrDefault(name))).ToList();
        if (available.Count == 0) return;

        var selected = available[0];
        var text = texts[selected]!;
        var manifestVersion = JarManifest.ReadVersion(texts.GetValueOrDefault("META-INF/MANIFEST.MF"));
        if (!string.IsNullOrWhiteSpace(manifestVersion))
            text = text.Replace("${file.jarVersion}", manifestVersion, StringComparison.OrdinalIgnoreCase);

        try
        {
            switch (selected)
            {
                case "fabric.mod.json":
                    jar.Mods.Add(_parser.ParseFabric(text));
                    break;
                case "quilt.mod.json":
                    jar.Mods.Add(_parser.ParseQuilt(text));
                    break;
                case "META-INF/mods.toml":
                    jar.Mods.AddRange(_parser.ParseForgeToml(text, selected, neoForge: false));
                    break;
                case "META-INF/neoforge.mods.toml":
                    jar.Mods.AddRange(_parser.ParseForgeToml(text, selected, neoForge: true));
                    break;
                case "mcmod.info":
                    jar.Mods.AddRange(_parser.ParseLegacyMcmodInfo(text));
                    break;
            }
        }
        catch (FormatException ex)
        {
            jar.Warnings.Add($"{selected} 解析失败：{ex.Message}");
        }

        AttachIcons(archive, jar.Mods);
        AlignMultiLoaderMetadata(jar.Mods, preferredLoader, available);

        // Nested jars declared by Fabric's "jars" array.
        foreach (var mod in jar.Mods)
        {
            foreach (var embedded in mod.Embedded.ToList())
            {
                if (string.IsNullOrWhiteSpace(embedded.File)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                var nested = ReadNestedMod(archive, embedded.File!, cancellationToken);
                if (nested is not null)
                {
                    nested.File = embedded.File;
                    jar.Embedded.Add(nested);
                }
                else
                {
                    jar.Warnings.Add($"内嵌 JAR 无法解析：{embedded.File}");
                }
            }
            mod.Embedded.Clear();
        }

        // Forge / NeoForge JarJar metadata.
        var jarJarPaths = ReadJarJarPaths(archive, cancellationToken);
        foreach (var path in jarJarPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nested = ReadNestedMod(archive, path, cancellationToken);
            if (nested is not null)
            {
                nested.File = path;
                jar.Embedded.Add(nested);
            }
        }
    }

    private static void AttachIcons(ZipArchive archive, IEnumerable<ParsedMod> mods)
    {
        foreach (var mod in mods)
        {
            if (mod.IconData is { Length: > 0 }) continue;

            byte[]? bytes = null;
            if (!string.IsNullOrWhiteSpace(mod.IconEntry))
                bytes = ReadIconBytes(archive, mod.IconEntry!);

            bytes ??= ReadFallbackIcon(archive, mod.ModId);
            if (bytes is not null) mod.IconData = bytes;
        }
    }

    private static byte[]? ReadFallbackIcon(ZipArchive archive, string modId)
    {
        var normalizedId = modId.Trim().ToLowerInvariant();
        var candidates = new[]
        {
            "icon.png",
            "logo.png",
            $"assets/{normalizedId}/icon.png",
            $"assets/{normalizedId}/logo.png",
            $"assets/{normalizedId}/textures/icon.png",
        };

        foreach (var candidate in candidates)
        {
            var bytes = ReadIconBytes(archive, candidate);
            if (bytes is not null) return bytes;
        }

        return null;
    }

    private static byte[]? ReadIconBytes(ZipArchive archive, string path)
    {
        var entry = FindEntry(archive, path.TrimStart('/', '.', '\\'));
        if (entry is null || entry.Length <= 0 || entry.Length > MaxIconBytes) return null;

        try
        {
            using var stream = entry.Open();
            using var memory = new MemoryStream((int)entry.Length);
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            return IsSupportedImage(bytes) ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static bool IsSupportedImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12) return false;

        // PNG
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true;
        // JPEG
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return true;
        // GIF
        if (bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F') return true;
        // WebP (RIFF....WEBP)
        if (bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P') return true;

        return false;
    }

    private ParsedEmbedded? ReadNestedMod(ZipArchive outer, string entryPath, CancellationToken cancellationToken)
    {
        var entry = FindEntry(outer, entryPath);
        if (entry is null || entry.Length > MaxNestedJarBytes) return null;

        try
        {
            using var memory = new MemoryStream((int)entry.Length);
            using (var entryStream = entry.Open())
                entryStream.CopyTo(memory);

            memory.Position = 0;
            using var nested = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: true);

            var fabric = ReadEntryText(nested, "fabric.mod.json");
            if (!string.IsNullOrWhiteSpace(fabric))
            {
                var mod = _parser.ParseFabric(fabric);
                return ToEmbedded(mod, LoaderKind.Fabric);
            }

            var quilt = ReadEntryText(nested, "quilt.mod.json");
            if (!string.IsNullOrWhiteSpace(quilt))
            {
                var mod = _parser.ParseQuilt(quilt);
                return ToEmbedded(mod, LoaderKind.Quilt);
            }

            var neoforge = ReadEntryText(nested, "META-INF/neoforge.mods.toml");
            if (!string.IsNullOrWhiteSpace(neoforge))
            {
                var mod = _parser.ParseForgeToml(neoforge, "nested:neoforge.mods.toml", neoForge: true).FirstOrDefault();
                if (mod is not null) return ToEmbedded(mod, LoaderKind.NeoForge);
            }

            var forge = ReadEntryText(nested, "META-INF/mods.toml");
            if (!string.IsNullOrWhiteSpace(forge))
            {
                var mod = _parser.ParseForgeToml(forge, "nested:mods.toml", neoForge: false).FirstOrDefault();
                if (mod is not null) return ToEmbedded(mod, LoaderKind.Forge);
            }

            var legacy = ReadEntryText(nested, "mcmod.info");
            if (!string.IsNullOrWhiteSpace(legacy))
            {
                var mod = _parser.ParseLegacyMcmodInfo(legacy).FirstOrDefault();
                if (mod is not null) return ToEmbedded(mod, LoaderKind.Forge);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// A jar can ship metadata for several loaders (for example Fabric plus
    /// Forge). The runtime loader is the pack loader, so use it when the
    /// selected fallback metadata still describes a compatible loader.
    /// </summary>
    private static void AlignMultiLoaderMetadata(
        IReadOnlyList<ParsedMod> mods,
        LoaderKind preferredLoader,
        IReadOnlyList<string> available)
    {
        if (mods.Count == 0 || available.Count < 2) return;
        var hasFabric = available.Contains("fabric.mod.json", StringComparer.OrdinalIgnoreCase);
        var hasForge = available.Contains("META-INF/mods.toml", StringComparer.OrdinalIgnoreCase);

        switch (preferredLoader)
        {
            case LoaderKind.Quilt when hasFabric:
                foreach (var mod in mods.Where(mod => mod.Loader == LoaderKind.Fabric))
                    mod.Loader = LoaderKind.Quilt;
                break;
            case LoaderKind.NeoForge when hasFabric && hasForge:
                foreach (var mod in mods.Where(mod => mod.Loader is LoaderKind.Fabric or LoaderKind.Forge))
                    mod.Loader = LoaderKind.NeoForge;
                break;
        }
    }

    private static ParsedEmbedded ToEmbedded(ParsedMod mod, LoaderKind loader) => new()
    {
        ModId = mod.ModId,
        Name = mod.Name,
        Version = mod.Version,
        Loader = loader,
    };

    private static IEnumerable<string> ReadJarJarPaths(ZipArchive archive, CancellationToken cancellationToken)
    {
        var metadata = ReadEntryText(archive, "META-INF/jarjar/metadata.json");
        if (string.IsNullOrWhiteSpace(metadata)) yield break;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(metadata, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("jars", out var jars) || jars.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (var jar in jars.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (jar.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                {
                    var value = path.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) yield return value!;
                }
            }
        }
    }

    private static async Task<string?> ReadEntryTextAsync(
        ZipArchive archive, string name, CancellationToken cancellationToken)
    {
        var entry = FindEntry(archive, name);
        if (entry is null || entry.Length > MaxMetadataBytes) return null;

        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadEntryText(ZipArchive archive, string name)
    {
        var entry = FindEntry(archive, name);
        if (entry is null || entry.Length > MaxMetadataBytes) return null;

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is not null) return entry;

        foreach (var candidate in archive.Entries)
        {
            if (string.Equals(candidate.FullName, name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }

    private static (string PackRoot, string ModsDir) ResolveDirectories(string fullRoot)
    {
        var modsSubdirectory = Path.Combine(fullRoot, "mods");
        if (Directory.Exists(modsSubdirectory))
            return (fullRoot, modsSubdirectory);

        if (string.Equals(Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar)), "mods", StringComparison.OrdinalIgnoreCase))
            return (Path.GetDirectoryName(fullRoot.TrimEnd(Path.DirectorySeparatorChar)) ?? fullRoot, fullRoot);

        return (fullRoot, fullRoot);
    }
}
