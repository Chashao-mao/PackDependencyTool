using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using PackDepTool.App.Services;

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

    private static bool IsHeader(string value)
    {
        var text = value.Trim();
        return text.Equals("name", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("mod", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("modid", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("slug", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("模组", StringComparison.Ordinal) ||
               text.Equals("名称", StringComparison.Ordinal);
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
                new FilePickerFileType("模组列表（文本 / 表格）")
                {
                    Patterns = ["*.txt", "*.csv", "*.tsv", "*.xlsx", "*.list", "*.md"],
                },
                FilePickerFileTypes.All,
            ],
        });
        var file = files.FirstOrDefault();
        if (file is null) return;
        var localPath = file.TryGetLocalPath();
        if (localPath is null) return;
        try
        {
            var entries = await ModListFileReader.ReadFirstColumnAsync(localPath);
            InputBox.Text = string.Join(Environment.NewLine, entries
                .Where(entry => !IsHeader(entry)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            InputBox.Text = $"# 读取失败：{ex.Message}";
        }
    }
}
