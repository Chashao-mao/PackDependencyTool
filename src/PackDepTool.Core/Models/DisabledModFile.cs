namespace PackDepTool.Core.Models;

public sealed class DisabledModFile
{
    public string FileName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public long Size { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }

    public string DisplayName => FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
        ? FileName[..^".disabled".Length]
        : FileName;

    public string SizeText => Size < 1024 * 1024
        ? $"{Size / 1024d:0.#} KB"
        : $"{Size / 1024d / 1024d:0.#} MB";
}
