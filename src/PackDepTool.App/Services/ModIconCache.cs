using Avalonia.Media.Imaging;
using PackDepTool.Core.Models;

namespace PackDepTool.App.Services;

/// <summary>Decodes and caches JAR icons for list/detail rendering.</summary>
public static class ModIconCache
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    /// <summary>Drops decoded icons when a different pack is opened.</summary>
    public static void Clear() => Cache.Clear();

    public static Bitmap? Get(ModNode node)
    {
        var key = $"{node.Id}|{node.Version}|{node.IconData?.Length ?? 0}|{node.IconUrl}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        Bitmap? bitmap = null;
        if (node.IconData is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(node.IconData, writable: false);
                bitmap = new Bitmap(stream);
            }
            catch (Exception)
            {
                bitmap = null;
            }
        }

        Cache[key] = bitmap;
        return bitmap;
    }
}
