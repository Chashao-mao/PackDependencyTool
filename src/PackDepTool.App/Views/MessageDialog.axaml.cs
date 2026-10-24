using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PackDepTool.App.Views;

public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    public static async Task<bool> ShowAsync(
        Window owner,
        string title,
        string message,
        string okText = "确定",
        string? cancelText = null)
    {
        var dialog = new MessageDialog();
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.OkButton.Content = okText;
        dialog.CancelButton.IsVisible = cancelText is not null;
        dialog.CancelButton.Content = cancelText ?? "取消";
        dialog.CancelButton.Click += (_, _) => dialog.Close(false);
        dialog.OkButton.Click += (_, _) => dialog.Close(true);

        return await dialog.ShowDialog<bool>(owner);
    }
}
