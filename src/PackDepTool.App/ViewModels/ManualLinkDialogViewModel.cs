using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Models;

namespace PackDepTool.App.ViewModels;

public sealed partial class ManualLinkDialogViewModel : ObservableObject
{
    private readonly string _id;
    private readonly DateTimeOffset _createdAt;

    public ManualLinkDialogViewModel(ManualLink? existing, IReadOnlyList<string> modIds, string? defaultFrom)
    {
        AvailableModIds = modIds;
        TypeOptions =
        [
            new EdgeTypeOption(EdgeType.Requires, "必需（requires）"),
            new EdgeTypeOption(EdgeType.Optional, "可选（optional）"),
            new EdgeTypeOption(EdgeType.Incompatible, "不兼容（incompatible）"),
            new EdgeTypeOption(EdgeType.Embedded, "内嵌（embedded）"),
            new EdgeTypeOption(EdgeType.Recommends, "推荐（recommends）"),
            new EdgeTypeOption(EdgeType.Manual, "手动（manual）"),
        ];
        SideOptions =
        [
            new SideOption(ModSide.Both, "双端"),
            new SideOption(ModSide.Client, "客户端"),
            new SideOption(ModSide.Server, "服务端"),
            new SideOption(ModSide.Unknown, "未知"),
        ];

        _id = existing?.Id ?? Guid.NewGuid().ToString("N");
        _createdAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow;
        IsEdit = existing is not null;
        TitleText = IsEdit ? "编辑手动关系" : "新增手动关系";

        From = existing?.From ?? defaultFrom ?? string.Empty;
        To = existing?.To ?? string.Empty;
        VersionRange = existing?.VersionRange ?? string.Empty;
        Note = existing?.Note ?? string.Empty;
        SelectedType = TypeOptions.FirstOrDefault(t => t.Value == (existing?.Type ?? EdgeType.Requires)) ?? TypeOptions[0];
        SelectedSide = SideOptions.FirstOrDefault(s => s.Value == (existing?.Side ?? ModSide.Both)) ?? SideOptions[0];
    }

    public string TitleText { get; }
    public bool IsEdit { get; }
    public IReadOnlyList<string> AvailableModIds { get; }
    public IReadOnlyList<EdgeTypeOption> TypeOptions { get; }
    public IReadOnlyList<SideOption> SideOptions { get; }

    [ObservableProperty] private string _from = string.Empty;
    [ObservableProperty] private string _to = string.Empty;
    [ObservableProperty] private string _versionRange = string.Empty;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private EdgeTypeOption? _selectedType;
    [ObservableProperty] private SideOption? _selectedSide;
    [ObservableProperty] private string _errorMessage = string.Empty;

    partial void OnFromChanged(string value) => ErrorMessage = string.Empty;
    partial void OnToChanged(string value) => ErrorMessage = string.Empty;

    public event Action<ManualLink?>? Completed;

    [RelayCommand]
    private void Save()
    {
        var from = From.Trim().ToLowerInvariant();
        var to = To.Trim().ToLowerInvariant();
        if (from.Length == 0 || to.Length == 0)
        {
            ErrorMessage = "起点和终点都必须填写。";
            return;
        }

        if (from == to)
        {
            ErrorMessage = "起点与终点不能相同。";
            return;
        }

        var link = new ManualLink
        {
            Id = _id,
            CreatedAt = _createdAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            From = from,
            To = to,
            Type = SelectedType?.Value ?? EdgeType.Requires,
            Side = SelectedSide?.Value ?? ModSide.Both,
            VersionRange = string.IsNullOrWhiteSpace(VersionRange) ? null : VersionRange.Trim(),
            Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
        };

        Completed?.Invoke(link);
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(null);
}
