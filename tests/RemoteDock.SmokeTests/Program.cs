using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private static int Main(string[] args)
    {
        var app = new RemoteDock.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string sandbox = Path.Combine(Path.GetTempPath(), "opencode", "RemoteDock.Smoke", Guid.NewGuid().ToString("N"));
        var store = new ConnectionStore(Path.Combine(sandbox, "connections.json"));
        var sessionStore = new SessionStore(Path.Combine(sandbox, "sessions.json"));
        var group = new ConnectionGroup { Name = "Lab", Defaults = new() { Domain = "LAB", Scale = new(2, 3) } };
        var savedProfile = new ConnectionProfile { Name = "Saved test connection", Host = "offline.invalid", GroupId = group.Id, Inherit = InheritedSettings.All };
        var importedProfiles = Enumerable.Range(1, 25).Select(index => savedProfile with { Id = Guid.NewGuid(), Name = $"Test imported connection {index:00}",
            LastConnectedAt = index == 25 ? DateTimeOffset.UnixEpoch : null });
        store.SaveCatalog(new() { Groups = [group], Connections = new[] { savedProfile }.Concat(importedProfiles).ToArray(), AutoHideConnections = true });
        string credentialTarget = CredentialStore.ConnectionTarget(Guid.NewGuid());
        var originalPointer = System.Windows.Forms.Cursor.Position;
        bool credentialWritten = false;
        app.InitializeComponent();
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                app.MainWindow = new MainWindow(store, sessionStore);
                await Task.Yield();
                window = (MainWindow)app.MainWindow;
                await Task.Delay(150);
                if (args.Contains("--focus-caption-only"))
                {
                    window.Topmost = true;
                    window.Activate();
                    window.WindowState = WindowState.Normal;
                    ((Button)window.FindName("FocusButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    foreach (var state in new[] { WindowState.Normal, WindowState.Maximized, WindowState.Normal, WindowState.Maximized })
                    {
                        window.WindowState = state;
                        await Task.Delay(350);
                        Require(((Image)window.FindName("FocusAppIcon")).IsVisible &&
                            ((Image)window.FindName("FocusAppIcon")).Source is not null &&
                            ((TextBlock)window.FindName("FocusAppTitle")).IsVisible &&
                            ((TextBlock)window.FindName("FocusAppTitle")).Text == "RemoteDock",
                            "Focus caption lost the application icon or title.");
                        var workArea = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(window).Handle).WorkingArea;
                        foreach (string name in new[] { "FocusMinimizeButton", "FocusMaximizeButton", "FocusCloseButton" })
                        {
                            var button = (Button)window.FindName(name);
                            var topLeft = button.PointToScreen(new Point(0, 0));
                            var bottomRight = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
                            Require(button.IsVisible && topLeft.X >= workArea.Left && topLeft.Y >= workArea.Top &&
                                bottomRight.X <= workArea.Right && bottomRight.Y <= workArea.Bottom,
                                $"Caption {name} is clipped in {state}: {topLeft}–{bottomRight}, work area {workArea}.");
                        }
                    }
                    Directory.CreateDirectory("artifacts/smoke");
                    Capture((FrameworkElement)window.FindName("FocusStrip"), "artifacts/smoke/focus-maximized-caption.png");
                    var maximize = (Button)window.FindName("FocusMaximizeButton");
                    var center = maximize.PointToScreen(new Point(maximize.ActualWidth / 2, maximize.ActualHeight / 2));
                    System.Windows.Forms.Cursor.Position = new((int)center.X, (int)center.Y);
                    await Task.Delay(100);
                    MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                    MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(250);
                    Require(window.WindowState == WindowState.Normal, "Caption restore did not respond to a real mouse click.");
                    var brand = (FrameworkElement)window.FindName("FocusBrand");
                    var dragPoint = brand.PointToScreen(new Point(brand.ActualWidth + 50, brand.ActualHeight / 2));
                    double previousLeft = window.Left;
                    double previousTop = window.Top;
                    System.Windows.Forms.Cursor.Position = new((int)dragPoint.X, (int)dragPoint.Y);
                    await Task.Delay(100);
                    MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(100);
                    System.Windows.Forms.Cursor.Position = new((int)dragPoint.X + 40, (int)dragPoint.Y + 30);
                    await Task.Delay(100);
                    MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(200);
                    Require(window.Left > previousLeft + 10 && window.Top > previousTop + 10,
                        "Dragging empty title-bar space did not move the window.");
                    if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                    {
                        Require(DwmGetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(window).Handle,
                            33, out uint cornerPreference, sizeof(uint)) == 0 && cornerPreference == 2,
                            "Windows rounded-corner preference was not applied.");
                    }
                    var origin = window.PointToScreen(new Point(0, 0));
                    var dpi = VisualTreeHelper.GetDpi(window);
                    using (var screenshot = new System.Drawing.Bitmap((int)(window.ActualWidth * dpi.DpiScaleX) + 20,
                        (int)(window.ActualHeight * dpi.DpiScaleY) + 20))
                    {
                        using var graphics = System.Drawing.Graphics.FromImage(screenshot);
                        graphics.CopyFromScreen((int)origin.X - 10, (int)origin.Y - 10, 0, 0, screenshot.Size);
                        screenshot.Save("artifacts/smoke/focus-native-window.png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Console.WriteLine("PASS: caption buttons remain completely within the monitor work area across normal/maximized transitions; real-mouse restore; caption screenshot captured.");
                    window.Close();
                    app.Shutdown(0);
                    return;
                }
                if (args.Length == 3 && args[0] == "--live-hyperv")
                {
                    await LiveHyperVChecks.Run(window, Guid.Parse(args[1]), Path.GetFullPath(args[2]));
                    window.Close();
                    app.Shutdown(0);
                    return;
                }
                if (args.Contains("--elevation-only"))
                {
                    await ElevationChecks.Run();
                    await WindowPlacementChecks.Run();
                    window.Close();
                    app.Shutdown(0);
                    return;
                }
                if (args.Contains("--session-status-only"))
                {
                    var statusSession = window.AddSession(new() { Name = "Offline status probe", Host = "offline.invalid" }, "", connect: false);
                    await Task.Delay(250);
                    await SessionStatusChecks.Run(statusSession);
                    await SessionStatusChecks.CheckProgress();
                    await SessionConnectionChecks.Run(window);
                    await SessionResizeChecks.Run(window);
                    window.Close();
                    app.Shutdown(0);
                    return;
                }
                if (args.Contains("--multi-monitor-only"))
                {
                    await MultiMonitorChecks.Run(window);
                    window.Close();
                    app.Shutdown(0);
                    return;
                }
                if (args.Contains("--hyperv-only"))
                {
                    await HyperVPickerChecks.Run();
                    await HyperVLifecycleChecks.Run();
                    await SessionStatusChecks.CheckProgress();
                    window.Close();
                    app.Shutdown(0);
                    return;
                }
                var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                image.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                Directory.CreateDirectory("artifacts/smoke");
                using (var file = File.Create("artifacts/smoke/startup.png")) encoder.Save(file);

                var sidebarPopup = (Popup)window.FindName("SidebarPopup");
                var sidebar = (FrameworkElement)window.FindName("Sidebar");
                var revealButton = (Button)window.FindName("SidebarRevealButton");
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var outside = ((FrameworkElement)window.FindName("SessionSurface")).PointToScreen(new Point(400, 100));
                    System.Windows.Forms.Cursor.Position = new((int)outside.X, (int)outside.Y);
                    await Task.Delay(100);
                    var rail = revealButton.PointToScreen(new Point(revealButton.ActualWidth / 2, revealButton.ActualHeight / 2));
                    System.Windows.Forms.Cursor.Position = new((int)rail.X, (int)rail.Y);
                    await Task.Delay(200);
                    Require(sidebarPopup.IsOpen && sidebar.IsVisible && PresentationSource.FromVisual(sidebar) is not null,
                        $"Saved auto-hide sidebar failed to open visibly on attempt {attempt + 1}: open={sidebarPopup.IsOpen}, visible={sidebar.IsVisible}.");
                    MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                    MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(200);
                    Require(sidebarPopup.IsOpen && sidebar.IsVisible, "Clicking the reveal rail after hovering closed the sidebar.");
                    var inside = sidebar.PointToScreen(new Point(100, 30));
                    System.Windows.Forms.Cursor.Position = new((int)inside.X, (int)inside.Y);
                    await Task.Delay(100);
                    Require(sidebarPopup.IsOpen, "Moving from the rail into the sidebar closed it.");
                    System.Windows.Forms.Cursor.Position = new((int)outside.X, (int)outside.Y);
                    await Task.Delay(850);
                    Require(!sidebarPopup.IsOpen, "The sidebar did not auto-hide after the pointer left.");
                }
                revealButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                var popupTree = (TreeView)window.FindName("ConnectionsList");
                var popupGroup = (TreeViewItem)popupTree.Items[0];
                var popupConnection = (TreeViewItem)popupGroup.Items[0];
                var connectionPoint = popupConnection.PointToScreen(new Point(50, 12));
                System.Windows.Forms.Cursor.Position = new((int)connectionPoint.X, (int)connectionPoint.Y);
                MouseEvent(0x0008, 0, 0, 0, UIntPtr.Zero);
                MouseEvent(0x0010, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(850);
                Require(popupTree.ContextMenu.IsOpen && sidebarPopup.IsOpen, "The sidebar disappeared while its right-click menu was open.");
                Capture(popupTree.ContextMenu, "artifacts/smoke/context-menu.png");
                Capture(sidebar, "artifacts/smoke/sidebar.png");
                popupTree.ContextMenu.IsOpen = false;
                sidebarPopup.IsOpen = false;
                var startupAutoHide = (CheckBox)window.FindName("AutoHideBox");
                startupAutoHide.IsChecked = false;
                startupAutoHide.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

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

                var sortButton = (Button)window.FindName("SortButton");
                sortButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var sortedGroup = (TreeViewItem)tree.Items[0];
                Require(((ConnectionProfile)((TreeViewItem)sortedGroup.Items[0]).Tag).Name == "Test imported connection 25",
                    "Last-connected sorting did not put the recent connection first.");
                Require(((ConnectionProfile)((TreeViewItem)tree.SelectedItem).Tag).Id == savedProfile.Id,
                    "Sorting lost the selected connection.");
                Require(new ConnectionStore(Path.Combine(sandbox, "connections.json")).LoadCatalog().SortOrder == ConnectionSortOrder.LastConnected,
                    "Sort preference was not saved.");
                sortButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(((ConnectionProfile)((TreeViewItem)((TreeViewItem)tree.Items[0]).Items[0]).Tag).Id == savedProfile.Id,
                    "Alphabetical sorting was not restored.");

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

                await SessionStatusChecks.Run(session);
                await SessionResizeChecks.Run(window);

                var host = (WindowsFormsHost)session.Content;
                var client = host.Child.Controls.OfType<System.Windows.Forms.AxHost>().Single();
                int DesktopWidth() => (int)client.GetType().GetProperty("DesktopWidth")!.GetValue(client)!;
                int DesktopHeight() => (int)client.GetType().GetProperty("DesktopHeight")!.GetValue(client)!;
                var initialDesktop = new PixelSize(DesktopWidth(), DesktopHeight());
                var extended = (MSTSCLib.IMsRdpExtendedSettings)((dynamic)client).GetOcx();
                int expectedDpi = DisplayPolicy.RemoteDesktopScale(profile, host.Child.DeviceDpi);
                Require(Convert.ToInt32(extended.get_Property("DesktopScaleFactor")) == expectedDpi,
                    "The default remote DPI did not follow the local monitor.");
                var expectedDesktop = DisplayPolicy.RequestedDesktop(profile, new(host.Child.ClientSize.Width, host.Child.ClientSize.Height));
                Require(initialDesktop == expectedDesktop, $"Initial desktop {initialDesktop} differs from adaptive policy {expectedDesktop}.");

                var previousClient = client;
                ((Button)window.FindName("ReconnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(400);
                Require(!app.Windows.OfType<CredentialsDialog>().Any(), "Reconnect opened a credential dialog.");
                Require(((ListBox)window.FindName("Tabs")).Items.Count == 1, "Reconnect replaced or duplicated the session tab.");
                client = host.Child.Controls.OfType<System.Windows.Forms.AxHost>().Single();
                Require(previousClient.IsDisposed && !ReferenceEquals(previousClient, client), "Reconnect did not recreate the native connection.");
                Require(session.Status == "RDP control initialized (offline)", session.Status);

                var autoHide = (CheckBox)window.FindName("AutoHideBox");
                session.ShowRemoteDisplay();
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

                var tabs = (ListBox)window.FindName("Tabs");
                Require(ReferenceEquals(window.AddSession(profile, "", connect: false), session),
                    "Opening the same connection created another session.");
                Require(ReferenceEquals(window.AddSession(profile with { Id = Guid.NewGuid(), Host = "OFFLINE.INVALID.",
                    UserName = "another-user", DisplayMode = DisplayMode.MatchPanel }, "", connect: false), session),
                    "A second saved entry for the same endpoint created another session.");
                Require(tabs.Items.Count == 2 && ReferenceEquals(((ListBoxItem)tabs.SelectedItem).Tag, session) &&
                    session.Visibility == Visibility.Visible && second.Visibility == Visibility.Collapsed,
                    "Opening an existing server did not select its tab.");
                Require(host.Child.Controls.Contains(client), "Opening an existing server recreated its native connection.");
                tree.ContextMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Connect")
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(tabs.Items.Count == 2 && ReferenceEquals(((ListBoxItem)tabs.SelectedItem).Tag, session),
                    "Connecting through the saved entry created a duplicate session.");
                bool duplicatePrompted = false;
                _ = window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (app.Windows.OfType<CredentialsDialog>().FirstOrDefault() is { } dialog)
                    {
                        duplicatePrompted = true;
                        dialog.Close();
                    }
                }), DispatcherPriority.Background);
                tree.ContextMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Connect as…")
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Task.Delay(100);
                Require(!duplicatePrompted && tabs.Items.Count == 2, "An existing server prompted for another connection's credentials.");
                Require(ReferenceEquals(window.AddSession(second.Profile with { Id = Guid.NewGuid(), EnhancedSession = true },
                    "", connect: false), second), "Switching Hyper-V mode opened a duplicate connection to the same VM.");
                var alternatePort = window.AddSession(profile with { Id = Guid.NewGuid(), Port = 3390 }, "", connect: false);
                Require(!ReferenceEquals(alternatePort, session) && tabs.Items.Count == 3, "Different RDP endpoints were treated as duplicates.");
                ((StackPanel)((ListBoxItem)tabs.SelectedItem).Content).Children.OfType<Button>().Single()
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                ((ListBox)window.FindName("Tabs")).SelectedIndex = 0;
                await Task.Delay(100);
                int originalWidth = client.Width;
                ((Button)window.FindName("FocusButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(350);
                Require(((FrameworkElement)window.FindName("Sidebar")).Visibility == Visibility.Collapsed, "Focus Mode retained the sidebar.");
                Require(((FrameworkElement)window.FindName("FocusStrip")).IsVisible, "Focus Mode has no local exit controls.");
                foreach (string buttonName in new[] { "FocusMinimizeButton", "FocusMaximizeButton", "FocusCloseButton" })
                {
                    var captionButton = (Button)window.FindName(buttonName);
                    Require(captionButton.IsVisible && captionButton.ActualWidth > 0 && captionButton.ActualHeight > 0,
                        $"Focus Mode has no visible {buttonName}.");
                }
                var originalWindowState = window.WindowState;
                for (int click = 0; click < 2; click++)
                {
                    var maximize = (Button)window.FindName("FocusMaximizeButton");
                    var captionPoint = maximize.PointToScreen(new Point(maximize.ActualWidth / 2, maximize.ActualHeight / 2));
                    System.Windows.Forms.Cursor.Position = new((int)captionPoint.X, (int)captionPoint.Y);
                    MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                    MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(250);
                    Require(click == 0 ? window.WindowState != originalWindowState : window.WindowState == originalWindowState,
                        "The Focus Mode caption button did not maximize/restore the window.");
                    if (window.WindowState == WindowState.Maximized)
                    {
                        var workArea = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(window).Handle).WorkingArea;
                        foreach (string name in new[] { "FocusMinimizeButton", "FocusMaximizeButton", "FocusCloseButton" })
                        {
                            var button = (Button)window.FindName(name);
                            var topLeft = button.PointToScreen(new Point(0, 0));
                            var bottomRight = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
                            Require(topLeft.X >= workArea.Left && topLeft.Y >= workArea.Top &&
                                bottomRight.X <= workArea.Right && bottomRight.Y <= workArea.Bottom,
                                $"Maximized caption {name} is clipped: {topLeft}–{bottomRight}, work area {workArea}.");
                        }
                        Capture((FrameworkElement)window.FindName("FocusStrip"), "artifacts/smoke/focus-maximized-caption.png");
                    }
                }
                Require(client.Width >= originalWidth, "Focus Mode reduced the remote viewport.");
                Require(new PixelSize(DesktopWidth(), DesktopHeight()) == initialDesktop, "Focus Mode attempted offline renegotiation.");

                var focusButtons = (StackPanel)window.FindName("FocusSessionButtons");
                Require(focusButtons.Children.OfType<Button>().Select(b => (string)b.Content)
                    .SequenceEqual(new[] { profile.Name, second.Profile.Name }), "Focus Mode did not show the open session names.");
                var firstFocusButton = (Button)focusButtons.Children[0];
                var secondFocusButton = (Button)focusButtons.Children[1];
                var switchPoint = secondFocusButton.PointToScreen(new Point(secondFocusButton.ActualWidth / 2, secondFocusButton.ActualHeight / 2));
                System.Windows.Forms.Cursor.Position = new((int)switchPoint.X, (int)switchPoint.Y);
                MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(150);
                Require(ReferenceEquals(((ListBoxItem)tabs.SelectedItem).Tag, second) && second.Visibility == Visibility.Visible &&
                    session.Visibility == Visibility.Collapsed, "Clicking a named Focus Mode button did not switch sessions.");
                Require(secondFocusButton.FontWeight == FontWeights.SemiBold && firstFocusButton.FontWeight == FontWeights.Normal,
                    "Focus Mode did not highlight only the active session.");
                Capture((FrameworkElement)window.FindName("FocusStrip"), "artifacts/smoke/focus-sessions.png");
                firstFocusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(host.Child.Controls.Contains(client) && tabs.Items.Count == 2,
                    "Focus Mode switching recreated or duplicated a connection.");

                ((Button)window.FindName("ExitFocusButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)window.FindName("FullscreenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(350);
                Require(window.WindowStyle == WindowStyle.None, "Full-screen did not remove window chrome.");
                Require(new PixelSize(DesktopWidth(), DesktopHeight()) == initialDesktop, "Full-screen attempted offline renegotiation.");
                ((Button)window.FindName("FullscreenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                // The native renderer needs an actual resize even if its outer bounds are unchanged.
                ((Button)window.FindName("FocusButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                foreach (var previousState in new[] { WindowState.Normal, WindowState.Maximized })
                {
                    window.WindowState = previousState;
                    await Task.Delay(100);
                    ((Button)window.FindName("FullscreenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(350);
                    var focusPopup = (Popup)window.FindName("FullscreenFocusPopup");
                    var focusEdge = (FrameworkElement)window.FindName("FullscreenFocusEdge");
                    var edgePoint = focusEdge.PointToScreen(new Point(focusEdge.ActualWidth / 2, 1));
                    System.Windows.Forms.Cursor.Position = new((int)edgePoint.X, (int)edgePoint.Y);
                    await Task.Delay(200);
                    Require(focusPopup.IsOpen && secondFocusButton.IsVisible, "Fullscreen top edge did not reveal the session controls.");
                    var popupButtonPoint = secondFocusButton.PointToScreen(new Point(secondFocusButton.ActualWidth / 2, secondFocusButton.ActualHeight / 2));
                    System.Windows.Forms.Cursor.Position = new((int)popupButtonPoint.X, (int)popupButtonPoint.Y);
                    MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                    MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(150);
                    Require(ReferenceEquals(((ListBoxItem)tabs.SelectedItem).Tag, second), "Fullscreen popup could not switch the native session.");
                    var belowPopup = focusEdge.PointToScreen(new Point(focusEdge.ActualWidth / 2, 150));
                    System.Windows.Forms.Cursor.Position = new((int)belowPopup.X, (int)belowPopup.Y);
                    await Task.Delay(150);
                    Require(!focusPopup.IsOpen, "Fullscreen controls did not hide after the pointer left.");
                    firstFocusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    dynamic rdp = client;
                    ((Button)window.FindName("FullscreenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(450);
                    Require(window.WindowStyle == WindowStyle.SingleBorderWindow && window.WindowState == previousState,
                        "Leaving fullscreen did not restore the previous window state.");
                    Require((bool)rdp.AdvancedSettings2.SmartSizing, "Leaving fullscreen disabled Scaled Fit.");
                    var expectedBounds = DisplayPolicy.Fit(new(DesktopWidth(), DesktopHeight()), new(host.Child.ClientSize.Width, host.Child.ClientSize.Height));
                    Require(client.Bounds == new System.Drawing.Rectangle(expectedBounds.X, expectedBounds.Y, expectedBounds.Width, expectedBounds.Height),
                        "Leaving fullscreen did not refit the native viewport.");
                    Require(host.Child.Controls.Contains(client), "Leaving fullscreen recreated the session.");
                    Require(((FrameworkElement)window.FindName("FocusStrip")).IsVisible, "Leaving fullscreen unexpectedly left Focus Mode.");
                    var finalBounds = client.Bounds;
                    var panelSize = host.Child.ClientSize;
                    int nativeResizes = 0;
                    EventHandler countResize = (_, _) => nativeResizes++;
                    client.SizeChanged += countResize;
                    session.RefreshDisplay();
                    await Task.Delay(150);
                    client.SizeChanged -= countResize;
                    Require(nativeResizes >= 2, "Refreshing unchanged bounds did not deliver a fresh native resize.");
                    Require(client.Bounds == finalBounds && host.Child.ClientSize == panelSize,
                        "Native resize recovery changed the final viewport geometry.");
                    Require(new PixelSize(DesktopWidth(), DesktopHeight()) == initialDesktop,
                        "Native resize recovery changed the offline remote resolution.");
                }
                ((Button)window.FindName("ExitFocusButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.WindowState = WindowState.Normal;

                var enhanced = window.AddSession(second.Profile with { Id = Guid.NewGuid(), VirtualMachineId = Guid.NewGuid(), EnhancedSession = true }, "", connect: false);
                await Task.Delay(250);
                Require(enhanced.Status == "RDP control initialized (offline)", enhanced.Status);
                Require(!ReferenceEquals(enhanced, second), "Different VMs on the same Hyper-V host were treated as duplicates.");
                var actualSize = window.AddSession(profile with { Id = Guid.NewGuid(), Host = "actual-size.invalid", DisplayMode = DisplayMode.ActualSize }, "", connect: false);
                await Task.Delay(250);
                Require(actualSize.Status == "RDP control initialized (offline)", actualSize.Status);
                var actualClient = ((WindowsFormsHost)actualSize.Content).Child.Controls.OfType<System.Windows.Forms.AxHost>().Single();
                actualSize.RefreshDisplay();
                await Task.Delay(100);
                Require(!(bool)((dynamic)actualClient).AdvancedSettings2.SmartSizing, "Display refresh enabled scaling in Actual Size mode.");
                Require(actualClient.Width == 1920 && actualClient.Height == 1080, "Actual Size did not preserve pixel dimensions.");

                var editor = new ConnectionEditor(profile) { Owner = window };
                editor.Show();
                await Task.Delay(100);
                var dpiBox = (ComboBox)editor.FindName("DpiBox");
                Require((string)dpiBox.SelectedItem.GetType().GetProperty("Label")!.GetValue(dpiBox.SelectedItem)! == "Same as local PC",
                    "Remote DPI does not default to the local PC in the editor.");
                var scaleBox = (ComboBox)editor.FindName("ScaleBox");
                Require(scaleBox.Items.Contains(new ResolutionScale(1, 2)), "The half-resolution preset is missing.");
                Require(scaleBox.Items.Contains(new ResolutionScale(2, 3)), "The two-thirds-resolution preset is missing.");
                scaleBox.BringIntoView();
                await Task.Delay(100);
                scaleBox.IsDropDownOpen = true;
                await Task.Delay(100);
                var fractionItem = (ComboBoxItem)scaleBox.ItemContainerGenerator.ContainerFromItem(new ResolutionScale(2, 3));
                Require(fractionItem.IsVisible, "The themed resolution dropdown did not show its options.");
                var fractionPoint = fractionItem.PointToScreen(new Point(fractionItem.ActualWidth / 2, fractionItem.ActualHeight / 2));
                System.Windows.Forms.Cursor.Position = new((int)fractionPoint.X, (int)fractionPoint.Y);
                MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(100);
                Require((ResolutionScale)scaleBox.SelectedItem == new ResolutionScale(2, 3), "Cannot select two-thirds resolution.");
                Require(!scaleBox.IsDropDownOpen, "The themed dropdown did not close after selecting a fraction.");
                var rememberPassword = (CheckBox)editor.FindName("RememberPasswordBox");
                rememberPassword.BringIntoView();
                await Task.Delay(100);
                var rememberPoint = rememberPassword.PointToScreen(new Point(8, rememberPassword.ActualHeight / 2));
                System.Windows.Forms.Cursor.Position = new((int)rememberPoint.X, (int)rememberPoint.Y);
                MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(100);
                Require(rememberPassword.IsChecked == true && ((PasswordBox)editor.FindName("PasswordInput")).IsEnabled,
                    "The themed checkbox did not enable password entry.");
                ((TextBlock)editor.FindName("Heading")).BringIntoView();
                await Task.Delay(100);
                Capture(editor, "artifacts/smoke/connection-editor.png");
                editor.Close();
                var dpiEditor = new ConnectionEditor(profile) { Owner = window };
                _ = window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var choices = (ComboBox)dpiEditor.FindName("DpiBox");
                    choices.SelectedItem = choices.Items.Cast<object>().Single(choice =>
                        (int)choice.GetType().GetProperty("Percent")!.GetValue(choice)! == 175);
                    ((Button)dpiEditor.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }), DispatcherPriority.Background);
                Require(dpiEditor.ShowDialog() == true && dpiEditor.Result?.RemoteDpiScalePercent == 175,
                    "The remote DPI dropdown did not save the selected value.");
                var dpiProfile = profile with { Id = Guid.NewGuid(), Host = "dpi.invalid", RemoteDpiScalePercent = 175 };
                var dpiSession = window.AddSession(dpiProfile, "", connect: false);
                await Task.Delay(250);
                Require(dpiSession.Status == "RDP control initialized (offline)", dpiSession.Status);
                var dpiClient = ((WindowsFormsHost)dpiSession.Content).Child.Controls.OfType<System.Windows.Forms.AxHost>().Single();
                var dpiSettings = (MSTSCLib.IMsRdpExtendedSettings)((dynamic)dpiClient).GetOcx();
                Require(Convert.ToInt32(dpiSettings.get_Property("DesktopScaleFactor")) == 175,
                    "The explicit remote DPI was not applied to the RDP control before connection.");
                Require(Convert.ToInt32(dpiSettings.get_Property("DeviceScaleFactor")) == 140,
                    "Remote device scale did not use an RDP-supported value.");
                var credentials = new CredentialsDialog(profile) { Owner = window };
                credentials.Show();
                await Task.Delay(100);
                Capture(credentials, "artifacts/smoke/credentials.png");
                credentials.Close();

                foreach (bool confirm in new[] { false, true })
                {
                    _ = window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var message = app.Windows.OfType<MessageDialog>().Single();
                        Capture(message, "artifacts/smoke/confirmation.png");
                        ((Button)message.FindName(confirm ? "ConfirmButton" : "DismissButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }), DispatcherPriority.Background);
                    var answer = MessageDialog.ShowMessage(window, "Delete this saved connection and its password?", "Delete connection", MessageBoxButton.YesNo);
                    Require(answer == (confirm ? MessageBoxResult.Yes : MessageBoxResult.No), "The themed confirmation returned the wrong choice.");
                }

                var originalTab = tabs.Items.Cast<ListBoxItem>().Single(t => ReferenceEquals(t.Tag, session));
                ((StackPanel)originalTab.Content).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var reopened = window.AddSession(savedProfile, "", connect: false);
                Require(!ReferenceEquals(reopened, session), "Closing a server tab prevented opening a fresh session.");
                await Task.Delay(200);
                Require(new ConnectionStore(Path.Combine(sandbox, "connections.json")).LoadCatalog().Connections.Single(p => p.Id == savedProfile.Id).LastConnectedAt is null,
                    "Initializing an offline session incorrectly recorded a successful connection.");
                Require(sessionStore.Load().SequenceEqual(new[] { savedProfile.Id }), "Open tabs were not saved before shutdown.");
                var savedTab = ((ListBox)window.FindName("Tabs")).Items.Cast<ListBoxItem>().Last();
                ((StackPanel)savedTab.Content).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(sessionStore.Load().Length == 0, "Explicitly closing a tab did not remove it from recovery state.");
                window.AddSession(savedProfile, "", connect: false);

                await HyperVPickerChecks.Run();
                await HyperVLifecycleChecks.Run();
                await SessionStatusChecks.CheckProgress();
                await SessionConnectionChecks.Run(window);
                await ElevationChecks.Run();
                await WindowPlacementChecks.Run();
                window.Close();
                Require(client.IsDisposed && actualClient.IsDisposed, "Closing the window left native controls alive.");
                Require(sessionStore.Load().SequenceEqual(new[] { savedProfile.Id }), "Application shutdown erased the tabs to restore.");

                var restoreCatalog = new ConnectionStore(Path.Combine(sandbox, "connections.json"));
                var catalog = restoreCatalog.LoadCatalog();
                restoreCatalog.SaveCatalog(catalog with { Connections = catalog.Connections.Select(p => p.Id == savedProfile.Id
                    ? p with { UserName = "restore-test-user", Inherit = InheritedSettings.None } : p).ToArray() });
                foreach (bool reopen in new[] { false, true })
                {
                    sessionStore.Save([savedProfile.Id, Guid.NewGuid()]);
                    window = new MainWindow(new ConnectionStore(Path.Combine(sandbox, "connections.json")), sessionStore);
                    var response = RespondToRestorePrompt(app, reopen);
                    window.Show();
                    await response;
                    await Task.Delay(100);
                    Require(((ListBox)window.FindName("Tabs")).Items.Count == 0, "Declined restore or cancelled credentials opened a connection.");
                    Require(sessionStore.Load().Length == 0, "Declined restore or cancelled credentials left stale recovery state.");
                    window.Close();
                }
                Console.WriteLine("PASS: live session snapshots; tab-close removal; shutdown preservation; restore Yes/No and credential cancellation; deleted-profile filtering; persisted alphabetical/recent sort toggle; offline sessions excluded from history; saved auto-hide startup; repeated real-mouse hover/click/reopen; delayed dismissal; right-click popup lifetime; themed dropdown selection and checkbox input; confirmation Yes/No results; grouped tree; right-click Edit; existing-connection welcome; Windows Credential Manager round-trip; direct reconnect; sidebar overlay without viewport resize; RDP/Hyper-V activation; fullscreen scaling-reset recovery in normal/maximized Focus Mode; Actual Size remains unscaled; native disposal. Only a disposable loopback endpoint was connected; no remote host or VM was contacted.");
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
            System.Windows.Forms.Cursor.Position = originalPointer;
            if (credentialWritten) CredentialStore.Delete(credentialTarget);
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }

    private static async Task RespondToRestorePrompt(Application app, bool reopen)
    {
        var prompt = await WaitForDialog<MessageDialog>(app, "Restore previous connections");
        Require(((TextBlock)prompt.FindName("MessageText")).Text.Contains("Saved test connection"), "Restore prompt omitted the previous connection.");
        ((Button)prompt.FindName(reopen ? "ConfirmButton" : "DismissButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (reopen)
        {
            var credentials = await WaitForDialog<CredentialsDialog>(app, "Connect");
            Require(credentials.UserName == "restore-test-user", "Restore did not use the saved profile's current credentials.");
            credentials.Close();
        }
    }

    private static async Task<T> WaitForDialog<T>(Application app, string title) where T : Window
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (app.Windows.OfType<T>().FirstOrDefault(w => w.Title == title && w.IsVisible) is { } dialog) return dialog;
            await Task.Delay(25);
        }
        throw new InvalidOperationException($"The {title} dialog did not open.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Capture(FrameworkElement element, string path)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out uint value, int size);

    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
