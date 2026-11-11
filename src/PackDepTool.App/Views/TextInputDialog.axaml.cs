using Avalonia.Controls;
using Avalonia.Input;

namespace PackDepTool.App.Views;

public partial class TextInputDialog : Window
{
    public TextInputDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public static async Task<string?> ShowAsync(
        Window owner, string title, string label, string initialValue = "")
    {
        var dialog = new TextInputDialog();
        dialog.Title = title;
        dialog.TitleText.Text = title;
        dialog.LabelText.Text = label;
        dialog.InputBox.Text = initialValue;
        return await dialog.ShowDialog<string?>(owner);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private void OnOkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Accept();

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);

    private void Accept()
    {
        var value = InputBox.Text?.Trim() ?? string.Empty;
        if (value.Length == 0) return;
        Close(value);
    }
}
