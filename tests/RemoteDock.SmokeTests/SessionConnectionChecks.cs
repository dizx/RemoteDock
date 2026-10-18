using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms.Integration;
using RemoteDock.App;
using RemoteDock.Core;
using Forms = System.Windows.Forms;

namespace RemoteDock.SmokeTests;

internal static class SessionConnectionChecks
{
    internal static async Task Run(MainWindow window)
    {
        await CheckRemoteSignOut(window);
        await CheckRestartReconnect(window);
        // Exercise the real ActiveX Connect/event path against a local disposable endpoint.
        // No remote machine, authentication, or VM state change is involved.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var session = window.AddSession(new ConnectionProfile { Name = "Local connection probe", Host = "127.0.0.1", Port = port }, "");
        try
        {
            Require(await Task.WhenAny(accept, Task.Delay(5000)) == accept, "The native RDP control did not initiate a connection.");
            using var peer = await accept;
            var panel = ((WindowsFormsHost)session.Content).Child;
            var client = panel.Controls.OfType<Forms.AxHost>().Single();
            var screen = panel.Controls.OfType<Forms.Label>().Single();
            Require(client.Visible && client.IsHandleCreated,
                "The connection was started with a hidden/deactivated ActiveX control behind the status screen.");
            Require(screen.Visible && panel.Controls.GetChildIndex(screen) < panel.Controls.GetChildIndex(client),
                "The black status screen did not cover the connecting native control.");

            int visibilityChanges = 0, nativeResizes = 0;
            client.VisibleChanged += (_, _) => visibilityChanges++;
            client.SizeChanged += (_, _) => nativeResizes++;
            for (int i = 0; i < 10; i++) session.ReportOperation($"Waiting for connection… {i}%");
            session.FinishOperation();
            Require(visibilityChanges == 0 && nativeResizes == 0,
                "Text-only progress toggled or resized the connecting ActiveX window.");

            peer.Client.LingerState = new LingerOption(true, 0);
            peer.Close();
            listener.Stop();
            for (int i = 0; i < 100 && !session.Status.StartsWith("Disconnected:"); i++) await Task.Delay(50);
            Require(session.Status.StartsWith("Disconnected:") && screen.Visible,
                $"A failed connection stayed stuck instead of displaying its error: {session.Status}");
            Console.WriteLine("PASS: real loopback RDP connection; native control stays active beneath status screen; progress does not change native visibility/size; connection failure reaches the green error screen.");
        }
        finally { await window.RequestCloseSessionAsync(session); }
    }

    private static async Task CheckRestartReconnect(MainWindow window)
    {
        var session = window.AddSession(new() { Name = "Restart probe", Host = "restart.invalid" }, "", connect: false);
        var timer = (System.Windows.Threading.DispatcherTimer)typeof(SessionView)
            .GetField("_restartTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session)!;
        try
        {
            await Task.Delay(100);
            var panel = ((WindowsFormsHost)session.Content).Child;
            var original = panel.Controls.OfType<Forms.AxHost>().Single();
            session.HandleDisconnected("Restart", 3, 26);
            Require(timer.IsEnabled && session.Status.Contains("Reconnecting"), "Restart did not schedule reconnect.");
            await Task.Delay(5300);
            Require(original.IsDisposed && panel.Controls.OfType<Forms.AxHost>().Single() != original,
                "Restart retry did not replace the native client.");
            session.ShowRemoteDisplay();
            session.HandleDisconnected("Still booting", 516, 0);
            Require(timer.IsEnabled && session.Status.Contains("Reconnecting"), "A transport connection followed by a boot-time disconnect stopped retrying.");
            var firstRetry = panel.Controls.OfType<Forms.AxHost>().Single();
            await Task.Delay(5300);
            Require(firstRetry.IsDisposed, "Restart did not make a second reconnect attempt.");
            typeof(SessionView).GetField("_loggedIn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(session, true);
            session.ShowRemoteDisplay();
            Require(!timer.IsEnabled, "Successful reconnect did not stop retries.");
            session.HandleDisconnected("Shutdown", 3, 25);
            Require(!timer.IsEnabled, "Shutdown incorrectly scheduled restart retries.");
            session.HandleDisconnected("Restart", 3, 26);
            typeof(SessionView).GetField("_restartDeadline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(session, DateTimeOffset.UtcNow.AddSeconds(-1));
            typeof(SessionView).GetMethod("RetryAfterRestart", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(session, [null, EventArgs.Empty]);
            Require(!timer.IsEnabled && session.Status.Contains("timed out") && !panel.Controls.OfType<Forms.AxHost>().Any(),
                "Reconnect deadline did not stop and dispose the pending connection.");
            session.Reconnect(session.Profile);
            await Task.Delay(100);
            session.HandleDisconnected("Restart", 3, 26);
            session.ReportOperation("Saving state");
            Require(!timer.IsEnabled, "VM management did not cancel pending retries.");
            session.FinishOperation("Saved");
            session.HandleDisconnected("Restart", 3, 26);
            Require(!timer.IsEnabled && session.Status == "Saved", "Restart overwrote a VM operation result.");
            session.ShowRemoteDisplay();
            session.HandleDisconnected("Restart", 3, 26);

            var buttons = (System.Windows.Controls.Panel)window.FindName("FocusSessionButtons");
            var tabs = (System.Windows.Controls.ListBox)window.FindName("Tabs");
            var button = buttons.Children.OfType<System.Windows.Controls.Button>().Single(b => ReferenceEquals(b.Tag, tabs.SelectedItem));
            var beforeDoubleClick = panel.Controls.OfType<Forms.AxHost>().Single();
            button.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent });
            await Task.Delay(100);
            Require(!timer.IsEnabled && beforeDoubleClick.IsDisposed && panel.Controls.OfType<Forms.AxHost>().Single() != beforeDoubleClick,
                "Focus server double-click did not reconnect its session.");
            session.HandleDisconnected("Restart", 3, 26);
        }
        finally { await window.RequestCloseSessionAsync(session); }
        Require(!timer.IsEnabled, "Closing the tab left restart retries running.");
        Console.WriteLine("PASS: restart retries replace the native client; transient failures keep retrying; success, shutdown, VM operations and tab close stop retries; Focus server double-click reconnects.");
    }

    private static async Task CheckRemoteSignOut(MainWindow window)
    {
        var tabs = (System.Windows.Controls.ListBox)window.FindName("Tabs");
        var surface = (System.Windows.Controls.Panel)window.FindName("SessionSurface");
        int initialTabs = tabs.Items.Count;
        foreach (var (reason, extended) in new[] { (2, 0), (3, 2), (3, 12) })
        {
            var session = window.AddSession(new() { Name = "Sign-out probe", Host = "signout.invalid" }, "", connect: false);
            await Task.Delay(100);
            var panel = ((WindowsFormsHost)session.Content).Child;
            session.HandleDisconnected("Misleading internal error", reason, extended);
            Require(surface.Children.Contains(session) && !panel.IsDisposed,
                "Remote sign-out disposed the ActiveX host inside its callback.");
            await Task.Delay(100);
            Require(tabs.Items.Count == initialTabs && !surface.Children.Contains(session) && panel.IsDisposed,
                $"Remote sign-out ({reason}/{extended}) did not close and dispose its session tab.");
        }

        var retained = window.AddSession(new() { Name = "Disconnect probe", Host = "signout.invalid" }, "", connect: false);
        try
        {
            await Task.Delay(100);
            retained.HandleDisconnected("Disconnected: network failure", 2308, 0);
            await Task.Delay(50);
            Require(surface.Children.Contains(retained) && retained.Status.Contains("network failure"), "A transport failure closed its error tab.");
            retained.ReportOperation("Saving state… 42%");
            retained.HandleDisconnected("Disconnected during save", 2, 12);
            await Task.Delay(50);
            Require(surface.Children.Contains(retained) && retained.Status.Contains("42%"), "A save-time disconnect closed its progress tab.");
            retained.FinishOperation("Saved · 100%");
            retained.HandleDisconnected("Late disconnect", 2, 12);
            await Task.Delay(50);
            Require(surface.Children.Contains(retained) && retained.Status.Contains("100%"), "A late disconnect closed the saved-state result.");
        }
        finally { await window.RequestCloseSessionAsync(retained); }
        Console.WriteLine("PASS: remote sign-out closes/disposes its tab after the callback; transport failures and VM operation results remain visible.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
