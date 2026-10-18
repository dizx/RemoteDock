using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using AxMSTSCLib;
using MSTSCLib;
using RemoteDock.App;
using RemoteDock.Core;
using Forms = System.Windows.Forms;

namespace RemoteDock.SmokeTests;

internal static class MultiMonitorChecks
{
    internal static async Task Run(MainWindow window)
    {
        window.WindowState = WindowState.Normal;
        await Task.Delay(100);
        var saved = new Rect(window.Left, window.Top, window.Width, window.Height);
        var monitors = Forms.Screen.AllScreens.Select(s => s.Bounds).ToArray();
        // Offline topology tests can also run on a one-monitor build machine.
        if (monitors.Length < 2)
            monitors = [monitors[0], new(monitors[0].Right, monitors[0].Top, 1024, 768)];
        window.MonitorBounds = () => monitors;
        var first = window.AddSession(new() { Name = "Multi RDP", Host = "multi-rdp.invalid" }, "test-secret", connect: false);
        var second = window.AddSession(new() { Name = "Multi Enhanced", Host = "multi-hyperv.invalid", Kind = ConnectionKind.HyperV,
            VirtualMachineId = Guid.NewGuid(), EnhancedSession = true }, "", connect: false);
        SessionView? basic = null;
        try
        {
            await Task.Delay(200);
            var originalFirst = Client(first);
            window.SetMultiMonitor(true);
            await Task.Delay(300);
            Require(window.IsMultiMonitor && second.UsesMultiMonitor, "Multi-monitor mode was not enabled for the selected Enhanced Session.");
            CheckNativeConfiguration(second);
            Require(Client(first) == originalFirst && !originalFirst.IsDisposed, "Enabling multi-monitor reconnected an inactive session.");
            GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds);
            var desktop = monitors.Aggregate(Rectangle.Union);
            Require(bounds.Left == desktop.Left && bounds.Top == desktop.Top && bounds.Right == desktop.Right && bounds.Bottom == desktop.Bottom,
                $"Fullscreen container did not cover all monitor coordinates: {bounds.Left},{bounds.Top}–{bounds.Right},{bounds.Bottom}; expected {desktop}.");
            var panel = ((WindowsFormsHost)second.Content).Child;
            Require(panel.ClientSize.Width == desktop.Width && panel.ClientSize.Height == desktop.Height,
                $"A caption or DPI conversion reduced the RDP viewport: {panel.ClientSize}; expected {desktop.Size}.");
            var toolbar = window.OwnedWindows.OfType<MultiMonitorToolbarWindow>().Single();
            Require(toolbar.IsVisible && ((FrameworkElement)window.FindName("FocusStrip")).IsVisible,
                "Focus controls were not available above the native multi-monitor surface.");
            Require(!((Button)window.FindName("FocusButton")).IsEnabled &&
                ((Button)window.FindName("ExitFocusButton")).Visibility == Visibility.Collapsed &&
                ((Button)window.FindName("FocusFullscreenButton")).Visibility == Visibility.Collapsed &&
                ((Button)window.FindName("FocusMultiMonitorButton")).IsVisible,
                "Multi-monitor toolbar must offer one clear exit and no Focus toggle.");
            typeof(MainWindow).GetMethod("ToggleFocus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Require(window.IsMultiMonitor, "The Focus shortcut unexpectedly exited multi-monitor fullscreen.");
            var tabs = (ListBox)window.FindName("Tabs");
            tabs.SelectedItem = tabs.Items.Cast<ListBoxItem>().Single(t => ReferenceEquals(t.Tag, first));
            await Task.Delay(250);
            CheckNativeConfiguration(first);
            Require(first.IsVisible && !second.IsVisible && originalFirst.IsDisposed, "Tab switching did not change the session on both monitors.");
            var secondClient = Client(second);
            tabs.SelectedItem = tabs.Items.Cast<ListBoxItem>().Single(t => ReferenceEquals(t.Tag, second));
            await Task.Delay(200);
            Require(Client(second) == secondClient && !secondClient.IsDisposed, "Returning to a negotiated multi-monitor session reconnected it again.");
            second.Reconnect(second.Profile);
            await Task.Delay(200);
            CheckNativeConfiguration(second);
            int resizeRequests = 0;
            second.UpdateDisplaySettings = (_, _) => resizeRequests++;
            typeof(SessionView).GetField("_loggedIn", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(second, true);
            second.ShowRemoteDisplay();
            second.RemoteDesktopSizeChanged(1024, 768);
            await Task.Delay(500);
            Require(resizeRequests == 0 && !Client(second).AdvancedSettings2.SmartSizing && Client(second).Bounds == panel.ClientRectangle,
                "Single-monitor resize/fit logic overwrote the native multi-monitor layout.");

            window.SetMultiMonitor(false);
            await Task.Delay(300);
            Require(!window.IsMultiMonitor && !second.UsesMultiMonitor && !toolbar.IsVisible && window.OwnedWindows.Count == 0,
                "Exiting multi-monitor left a companion/control window open.");
            // Native mouse-leave can arrive after closing/reparenting the toolbar.
            // It must not re-arm a timer that later tries to recreate its HWND.
            var hideTimer = (System.Windows.Threading.DispatcherTimer)typeof(MultiMonitorToolbarWindow)
                .GetField("_hideTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(toolbar)!;
            toolbar.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
            bool restartedAfterClose = hideTimer.IsEnabled;
            hideTimer.Stop();
            Require(!restartedAfterClose, "A late mouse-leave restarted the closed toolbar's hide timer (the single-monitor exit crash).");
            var strip = (FrameworkElement)window.FindName("FocusStrip");
            var restoredVisibility = strip.Visibility;
            typeof(MultiMonitorToolbarWindow).GetMethod("HideToolbar", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(toolbar, [null, EventArgs.Empty]);
            toolbar.Expand();
            await Task.Delay(600);
            Require(!hideTimer.IsEnabled && strip.Visibility == restoredVisibility,
                "A late toolbar callback restarted its timer or changed the restored Focus controls.");
            Require(!((IMsRdpClientNonScriptable5)Client(second).GetOcx()!).UseMultimon,
                "Exiting did not renegotiate a single-monitor session.");
            Require(Math.Abs(window.Left - saved.Left) < 2 && Math.Abs(window.Top - saved.Top) < 2 &&
                Math.Abs(window.Width - saved.Width) < 2 && Math.Abs(window.Height - saved.Height) < 2,
                $"Original window placement was not restored: {window.Left},{window.Top} {window.Width}x{window.Height}; expected {saved}.");
            tabs.SelectedItem = tabs.Items.Cast<ListBoxItem>().Single(t => ReferenceEquals(t.Tag, first));
            await Task.Delay(200);
            Require(!((IMsRdpClientNonScriptable5)Client(first).GetOcx()!).UseMultimon,
                "An inactive multi-monitor session did not return to single-monitor when selected.");

            window.SetMultiMonitor(true);
            await Task.Delay(200);
            typeof(MainWindow).GetMethod("MultiMonitorDisplaysChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [null, EventArgs.Empty]);
            await Task.Delay(300);
            Require(!window.IsMultiMonitor && window.OwnedWindows.Count == 0, "A display change stranded the full-desktop window.");
            window.SetMultiMonitor(true);
            await Task.Delay(200);
            window.OwnedWindows.OfType<MultiMonitorToolbarWindow>().Single().Close();
            await Task.Delay(200);
            Require(!window.IsMultiMonitor, "Closing the control window stranded the remote session in fullscreen.");
            window.SetMultiMonitor(true);
            await Task.Delay(200);
            basic = window.AddSession(new() { Name = "Basic console", Host = "multi-basic.invalid", Kind = ConnectionKind.HyperV,
                VirtualMachineId = Guid.NewGuid(), EnhancedSession = false }, "", connect: false);
            await Task.Delay(200);
            Require(!window.IsMultiMonitor && !basic.UsesMultiMonitor, "Basic Hyper-V console incorrectly negotiated multiple monitors.");
            tabs.SelectedItem = tabs.Items.Cast<ListBoxItem>().Single(t => ReferenceEquals(t.Tag, second));
            window.SetMultiMonitor(true);
            await Task.Delay(200);
            await window.RequestCloseSessionAsync(second);
            await Task.Delay(200);
            Require(window.IsMultiMonitor && first.IsVisible, "Closing the selected server did not move both monitors to the remaining session.");
            window.SetMultiMonitor(false);
            await Task.Delay(200);
            var savedClient = Client(first);
            first.ReportOperation("Saving state…");
            window.SetMultiMonitor(true);
            await Task.Delay(150);
            Require(Client(first) == savedClient && first.Status.Contains("Saving state"), "Changing monitor mode interrupted VM management.");
            first.FinishOperation("Saved");
            window.SetMultiMonitor(false);
            await Task.Delay(150);
            Require(Client(first) == savedClient && first.Status == "Saved", "Changing monitor mode reconnected a saved session.");
            window.MonitorBounds = () => [monitors[0]];
            tabs.SelectedItem = tabs.Items.Cast<ListBoxItem>().Single(t => ReferenceEquals(t.Tag, first));
            try { window.SetMultiMonitor(true); throw new Exception("One-monitor configuration was accepted."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("second monitor")) { }
            window.MonitorBounds = () => monitors;
            var focus = (Button)window.FindName("FocusButton");
            var fullscreen = (Button)window.FindName("FullscreenButton");
            focus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            fullscreen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
            CheckSingleMonitorFullscreen(window);
            window.SetMultiMonitor(true);
            await Task.Delay(150);
            Require(!window.IsMultiMonitor && !((Button)window.FindName("MultiMonitorButton")).IsEnabled &&
                ((Button)window.FindName("FocusMultiMonitorButton")).Visibility == Visibility.Collapsed &&
                window.WindowState == WindowState.Maximized &&
                ((ContentControl)window.FindName("FullscreenFocusHost")).Content == window.FindName("FocusStrip"),
                "Focus mode allowed entry into multi-monitor mode or changed the current workspace.");
            fullscreen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            focus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
            var windowed = new Rect(window.Left, window.Top, window.Width, window.Height);
            fullscreen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
            Require(window.WindowStyle == WindowStyle.None, "Fullscreen retained a native window frame.");
            CheckSingleMonitorFullscreen(window);
            fullscreen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
            Require(window.WindowState == WindowState.Normal && window.WindowStyle == WindowStyle.SingleBorderWindow &&
                Math.Abs(window.Left - windowed.Left) < 2 && Math.Abs(window.Top - windowed.Top) < 2 &&
                Math.Abs(window.Width - windowed.Width) < 2 && Math.Abs(window.Height - windowed.Height) < 2,
                "Leaving fullscreen retained screen-sized bounds instead of the original normal window.");
            window.WindowState = WindowState.Maximized;
            await Task.Delay(150);
            int focusStateChanges = 0;
            EventHandler trackFocusState = (_, _) => focusStateChanges++;
            window.StateChanged += trackFocusState;
            try
            {
                focus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(150);
                focus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(150);
                Require(window.WindowState == WindowState.Maximized && focusStateChanges == 0,
                    "Switching Focus mode restored/re-maximized the window, causing a visible transition.");
            }
            finally { window.StateChanged -= trackFocusState; }
            window.WindowState = WindowState.Normal;
            focus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(250);
            Require(((FrameworkElement)window.FindName("WindowLayout")).Margin == new Thickness(0),
                "Windowed Focus mode retained the added visible resize border.");
            focus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally
        {
            if (window.IsMultiMonitor) window.SetMultiMonitor(false);
            if (basic is not null) await window.RequestCloseSessionAsync(basic);
            await window.RequestCloseSessionAsync(second);
            await window.RequestCloseSessionAsync(first);
        }
        Console.WriteLine("PASS: native RDP and Enhanced Session multimon configuration; physical monitor bounds; primary Focus controls; both displays follow tabs; reconnect preserves layout; no single-monitor resize/zoom; exit restores placement; hotplug and basic-console fallback.");
    }

    private static void CheckSingleMonitorFullscreen(MainWindow window)
    {
        Require(!((Button)window.FindName("FocusButton")).IsEnabled &&
            ((Button)window.FindName("ExitFocusButton")).Visibility == Visibility.Collapsed &&
            ((FrameworkElement)window.FindName("Toolbar")).Visibility == Visibility.Collapsed &&
            ((FrameworkElement)window.FindName("Tabs")).Visibility == Visibility.Collapsed &&
            ((FrameworkElement)window.FindName("SidebarDockHost")).Visibility == Visibility.Collapsed &&
            ((FrameworkElement)window.FindName("StatusBar")).Visibility == Visibility.Collapsed &&
            ((ContentControl)window.FindName("FullscreenFocusHost")).Content == window.FindName("FocusStrip") &&
            (string)((Button)window.FindName("FocusFullscreenButton")).Content == "Exit full screen",
            "Fullscreen exposed Focus/Normal controls or retained normal navigation.");
        var focusState = typeof(MainWindow).GetField("_focusMode", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool before = (bool)focusState.GetValue(window)!;
        typeof(MainWindow).GetMethod("ToggleFocus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Require((bool)focusState.GetValue(window)! == before,
            "The Focus shortcut changed the windowed mode while fullscreen was active.");
        typeof(MainWindow).GetMethod("FullscreenFocusEdge_MouseEnter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [null, new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)]);
        Require(((System.Windows.Controls.Primitives.Popup)window.FindName("FullscreenFocusPopup")).IsOpen,
            "Fullscreen entered from normal mode did not reveal top-edge controls.");
        ((System.Windows.Controls.Primitives.Popup)window.FindName("FullscreenFocusPopup")).IsOpen = false;
    }

    private static AxMsRdpClient9NotSafeForScripting Client(SessionView session) =>
        ((WindowsFormsHost)session.Content).Child.Controls.OfType<AxMsRdpClient9NotSafeForScripting>().Single();

    private static void CheckNativeConfiguration(SessionView session)
    {
        var client = Client(session);
        bool multimon = ((IMsRdpClientNonScriptable5)client.GetOcx()!).UseMultimon;
        int container = client.AdvancedSettings.ContainerHandledFullScreen;
        // FullScreen is a runtime state and remains false without a remote connection.
        Require(multimon && container != 0 && !client.AdvancedSettings2.SmartSizing,
            $"Native multi-monitor flags were not configured: multimon={multimon}, container={container}, smartSizing={client.AdvancedSettings2.SmartSizing}, fullscreen={client.FullScreen}; {session.Status}");
        if (session.Profile.Kind == ConnectionKind.HyperV)
            Require(client.AdvancedSettings7.PCB == session.Profile.VirtualMachineId!.Value.ToString("D") + ";EnhancedMode=1" &&
                !client.AdvancedSettings8.NegotiateSecurityLayer,
                "Multi-monitor configuration changed the direct Hyper-V Enhanced Session transport.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);
}
