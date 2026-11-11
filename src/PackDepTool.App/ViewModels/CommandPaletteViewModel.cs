using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PackDepTool.App.ViewModels;

public sealed class CommandPaletteItem
{
    public CommandPaletteItem(string title, string description, string category, string shortcut, ICommand command)
    {
        Title = title;
        Description = description;
        Category = category;
        Shortcut = shortcut;
        Command = command;
    }

    public string Title { get; }
    public string Description { get; }
    public string Category { get; }
    public string Shortcut { get; }
    public string SearchText => $"{Title} {Description} {Category}";
    public ICommand Command { get; }

    public bool CanExecute => Command.CanExecute(null);
}

public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly IReadOnlyList<CommandPaletteItem> _all;

    public CommandPaletteViewModel(IReadOnlyList<CommandPaletteItem> items)
    {
        _all = items;
        foreach (var item in items) Items.Add(item);
        SelectedItem = Items.FirstOrDefault();
    }

    public System.Collections.ObjectModel.ObservableCollection<CommandPaletteItem> Items { get; } = [];
    [ObservableProperty] private CommandPaletteItem? _selectedItem;
    [ObservableProperty] private string _searchText = string.Empty;

    public event Action? CloseRequested;

    partial void OnSearchTextChanged(string value)
    {
        Items.Clear();
        var terms = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var item in _all)
        {
            if (terms.Length == 0 ||
                terms.All(term => item.SearchText.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
                Items.Add(item);
        }
        SelectedItem = Items.FirstOrDefault();
    }

    [RelayCommand]
    private void Execute()
    {
        if (SelectedItem is not { } item) return;
        if (!item.CanExecute) return;
        CloseRequested?.Invoke();
        item.Command.Execute(null);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
