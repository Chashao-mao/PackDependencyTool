using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Models;
using PackDepTool.Core.Persistence;

namespace PackDepTool.App.ViewModels;

public sealed partial class LocalRuleDialogViewModel : ObservableObject
{
    private readonly LocalRule? _existing;

    public LocalRuleDialogViewModel(LocalRule? existing, IReadOnlyList<string> modIds)
    {
        _existing = existing;
        AvailableModIds = modIds;
        TitleText = existing is null ? "新增本地规则" : "编辑本地规则";

        TypeOptions =
        [
            new(LocalRuleType.IgnoreMissing, "忽略缺失依赖",
                "忽略某个缺失依赖。To 填缺失的 modId；From 留空或填 * 表示任意依赖方。"),
            new(LocalRuleType.IgnoreConflict, "忽略冲突",
                "忽略两个模组之间的不兼容声明（双向匹配）。From 与 To 都必填。"),
            new(LocalRuleType.IgnoreVersionMismatch, "忽略版本不符",
                "忽略两个模组之间的版本范围不匹配；To 填 minecraft 可忽略整合包 MC 版本警告。"),
            new(LocalRuleType.ProvideAlias, "别名提供",
                "把 From 这个 modId 视为由 To 提供，从而不再报告缺失依赖。"),
            new(LocalRuleType.OverrideVersionRange, "覆盖版本范围",
                "把 From → To 的版本要求替换为 Value，并重新参与分析。"),
            new(LocalRuleType.ExcludeMod, "排除模组",
                "从依赖图和报告中隐藏 From 指定的模组及其全部关系。"),
        ];

        SelectedType = existing is null
            ? TypeOptions[0]
            : TypeOptions.FirstOrDefault(option => option.Value == existing.Type) ?? TypeOptions[0];

        From = existing?.From ?? string.Empty;
        To = existing?.To ?? string.Empty;
        Value = existing?.Value ?? string.Empty;
        Note = existing?.Note ?? string.Empty;
        PriorityText = (existing?.Priority ?? 0).ToString();
        Enabled = existing?.Enabled ?? true;
        UpdateHint();
    }

    public string TitleText { get; }
    public IReadOnlyList<string> AvailableModIds { get; }
    public IReadOnlyList<LocalRuleTypeOption> TypeOptions { get; }

    [ObservableProperty] private LocalRuleTypeOption? _selectedType;
    [ObservableProperty] private string _from = string.Empty;
    [ObservableProperty] private string _to = string.Empty;
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string _priorityText = "0";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _hint = string.Empty;
    [ObservableProperty] private string _fromLabel = "From modId";
    [ObservableProperty] private string _toLabel = "To modId";
    [ObservableProperty] private string _valueLabel = "值";
    [ObservableProperty] private string _valueHint = string.Empty;
    [ObservableProperty] private bool _showTo = true;
    [ObservableProperty] private bool _showValue;

    public event Action<LocalRule?>? Completed;

    partial void OnSelectedTypeChanged(LocalRuleTypeOption? value) => UpdateHint();

    private void UpdateHint()
    {
        var type = SelectedType?.Value ?? LocalRuleType.IgnoreMissing;
        Hint = SelectedType?.Hint ?? string.Empty;
        ShowTo = type != LocalRuleType.ExcludeMod;
        ShowValue = type == LocalRuleType.OverrideVersionRange;

        (FromLabel, ToLabel, ValueLabel, ValueHint) = type switch
        {
            LocalRuleType.IgnoreMissing => ("依赖方 modId（可空 / *）", "缺失 modId", "值", string.Empty),
            LocalRuleType.IgnoreConflict => ("模组 A modId", "模组 B modId", "值", string.Empty),
            LocalRuleType.IgnoreVersionMismatch => ("依赖方 modId", "被依赖方 modId", "值", string.Empty),
            LocalRuleType.ProvideAlias => ("别名 modId", "提供者 modId", "值", string.Empty),
            LocalRuleType.OverrideVersionRange => ("依赖方 modId", "被依赖方 modId", "新版本范围", "例如 >=1.0 或 [1.20,1.21)"),
            LocalRuleType.ExcludeMod => ("要排除的 modId", "To modId", "值", string.Empty),
            _ => ("From modId", "To modId", "值", string.Empty),
        };
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedType is null)
        {
            ErrorMessage = "请选择规则类型。";
            return;
        }

        if (!int.TryParse(PriorityText.Trim(), out var priority))
        {
            ErrorMessage = "优先级必须是整数（越大越优先）。";
            return;
        }

        var rule = new LocalRule
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            CreatedAt = _existing?.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Type = SelectedType.Value,
            From = Normalize(From),
            To = Normalize(To),
            Value = ShowValue && !string.IsNullOrWhiteSpace(Value) ? Value.Trim() : null,
            Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
            Priority = priority,
            Enabled = Enabled,
        };

        var error = LocalRuleStore.Validate(rule);
        if (error is not null)
        {
            ErrorMessage = error;
            return;
        }

        Completed?.Invoke(rule);
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(null);

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
