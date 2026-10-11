using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteDock.App;
using RemoteDock.Core;

namespace RemoteDock.SmokeTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new RemoteDock.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string sandbox = Path.Combine(Path.GetTempPath(), "opencode", "RemoteDock.Smoke", Guid.NewGuid().ToString("N"));
        var store = new ConnectionStore(Path.Combine(sandbox, "connections.json"));
        var group = new ConnectionGroup { Name = "Lab", Defaults = new() { Domain = "LAB", Scale = new(2, 3) } };
        var savedProfile = new ConnectionProfile { Name = "Saved test connection", Host = "offline.invalid", GroupId = group.Id, Inherit = InheritedSettings.All };
        store.SaveCatalog(new() { Groups = [group], Connections = [savedProfile] });
        string credentialTarget = CredentialStore.ConnectionTarget(Guid.NewGuid());
        bool credentialWritten = false;
        app.InitializeComponent();
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                app.MainWindow = new MainWindow(store);
                await Task.Yield();
                window = (MainWindow)app.MainWindow;
                await Task.Delay(150);
                var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                image.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                Directory.CreateDirectory("artifacts/smoke");
                using (var file = File.Create("artifacts/smoke/startup.png")) encoder.Save(file);

                Require(((Button)window.FindName("WelcomeAction")).Content.ToString() == "Connect to Saved test connection",
                    "The welcome screen treats an existing connection as the first connection.");
                var tree = (TreeView)window.FindName("ConnectionsList");
                var groupItem = (TreeViewItem)tree.Items[0];
                var connectionItem = (TreeViewItem)groupItem.Items[0];
                Require(groupItem.Tag is ConnectionGroup && connectionItem.Tag is ConnectionProfile, "The group hierarchy was not rendered.");
                tree.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = connectionItem });
                Require(ReferenceEquals(tree.SelectedItem, connectionItem), "Right-click did not select the connection.");
                bool editOpened = false;
                _ = window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var openEditor = app.Windows.OfType<ConnectionEditor>().Single();
                    editOpened = ((TextBox)openEditor.FindName("HostBox")).Text == "offline.invalid";
                    openEditor.Close();
                }), DispatcherPriority.Background);
                tree.ContextMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Edit")
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(editOpened, "The context-menu Edit action did not open the selected connection.");

                CredentialStore.Write(credentialTarget, "smoke-test-user", "temporary-smoke-test-secret");
                credentialWritten = true;
                Require(CredentialStore.Read(credentialTarget) == new SavedCredential("smoke-test-user", "temporary-smoke-test-secret"),
                    "Windows Credential Manager round-trip failed.");
                Require(!File.ReadAllText(Path.Combine(sandbox, "connections.json")).Contains("temporary-smoke-test-secret"),
                    "A password appeared in the connection file.");

                var profile = new ConnectionProfile { Name = "Offline RDP probe", Host = "offline.invalid" };
                var session = window.AddSession(profile, "", connect: false);
                await Task.Delay(500);
                Require(session.Status == "RDP control initialized (offline)", session.Status);

                var host = (WindowsFormsHost)session.Content;
                var client = host.Child.Controls[0];
                int DesktopWidth() => (int)client.GetType().GetProperty("DesktopWidth")!.GetValue(client)!;
                int DesktopHeight() => (int)client.GetType().GetProperty("DesktopHeight")!.GetValue(client)!;
                var initialDesktop = new PixelSize(DesktopWidth(), DesktopHeight());
                var expectedDesktop = DisplayPolicy.RequestedDesktop(profile, new(host.Child.ClientSize.Width, host.Child.ClientSize.Height));
                Require(initialDesktop == expectedDesktop, $"Initial desktop {initialDesktop} differs from adaptive policy {expectedDesktop}.");

                var previousClient = client;
                ((Button)window.FindName("ReconnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(400);
                Require(!app.Windows.OfType<CredentialsDialog>().Any(), "Reconnect opened a credential dialog.");
                Require(((ListBox)window.FindName("Tabs")).Items.Count == 1, "Reconnect replaced or duplicated the session tab.");
                client = host.Child.Controls[0];
                Require(previousClient.IsDisposed && !ReferenceEquals(previousClient, client), "Reconnect did not recreate the native connection.");
                Require(session.Status == "RDP control initialized (offline)", session.Status);

                var autoHide = (CheckBox)window.FindName("AutoHideBox");
                autoHide.IsChecked = true;
                autoHide.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Task.Delay(150);
                var panelBeforeReveal = host.Child.ClientSize;
                ((Button)window.FindName("SidebarRevealButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(150);
                var popup = (Popup)window.FindName("SidebarPopup");
                Require(popup.IsOpen, "The auto-hidden sidebar could not be revealed.");
                Require(host.Child.ClientSize == panelBeforeReveal, "Revealing the sidebar resized the remote panel.");
                Require(new ConnectionStore(Path.Combine(sandbox, "connections.json")).LoadCatalog().AutoHideConnections, "Auto-hide preference was not saved.");
                popup.IsOpen = false;
                autoHide.IsChecked = false;
                autoHide.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                window.Width = 1600;
                window.Height = 950;
                await Task.Delay(400);
                Require(new PixelSize(DesktopWidth(), DesktopHeight()) == initialDesktop, "An offline control attempted remote renegotiation.");
                Require(client.Width > 0 && client.Height > 0, "RDP control has no visible area.");

                var second = window.AddSession(profile with { Id = Guid.NewGuid(), Name = "Offline Hyper-V probe",
                    Kind = ConnectionKind.HyperV, Port = 2179, VirtualMachineId = Guid.NewGuid() }, "", connect: false);
                await Task.Delay(400);
                Require(second.Status == "RDP control initialized (offline)", second.Status);
                Require(session.Visibility == Visibility.Collapsed, "Inactive session is still visible.");

                ((ListBox)window.FindName("Tabs")).SelectedIndex = 0;
                await Task.Delay(100);
                int originalWidth = client.Width;
                ((Button)window.FindName("FocusButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(350);
                Require(((FrameworkElement)window.FindName("Sidebar")).Visibility == Visibility.Collapsed, "Focus Mode retained the sidebar.");
                Require(((FrameworkElement)window.FindName("FocusStrip")).IsVisible, "Focus Mode has no local exit controls.");
                Require(client.Width >= originalWidth, "Focus Mode reduced the remote viewport.");
                Require(new PixelSize(DesktopWidth(), DesktopHeight()) == initialDesktop, "Focus Mode attempted offline renegotiation.");

                ((Button)window.FindName("ExitFocusButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)window.FindName("FullscreenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(350);
                Require(window.WindowStyle == WindowStyle.None, "Full-screen did not remove window chrome.");
                Require(new PixelSize(DesktopWidth(), DesktopHeight()) == initialDesktop, "Full-screen attempted offline renegotiation.");
                ((Button)window.FindName("FullscreenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var enhanced = window.AddSession(second.Profile with { Id = Guid.NewGuid(), EnhancedSession = true }, "", connect: false);
                await Task.Delay(250);
                Require(enhanced.Status == "RDP control initialized (offline)", enhanced.Status);
                var actualSize = window.AddSession(profile with { Id = Guid.NewGuid(), DisplayMode = DisplayMode.ActualSize }, "", connect: false);
                await Task.Delay(250);
                Require(actualSize.Status == "RDP control initialized (offline)", actualSize.Status);
                var actualClient = ((WindowsFormsHost)actualSize.Content).Child.Controls[0];
                Require(actualClient.Width == 1920 && actualClient.Height == 1080, "Actual Size did not preserve pixel dimensions.");

                var editor = new ConnectionEditor(profile) { Owner = window };
                editor.Show();
                await Task.Delay(100);
                var scaleBox = (ComboBox)editor.FindName("ScaleBox");
                Require(scaleBox.Items.Contains(new ResolutionScale(1, 2)), "The half-resolution preset is missing.");
                Require(scaleBox.Items.Contains(new ResolutionScale(2, 3)), "The two-thirds-resolution preset is missing.");
                scaleBox.SelectedItem = new ResolutionScale(2, 3);
                Require((ResolutionScale)scaleBox.SelectedItem == new ResolutionScale(2, 3), "Cannot select two-thirds resolution.");
                editor.Close();
                var credentials = new CredentialsDialog(profile) { Owner = window };
                credentials.Show();
                await Task.Delay(100);
                credentials.Close();

                window.Close();
                Require(client.IsDisposed && actualClient.IsDisposed, "Closing the window left native controls alive.");
                Console.WriteLine("PASS: grouped tree; right-click Edit; existing-connection welcome; Windows Credential Manager round-trip; direct reconnect; sidebar overlay without viewport resize; RDP/Hyper-V activation; scaling; Focus Mode/full-screen; native disposal. No network connection attempted.");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                window?.Close();
                app.Shutdown(1);
            }
        };
        try { return app.Run(); }
        finally
        {
            if (credentialWritten) CredentialStore.Delete(credentialTarget);
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
