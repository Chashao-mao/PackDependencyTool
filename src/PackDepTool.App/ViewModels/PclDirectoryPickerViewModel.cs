using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackDepTool.Core.Scanning;

namespace PackDepTool.App.ViewModels;

public sealed partial class PclDirectoryPickerViewModel : ObservableObject
{
    public PclDirectoryPickerViewModel(IReadOnlyList<PclGameDirectoryCandidate> candidates)
    {
        Candidates = candidates;
        _selected = candidates.FirstOrDefault();
    }

    public IReadOnlyList<PclGameDirectoryCandidate> Candidates { get; }

    [ObservableProperty] private PclGameDirectoryCandidate? _selected;

    public event Action<PclGameDirectoryCandidate?>? Completed;

    [RelayCommand]
    private void Use() => Completed?.Invoke(Selected);

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(null);
}
