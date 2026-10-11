using System.Windows;
using RemoteDock.Core;

namespace RemoteDock.App;

public partial class CredentialsDialog : Window
{
    public string UserName => UserBox.Text.Trim();
    public string Domain => DomainBox.Text.Trim();

    public CredentialsDialog(ConnectionProfile profile)
    {
        InitializeComponent();
        Heading.Text = profile.Name;
        UserBox.Text = profile.UserName;
        DomainBox.Text = profile.Domain;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    public string TakePassword()
    {
        string password = PasswordInput.Password;
        PasswordInput.Clear();
        return password;
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
