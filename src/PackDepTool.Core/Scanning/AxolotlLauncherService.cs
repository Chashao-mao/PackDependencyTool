using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PackDepTool.Core.Models;
using PackDepTool.Core.Scanning;

namespace PackDepTool.Core.Scanning;

public sealed class AxolotlInstance
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string? GameDirectoryOverride { get; init; }
    public string MinecraftVersion { get; init; } = "?";
    public string Loader { get; init; } = string.Empty;
    public string? LoaderVersion { get; init; }
    public string? LastPlayed { get; init; }
}

/// <summary>
/// Lightweight Axolotl Launcher adapter. Reads its SQLite database through
/// Windows' built-in winsqlite3 and launches instances through the documented
/// axolotl:// URL scheme. No Axolotl code is linked or copied.
/// </summary>
public sealed class AxolotlLauncherService
{
    private const string DbRelativePath = @"red.ghs.axolotl\release\app.db";
    private static readonly string[] ExeCandidates =
    [
        @"E:\Axolotl\AxolotlLauncher\Axolotl Launcher.exe",
        @"%LOCALAPPDATA%\Programs\Axolotl Launcher\Axolotl Launcher.exe",
        @"%LOCALAPPDATA%\Axolotl Launcher\Axolotl Launcher.exe",
        @"%PROGRAMFILES%\Axolotl Launcher\Axolotl Launcher.exe",
    ];

    public string? ExecutablePath { get; private set; }
    public string? DatabasePath { get; private set; }
    public string? LauncherRoot { get; private set; }
    public bool IsInstalled => !string.IsNullOrWhiteSpace(ExecutablePath) ||
                               !string.IsNullOrWhiteSpace(DatabasePath);

    public void Detect(string? configuredExecutable = null)
    {
        ExecutablePath = null;
        DatabasePath = null;
        if (!string.IsNullOrWhiteSpace(configuredExecutable) && File.Exists(configuredExecutable))
            ExecutablePath = Path.GetFullPath(configuredExecutable);

        foreach (var candidate in ExeCandidates)
        {
            if (ExecutablePath is not null) break;
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (File.Exists(expanded)) ExecutablePath = expanded;
        }

        if (ExecutablePath is not null)
        {
            LauncherRoot = Directory.GetParent(Path.GetDirectoryName(ExecutablePath) ?? string.Empty)?.FullName;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var databases = new[]
        {
            Path.Combine(appData, DbRelativePath),
            Path.Combine(appData, "red.ghs.axolotl", "app.db"),
            Path.Combine(localData, DbRelativePath),
            Path.Combine(localData, "red.ghs.axolotl", "app.db"),
        };
        DatabasePath = databases.FirstOrDefault(File.Exists);
    }

    public IReadOnlyList<AxolotlInstance> ListInstances()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath) || !File.Exists(DatabasePath)) return [];
        var result = new List<AxolotlInstance>();
        IntPtr database = IntPtr.Zero;
        try
        {
            if (Sqlite3.sqlite3_open_v2(DatabasePath, out database, 1, IntPtr.Zero) != 0)
                return [];
            const string sql = """
                select i.id, i.name, i.path, i.game_dir_override,
                       coalesce((select s.game_version from instance_content_sets s
                                 where s.instance_id = i.id order by s.created desc limit 1), '?'),
                       coalesce((select s.loader from instance_content_sets s
                                 where s.instance_id = i.id order by s.created desc limit 1), ''),
                       (select s.loader_version from instance_content_sets s
                        where s.instance_id = i.id order by s.created desc limit 1),
                       i.last_played
                from instances i
                order by i.pinned_at is not null desc, i.last_played desc, i.name
                """;
            if (Read(database, sql, result)) return result;
            // Older Axolotl builds do not expose content sets/last_played;
            // keep instance discovery working instead of returning nothing.
            Read(database, "select id, name, path from instances order by name", result);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return [];
        }
        finally
        {
            if (database != IntPtr.Zero) Sqlite3.sqlite3_close(database);
        }
        return result;
    }

    public string ResolvePath(AxolotlInstance instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.GameDirectoryOverride) &&
            Directory.Exists(instance.GameDirectoryOverride))
            return instance.GameDirectoryOverride;
        if (Path.IsPathRooted(instance.Path) && Directory.Exists(instance.Path))
            return instance.Path;

        var roots = new[]
        {
            LauncherRoot,
            LauncherRoot is null ? null : Directory.GetParent(LauncherRoot)?.FullName,
        }.Where(root => !string.IsNullOrWhiteSpace(root));
        foreach (var root in roots)
        {
            foreach (var relative in new[]
                     {
                         Path.Combine("mc", "profiles"),
                         "profiles",
                         Path.Combine("mc", "instances"),
                         "instances",
                     })
            {
                var candidate = Path.Combine(root!, relative, instance.Path);
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return instance.GameDirectoryOverride ?? instance.Path;
    }

    public MinecraftInstanceInfo ToCatalog(AxolotlInstance instance)
    {
        var root = ResolvePath(instance);
        var loader = instance.Loader.ToLowerInvariant() switch
        {
            "fabric" => LoaderKind.Fabric,
            "quilt" => LoaderKind.Quilt,
            "forge" => LoaderKind.Forge,
            "neoforge" => LoaderKind.NeoForge,
            _ => LoaderKind.Unknown,
        };
        var mods = Path.Combine(instance.GameDirectoryOverride ?? instance.Path, "mods");
        var count = Directory.Exists(mods)
            ? Directory.EnumerateFiles(mods, "*.jar").Count()
            : 0;
        return new MinecraftInstanceInfo
        {
            Name = instance.Name,
            Path = root,
            GameDirectory = root,
            EffectiveRoot = root,
            MinecraftVersion = instance.MinecraftVersion,
            Loader = loader,
            ModCount = count,
            ModifiedAt = DateTimeOffset.MinValue,
            ExternalLauncher = "Axolotl",
            ExternalId = instance.Id,
        };
    }

    private static bool Read(IntPtr database, string sql, List<AxolotlInstance> result)
    {
        if (Sqlite3.sqlite3_prepare_v2(database, sql, -1, out var statement, IntPtr.Zero) != 0)
            return false;
        var columns = Sqlite3.sqlite3_column_count(statement);
        try
        {
            while (Sqlite3.sqlite3_step(statement) == 100)
            {
                result.Add(new AxolotlInstance
                {
                    Id = Text(statement, 0),
                    Name = Text(statement, 1),
                    Path = Text(statement, 2),
                    GameDirectoryOverride = columns > 3 ? Nullable(Text(statement, 3)) : null,
                    MinecraftVersion = columns > 4 ? Text(statement, 4) : "?",
                    Loader = columns > 5 ? Text(statement, 5) : string.Empty,
                    LoaderVersion = columns > 6 ? Nullable(Text(statement, 6)) : null,
                    LastPlayed = columns > 7 ? Nullable(Text(statement, 7)) : null,
                });
            }
            return true;
        }
        finally
        {
            Sqlite3.sqlite3_finalize(statement);
        }
    }

    public bool Launch(string instanceId) => OpenUrl($"axolotl://launch?instance_id={Uri.EscapeDataString(instanceId)}");

    public bool OpenInstance(string instanceId) =>
        OpenUrl($"axolotl://instance/{Uri.EscapeDataString(instanceId)}");

    private static bool OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string Text(IntPtr statement, int index)
    {
        var pointer = Sqlite3.sqlite3_column_text(statement, index);
        return pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
    }

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

internal static class Sqlite3
{
    private const string Library = "winsqlite3.dll";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_open_v2(string filename, out IntPtr database, int flags, IntPtr vfs);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_close(IntPtr database);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_prepare_v2(
        IntPtr database, string sql, int bytes, out IntPtr statement, IntPtr tail);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_step(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_column_count(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sqlite3_column_text(IntPtr statement, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_finalize(IntPtr statement);
}
