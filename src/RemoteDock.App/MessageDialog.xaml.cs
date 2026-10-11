using System.Windows;
using System.Windows.Media;

namespace RemoteDock.App;

public partial class MessageDialog : Window
{
    private MessageBoxResult _result;

    private MessageDialog(Window owner, string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        Owner = owner;
        Title = Heading.Text = title;
        MessageText.Text = message;
        bool confirmation = buttons == MessageBoxButton.YesNo;
        _result = confirmation ? MessageBoxResult.No : MessageBoxResult.OK;
        DismissButton.Content = confirmation ? "No" : "Close";
        DismissButton.IsDefault = !confirmation;
        ConfirmButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.IsDefault = confirmation;
        if (image == MessageBoxImage.Error)
            Accent.Background = new SolidColorBrush(Color.FromRgb(240, 172, 172));
    }

    public static MessageBoxResult ShowMessage(Window owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new MessageDialog(owner, message, title, buttons, image);
        dialog.ShowDialog();
        return dialog._result;
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        _result = MessageBoxResult.Yes;
        DialogResult = true;
    }
}
