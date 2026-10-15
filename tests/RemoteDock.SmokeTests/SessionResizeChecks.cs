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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
