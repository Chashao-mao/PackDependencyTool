using System.Text;
using System.Text.Json;

namespace PackDepTool.Core.Persistence;

public sealed class ViewStateSnapshot
{
    public int Schema { get; set; } = 1;
    public string Search { get; set; } = string.Empty;
    public bool ShowRequires { get; set; } = true;
    public bool ShowIncompatible { get; set; } = true;
    public bool ShowOptional { get; set; }
    public bool ShowEmbedded { get; set; }
    public bool ShowRecommends { get; set; }
    public bool ShowManual { get; set; } = true;
    public bool ShowFabric { get; set; } = true;
    public bool ShowQuilt { get; set; } = true;
    public bool ShowForge { get; set; } = true;
    public bool ShowNeoForge { get; set; } = true;
    public bool ShowUnknown { get; set; } = true;
    public bool OnlyProblems { get; set; }
    public bool OnlyUpdatable { get; set; }
    public bool HideIsolated { get; set; }
    public bool ShowLabels { get; set; } = true;
    public bool FocusEdgesOnly { get; set; } = true;
    public bool ShowUninstalledOptional { get; set; }
    public bool ShowOptionalInReport { get; set; }
    public bool ShowMissing { get; set; } = true;
    public string StatusFilter { get; set; } = "all";
    public string SideFilter { get; set; } = "all";
    public string SortOption { get; set; } = "name";
    public string CategoryFilter { get; set; } = "all";
    public string ViewOption { get; set; } = "graph";
    public string GroupMode { get; set; } = "none";
}

/// <summary>
/// Encodes only presentation choices (never paths, tokens or account data) into
/// a compact, copyable string that another PackDependencyTool window can restore.
/// </summary>
public static class ViewStateCodec
{
    public const string Prefix = "pdt-view:v1:";

    public static string Encode(ViewStateSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot);
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static ViewStateSnapshot? Decode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var text = code.Trim();
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var payload = text[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            var snapshot = JsonSerializer.Deserialize<ViewStateSnapshot>(json);
            return snapshot?.Schema == 1 ? snapshot : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
