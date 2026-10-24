using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Models;

namespace PackDepTool.App.ViewModels;

public sealed class DisabledModsResult
{
    public List<DisabledModItem> Items { get; } = [];
}

public sealed partial class DisabledModsViewModel : ObservableObject
{
    public DisabledModsViewModel(IReadOnlyList<DisabledModFile> files)
    {
        foreach (var file in files)
            Items.Add(new DisabledModItem(file));
        _selected = Items.FirstOrDefault();
    }

    public ObservableCollection<DisabledModItem> Items { get; } = [];

    [ObservableProperty] private DisabledModItem? _selected;

    public event Action<DisabledModsResult?>? Completed;

    [RelayCommand]
    private void EnableSelected()
    {
        if (Selected is null) return;
        var result = new DisabledModsResult();
        result.Items.Add(Selected);
        Completed?.Invoke(result);
    }

    [RelayCommand]
    private void EnableAll()
    {
        var result = new DisabledModsResult();
        result.Items.AddRange(Items);
        Completed?.Invoke(result);
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(null);
}
