using Avalonia.Controls;
using Avalonia.Input;

namespace PackDepTool.App.Views;

public partial class TextInputDialog : Window
{
    public TextInputDialog()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Close(ValueBox.Text);
        CancelButton.Click += (_, _) => Close(null);
        ValueBox.KeyDown += OnValueKeyDown;
    }

    private void OnValueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Close(ValueBox.Text);
        e.Handled = true;
    }

    public static async Task<string?> ShowAsync(
        Window owner,
        string title,
        string message,
        string label,
        string initialValue)
    {
        var dialog = new TextInputDialog();
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.LabelText.Text = label;
        dialog.ValueBox.Text = initialValue;
        dialog.ValueBox.SelectAll();
        dialog.ValueBox.Focus();

        return await dialog.ShowDialog<string?>(owner);
    }
}
