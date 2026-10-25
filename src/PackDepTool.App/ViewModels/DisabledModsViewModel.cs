using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Models;

namespace PackDepTool.App.ViewModels;

public enum DisabledModsAction
{
    Enable,
    Delete,
}

public sealed class DisabledModsResult
{
    public DisabledModsAction Action { get; init; }
    public List<DisabledModItem> Items { get; } = [];
}

public sealed partial class DisabledModsViewModel : ObservableObject
{
    public DisabledModsViewModel(IReadOnlyList<DisabledModFile> files)
    {
        foreach (var file in files)
        {
            var item = new DisabledModItem(file);
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(DisabledModItem.IsChecked)) return;
                OnPropertyChanged(nameof(HasChecked));
            };
            Items.Add(item);
        }
        _selected = Items.FirstOrDefault();
    }

    public ObservableCollection<DisabledModItem> Items { get; } = [];

    [ObservableProperty] private DisabledModItem? _selected;

    public bool HasChecked => Items.Any(item => item.IsChecked);

    public event Action<DisabledModsResult?>? Completed;

    private IReadOnlyList<DisabledModItem> Chosen =>
        Items.Where(item => item.IsChecked).DefaultIfEmpty(Selected).Where(item => item is not null)
            .Select(item => item!).Distinct().ToList();

    [RelayCommand]
    private void EnableSelected()
    {
        var chosen = Chosen;
        if (chosen.Count == 0) return;
        var result = new DisabledModsResult { Action = DisabledModsAction.Enable };
        result.Items.AddRange(chosen);
        Completed?.Invoke(result);
    }

    [RelayCommand]
    private void EnableAll()
    {
        var result = new DisabledModsResult { Action = DisabledModsAction.Enable };
        result.Items.AddRange(Items);
        Completed?.Invoke(result);
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        var chosen = Chosen;
        if (chosen.Count == 0) return;
        var result = new DisabledModsResult { Action = DisabledModsAction.Delete };
        result.Items.AddRange(chosen);
        Completed?.Invoke(result);
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(null);
}
