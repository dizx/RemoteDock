using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using RemoteDock.App;
using RemoteDock.Core;

namespace RemoteDock.SmokeTests;

internal static class SessionResizeChecks
{
    internal static async Task Run(MainWindow window)
    {
        await CheckRestoredSessionSizes(window);
        await CheckFocusReconnectSize(window);
        await CheckPreLoginResize(window);
        await CheckDisplayTransition(window);
        var session = window.AddSession(new() { Name = "Resize probe", Host = "resize.invalid" }, "", connect: false);
        var requests = new List<PixelSize>();
        session.UpdateDisplaySettings = (size, _) => requests.Add(size);
        SessionView? second = null;
        try
        {
            await Task.Delay(150);
            var host = (WindowsFormsHost)session.Content;
            var panel = host.Child;
            // Simulate login, while recording outbound display requests instead of contacting a server.
            typeof(SessionView).GetField("_loggedIn", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
            session.ShowRemoteDisplay();
            await Task.Delay(450);
            requests.Clear();
            var originalSize = panel.Size;
            panel.Size = new(240, 240);
            await Task.Delay(450);
            Require(requests.Count == 0, $"A transient native viewport sent a tiny desktop to the server: {string.Join(", ", requests)}; native {panel.Size}; host {host.ActualWidth}x{host.ActualHeight}; session {session.ActualWidth}x{session.ActualHeight}; surface {((FrameworkElement)session.Parent).ActualWidth}x{((FrameworkElement)session.Parent).ActualHeight}");
            panel.Size = originalSize;
            await Task.Delay(450);

            second = window.AddSession(new() { Name = "Other resize probe", Host = "other-resize.invalid" }, "", connect: false);
            await Task.Delay(150);
            var tabs = (ListBox)window.FindName("Tabs");
            var tab = tabs.Items.Cast<ListBoxItem>().Single(t => ReferenceEquals(t.Tag, session));
            var otherTab = tabs.Items.Cast<ListBoxItem>().Single(t => ReferenceEquals(t.Tag, second));
            requests.Clear();
            for (int i = 0; i < 5; i++)
            {
                tabs.SelectedItem = tab;
                await Task.Delay(60);
                tabs.SelectedItem = otherTab;
                await Task.Delay(60);
            }
            tabs.SelectedItem = tab;
            await Task.Delay(450);
            Require(requests.Count == 0, "Switching tabs renegotiated an unchanged remote desktop.");

            double previousWidth = window.Width;
            window.Width = previousWidth + 160;
            await Task.Delay(650);
            var expected = DisplayPolicy.RequestedDesktop(session.Profile, new(panel.ClientSize.Width, panel.ClientSize.Height));
            Require(requests.Count == 1 && requests[0] == expected,
                $"A real window resize did not send exactly one correctly scaled desktop. Expected {expected}; received {string.Join(", ", requests)}.");
            window.Width = previousWidth;
            await Task.Delay(450);
            Console.WriteLine("PASS: transient native sizes are ignored; switching tabs preserves remote resolution; real window resizing still updates it.");
        }
        finally
        {
            if (second is not null) await window.RequestCloseSessionAsync(second);
            await window.RequestCloseSessionAsync(session);
        }
    }

    private static async Task CheckDisplayTransition(MainWindow window)
    {
        var session = window.AddSession(new() { Name = "Transition cover", Host = "transition.invalid", DisplayMode = DisplayMode.MatchPanel }, "", connect: false);
        double originalWidth = window.Width;
        try
        {
            await Task.Delay(200);
            var panel = ((WindowsFormsHost)session.Content).Child;
            var client = panel.Controls.OfType<System.Windows.Forms.AxHost>().Single();
            var cover = panel.Controls.OfType<System.Windows.Forms.Panel>().Single();
            typeof(SessionView).GetField("_loggedIn", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
            session.UpdateDisplaySettings = (_, _) => { };
            session.ShowRemoteDisplay();
            session.RemoteDesktopSizeChanged(640, 480);
            await Task.Delay(200);
            session.BeginDisplayTransition();
            Require(cover.Visible && cover.BackColor == System.Drawing.Color.Black &&
                cover.Bounds == panel.ClientRectangle && panel.Controls.GetChildIndex(cover) < panel.Controls.GetChildIndex(client) && client.Visible,
                "Transition cover did not cover the native surface while leaving the RDP control active.");
            await Task.Delay(600);
            Require(session.IsDisplayTransitionCovered, "Cover disappeared before the guest confirmed its new resolution.");
            session.RemoteDesktopSizeChanged(panel.ClientSize.Width, panel.ClientSize.Height);
            await Task.Delay(350);
            Require(!session.IsDisplayTransitionCovered && !cover.Visible, "Confirmed guest resolution did not remove the cover.");

            window.Width += 100;
            await Task.Delay(100);
            Require(session.IsDisplayTransitionCovered && cover.Visible && cover.Bounds == panel.ClientRectangle,
                "Resizing the window did not automatically cover the complete native viewport.");
            for (int i = 0; i < 4; i++)
            {
                window.Width += 10;
                await Task.Delay(450);
                Require(session.IsDisplayTransitionCovered && cover.Bounds == panel.ClientRectangle,
                    "The resize cover expired or left an exposed strip during continued resizing.");
            }
            session.RemoteDesktopSizeChanged(panel.ClientSize.Width, panel.ClientSize.Height);
            await Task.Delay(350);
            Require(!session.IsDisplayTransitionCovered && !cover.Visible,
                "The resize cover did not clear after the final guest size was confirmed.");

            session.RemoteDesktopSizeChanged(640, 480);
            session.BeginDisplayTransition();
            await Task.Delay(1850);
            Require(!session.IsDisplayTransitionCovered && !cover.Visible, "Missing guest confirmation left the black cover stuck.");

            session.BeginDisplayTransition();
            session.ReportOperation("Saving state…");
            Require(!session.IsDisplayTransitionCovered && !cover.Visible, "Transition cover concealed VM status.");
        }
        finally
        {
            await window.RequestCloseSessionAsync(session);
            window.Width = originalWidth;
        }
        Require(!session.IsDisplayTransitionCovered, "Closing the session left its transition timer running.");
        Console.WriteLine("PASS: native black cover keeps RDP active, waits for confirmed resolution, times out, and yields to status/close.");
    }

    private static async Task CheckFocusReconnectSize(MainWindow window)
    {
        var session = window.AddSession(new() { Name = "Focus reconnect size", Host = "focus-size.invalid", DisplayMode = DisplayMode.MatchPanel }, "", connect: false);
        var focus = (Button)window.FindName("FocusButton");
        double originalWidth = window.Width;
        focus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            await Task.Delay(200);
            var tabs = (ListBox)window.FindName("Tabs");
            var buttons = (Panel)window.FindName("FocusSessionButtons");
            var button = buttons.Children.OfType<Button>().Single(b => ReferenceEquals(b.Tag, tabs.SelectedItem));
            button.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
            await Task.Delay(200);
            var panel = ((WindowsFormsHost)session.Content).Child;
            dynamic client = panel.Controls.OfType<System.Windows.Forms.AxHost>().Single();
            Require((int)client.DesktopWidth == panel.ClientSize.Width && (int)client.DesktopHeight == panel.ClientSize.Height,
                $"Focus reconnect requested {(int)client.DesktopWidth}x{(int)client.DesktopHeight} instead of {panel.ClientSize}.");
            session.RemoteDesktopSizeChanged(1024, 768);
            session.ShowRemoteDisplay();
            await Task.Delay(150);
            var fit = DisplayPolicy.Fit(new(1024, 768), new(panel.ClientSize.Width, panel.ClientSize.Height));
            Require((bool)client.AdvancedSettings2.SmartSizing &&
                client.Bounds == new System.Drawing.Rectangle(fit.X, fit.Y, fit.Width, fit.Height),
                "A server-selected small login screen was left unscaled in the top-left corner after Focus reconnect.");
            typeof(SessionView).GetField("_loggedIn", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
            session.UpdateDisplaySettings = (_, _) => { };
            session.RefreshDisplay();
            await Task.Delay(450);
            Require((bool)client.AdvancedSettings2.SmartSizing &&
                client.Bounds == new System.Drawing.Rectangle(fit.X, fit.Y, fit.Width, fit.Height),
                "Login completion or an accepted resize request disabled fitting before the guest changed resolution.");
            session.RemoteDesktopSizeChanged(panel.ClientSize.Width, panel.ClientSize.Height);
            await Task.Delay(100);
            Require(!(bool)client.AdvancedSettings2.SmartSizing && client.Bounds == panel.ClientRectangle,
                "Match Panel retained fallback scaling after the guest reported the full resolution.");
            // Reproduce the live guest rounding 3807 requested pixels down to 3806.
            if ((panel.ClientSize.Width & 1) == 0)
            {
                window.Width += 3 / System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
                await Task.Delay(500);
            }
            Require((panel.ClientSize.Width & 1) != 0, "Rounded-width regression requires an odd viewport width.");
            int repeatedRequests = 0;
            session.UpdateDisplaySettings = (_, _) => repeatedRequests++;
            session.RemoteDesktopSizeChanged(panel.ClientSize.Width - 1, panel.ClientSize.Height);
            await Task.Delay(500);
            Require(repeatedRequests == 0 && !(bool)client.AdvancedSettings2.SmartSizing,
                "A guest-rounded odd width triggered another remote resize or retained fallback fitting.");
        }
        finally
        {
            window.Width = originalWidth;
            focus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.RequestCloseSessionAsync(session);
        }
        Console.WriteLine("PASS: Focus reconnect fits small guest screens through login and pending resize; only confirmed full resolution restores native rendering.");
    }

    private static async Task CheckRestoredSessionSizes(MainWindow window)
    {
        var sessions = new List<SessionView>();
        double previousWidth = window.Width;
        try
        {
            window.Width = previousWidth + 90;
            for (int i = 0; i < 3; i++)
                sessions.Add(window.AddSession(new()
                {
                    Name = $"Restored VM {i}", Host = "offline.invalid", Kind = ConnectionKind.HyperV,
                    VirtualMachineId = Guid.NewGuid(), EnhancedSession = true, DisplayMode = DisplayMode.MatchPanel
                }, "", connect: false));
            await Task.Delay(350);
            var surface = (FrameworkElement)window.FindName("SessionSurface");
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(surface);
            var expected = new PixelSize((int)Math.Round(surface.ActualWidth * dpi.DpiScaleX),
                (int)Math.Round(surface.ActualHeight * dpi.DpiScaleY));
            foreach (var session in sessions)
            {
                dynamic client = ((WindowsFormsHost)session.Content).Child.Controls.OfType<System.Windows.Forms.AxHost>().Single();
                Require((int)client.DesktopWidth == expected.Width && (int)client.DesktopHeight == expected.Height,
                    $"Restored VM (visible={session.IsVisible}) started at {(int)client.DesktopWidth}x{(int)client.DesktopHeight} instead of {expected}.");
            }
        }
        finally
        {
            foreach (var session in sessions) await window.RequestCloseSessionAsync(session);
            window.Width = previousWidth;
            await Task.Delay(100);
        }
        Console.WriteLine("PASS: restored active and background VMs use the final workspace dimensions for their initial desktop.");
    }

    private static async Task CheckPreLoginResize(MainWindow window)
    {
        foreach (bool enhanced in new[] { true, false })
        {
            var session = window.AddSession(new()
            {
                Name = "Pre-login resize probe", Host = "offline.invalid", Kind = ConnectionKind.HyperV,
                VirtualMachineId = Guid.NewGuid(), EnhancedSession = enhanced, DisplayMode = DisplayMode.MatchPanel
            }, "", connect: false);
            var requests = new List<PixelSize>();
            session.UpdateDisplaySettings = (size, _) => requests.Add(size);
            double previousWidth = window.Width;
            try
            {
                await Task.Delay(150);
                // The final viewport arrives after the initial connection size was chosen.
                var initialPanel = ((WindowsFormsHost)session.Content).Child;
                dynamic initialClient = initialPanel.Controls.OfType<System.Windows.Forms.AxHost>().Single();
                if (enhanced)
                    Require((int)initialClient.DesktopWidth == initialPanel.ClientSize.Width &&
                        (int)initialClient.DesktopHeight == initialPanel.ClientSize.Height,
                        $"Enhanced session started with a fallback resolution: {(int)initialClient.DesktopWidth}x{(int)initialClient.DesktopHeight}, viewport {initialPanel.ClientSize}.");
                window.Width = previousWidth + 120;
                await Task.Delay(450);
                Require(requests.Count == 0, "An unconnected session tried to resize the remote desktop.");
                session.ShowRemoteDisplay();
                await Task.Delay(450);
                var panel = ((WindowsFormsHost)session.Content).Child;
                var client = panel.Controls.OfType<System.Windows.Forms.AxHost>().Single();
                dynamic rdp = client;
                var expected = new PixelSize(panel.ClientSize.Width, panel.ClientSize.Height);
                Require(requests.Count == 0, "The login screen attempted an unsupported remote resize before login completed.");
                var loginFit = DisplayPolicy.Fit(new((int)rdp.DesktopWidth, (int)rdp.DesktopHeight), expected);
                Require((bool)rdp.AdvancedSettings2.SmartSizing &&
                    client.Bounds == new System.Drawing.Rectangle(loginFit.X, loginFit.Y, loginFit.Width, loginFit.Height),
                    "Match Panel did not fit the login screen while remote resizing was unavailable.");
                typeof(SessionView).GetField("_loggedIn", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
                session.RefreshDisplay();
                await Task.Delay(450);
                Require(enhanced ? requests.Count == 1 && requests[0] == expected : requests.Count == 0,
                    $"Post-login {(enhanced ? "enhanced" : "basic")} console resize was incorrect: expected {(enhanced ? expected.ToString() : "no requests")}; got {string.Join(", ", requests)}.");
                if (enhanced)
                {
                    session.RemoteDesktopSizeChanged(1024, 768);
                    await Task.Delay(450);
                    Require(requests.Count == 2 && requests[1] == expected,
                        "A server-selected startup resolution prevented a request for the final viewport.");
                    session.RemoteDesktopSizeChanged(1024, 768);
                    await Task.Delay(450);
                    Require(requests.Count == 2, "Duplicate remote size notifications caused repeated resize requests.");
                    session.UpdateDisplaySettings = (size, _) =>
                    {
                        requests.Add(size);
                        throw new System.Runtime.InteropServices.COMException("Simulated unsupported remote resize", unchecked((int)0x8000FFFF));
                    };
                    window.Width += 80;
                    await Task.Delay(650);
                    var fallback = DisplayPolicy.Fit(new(1024, 768), new(panel.ClientSize.Width, panel.ClientSize.Height));
                    Require(session.Status.StartsWith("Connected · Remote resolution could not be changed") && (bool)rdp.AdvancedSettings2.SmartSizing &&
                        client.Bounds == new System.Drawing.Rectangle(fallback.X, fallback.Y, fallback.Width, fallback.Height),
                        "Rejected remote resize left the guest screen unfitted.");
                    await Task.Delay(450);
                    Require(requests.Count == 3, "Rejected remote resize caused an automatic retry loop.");
                }
            }
            finally
            {
                await window.RequestCloseSessionAsync(session);
                window.Width = previousWidth;
                await Task.Delay(100);
            }
        }
        Console.WriteLine("PASS: enhanced sessions start at viewport resolution; pre-login screens fit locally; remote resize waits for login and excludes basic consoles; rejected requests do not loop.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
