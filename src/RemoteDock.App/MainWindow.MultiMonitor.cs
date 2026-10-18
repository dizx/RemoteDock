using System;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using RemoteDock.Core;
using Forms = System.Windows.Forms;

namespace RemoteDock.App;

public partial class MainWindow
{
    private bool _multiMonitor;
    private bool _restoreMultiMonitorFocus;
    private bool _restoreMultiMonitorFullscreen;
    private WindowPlacement? _multiMonitorPlacement;
    private MultiMonitorToolbarWindow? _monitorToolbar;
    internal Func<Rectangle[]> MonitorBounds { get; set; } = () => Forms.Screen.AllScreens.Select(s => s.Bounds).ToArray();
    internal bool IsMultiMonitor => _multiMonitor;

    private void MultiMonitor_Click(object sender, RoutedEventArgs e)
    {
        try { SetMultiMonitor(!_multiMonitor); }
        catch (Exception ex)
        {
            SetMultiMonitor(false);
            ShowError(ex, "Multi-monitor display");
        }
    }

    internal void SetMultiMonitor(bool enabled)
    {
        if (_multiMonitor == enabled) return;
        if (enabled && _focusMode) return;
        if (enabled)
        {
            var monitors = MonitorBounds();
            if (monitors.Length < 2) throw new InvalidOperationException("Connect a second monitor and use Extend displays in Windows.");
            if (ActiveSession is not { SupportsMultiMonitor: true })
                throw new InvalidOperationException("Select an RDP or Hyper-V Enhanced Session to use multiple monitors.");
            var primary = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).Bounds;
            var desktop = monitors.Aggregate(Rectangle.Union);
            _restoreMultiMonitorFocus = _focusMode;
            _restoreMultiMonitorFullscreen = _fullScreen;
            if (_fullScreen) ToggleFullscreen();
            _multiMonitorPlacement = CaptureWindowPlacement();
            _preFullscreenPlacement = _multiMonitorPlacement;
            _previousState = WindowState;
            _focusMode = true;
            _fullScreen = true;
            _multiMonitor = true;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Toolbar.Visibility = Tabs.Visibility = StatusBar.Visibility = Visibility.Collapsed;
            UpdateSidebarLayout();
            UpdateFocusChrome();
            MultiMonitorToolbarWindow.Position(this, desktop);
            // The fullscreen control covers the entire virtual desktop, with no
            // toolbar row reducing the monitor geometry sent to the server.
            _monitorToolbar = new(this, FocusStrip, primary);
            _monitorToolbar.PreviewKeyDown += Window_KeyDown;
            _monitorToolbar.Closed += (_, _) =>
            {
                if (_multiMonitor && !_closing) SetMultiMonitor(false);
            };
            _monitorToolbar.Show();
            _monitorToolbar.Expand();
            SystemEvents.DisplaySettingsChanged += MultiMonitorDisplaysChanged;
        }
        else
        {
            _multiMonitor = false;
            SystemEvents.DisplaySettingsChanged -= MultiMonitorDisplaysChanged;
            var toolbar = _monitorToolbar;
            _monitorToolbar = null;
            toolbar?.Close();
            _fullScreen = false;
            _focusMode = _restoreMultiMonitorFocus;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            if (_multiMonitorPlacement is { } saved)
            {
                Left = saved.Left;
                Top = saved.Top;
                Width = saved.Width;
                Height = saved.Height;
            }
            Toolbar.Visibility = Tabs.Visibility = StatusBar.Visibility = _focusMode ? Visibility.Collapsed : Visibility.Visible;
            UpdateFocusChrome();
            if (_multiMonitorPlacement is { Maximized: true }) WindowState = WindowState.Maximized;
            UpdateSidebarLayout();
            if (_restoreMultiMonitorFullscreen) ToggleFullscreen();
        }
        foreach (var session in SessionSurface.Children.OfType<SessionView>())
            session.SetMultiMonitor(enabled, reconnect: session == ActiveSession);
        UpdateStatus();
        ActiveSession?.Activate();
    }

    private void MultiMonitorDisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!_multiMonitor || _closing) return;
        // Renegotiate only after the user opts in again; a removed display must
        // never leave the controls stranded outside the remaining desktop.
        SetMultiMonitor(false);
        var workArea = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        if (WindowState == WindowState.Normal)
            MultiMonitorToolbarWindow.Position(this, new(workArea.X, workArea.Y,
                Math.Min(workArea.Width, (int)(Width * dpi.DpiScaleX)),
                Math.Min(workArea.Height, (int)(Height * dpi.DpiScaleY))));
        SetStatusText("Monitor layout changed. Returned to a single monitor.");
    }), DispatcherPriority.Background);

    private void Session_LeaveMultiMonitorRequested(object? sender, EventArgs e)
    {
        if (sender == ActiveSession) SetMultiMonitor(false);
    }

    private void CloseMultiMonitorToolbar()
    {
        SystemEvents.DisplaySettingsChanged -= MultiMonitorDisplaysChanged;
        _multiMonitor = false;
        _monitorToolbar?.Close();
        _monitorToolbar = null;
    }
}
