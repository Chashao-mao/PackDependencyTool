using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;

namespace PackDepTool.App.Views;

public partial class ModListImportDialog : Window
{
    public ModListImportDialog()
    {
        InitializeComponent();
        UpdatePreview();
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewList is null || CountText is null) return;
        var entries = (InputBox.Text ?? string.Empty)
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split([',', ';'], StringSplitOptions.TrimEntries)[0])
            .Where(line => line.Length > 0)
            .ToList();
        CountText.Text = $"{entries.Count} 条";
        PreviewList.ItemsSource = entries;
    }

    public static async Task<string?> ShowAsync(Window owner)
    {
        var dialog = new ModListImportDialog();
        return await dialog.ShowDialog<string?>(owner);
    }

    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        Close(text);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnPickFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择模组列表",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("文本列表") { Patterns = ["*.txt", "*.csv", "*.list", "*.md"] },
                FilePickerFileTypes.All,
            ],
        });
        var file = files.FirstOrDefault();
        if (file is null) return;
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        InputBox.Text = await reader.ReadToEndAsync();
    }
}
