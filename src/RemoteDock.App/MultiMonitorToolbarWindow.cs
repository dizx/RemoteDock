using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RemoteDock.App;

// An owned native window can cover the ActiveX surface without WPF airspace
// clipping. Only the primary workspace monitor gets controls; the rest are pure RDP.
internal sealed class MultiMonitorToolbarWindow : Window
{
    private readonly FrameworkElement _strip;
    private readonly Rectangle _monitor;
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Border _border;
    private bool _closed;

    internal MultiMonitorToolbarWindow(Window owner, FrameworkElement strip, Rectangle monitor)
    {
        Owner = owner;
        Style = null;
        Title = "RemoteDock · Monitor controls";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _strip = strip;
        _monitor = monitor;
        _border = new Border { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(26, 35, 46)),
            Child = strip, ClipToBounds = true };
        Content = _border;
        MouseEnter += ToolbarMouseEnter;
        MouseLeave += ToolbarMouseLeave;
        _hideTimer.Tick += HideToolbar;
        SourceInitialized += ToolbarSourceInitialized;
    }

    internal void Expand()
    {
        if (_closed) return;
        _strip.Visibility = Visibility.Visible;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        Position(this, new(_monitor.Left, _monitor.Top, _monitor.Width, (int)Math.Ceiling(36 * scale)));
        if (!_closed) _hideTimer.Start();
    }

    private void Collapse()
    {
        if (_closed) return;
        _strip.Visibility = Visibility.Hidden;
        Position(this, new(_monitor.Left, _monitor.Top, _monitor.Width, 3));
    }

    private void ToolbarMouseEnter(object sender, MouseEventArgs e)
    {
        _hideTimer.Stop();
        Expand();
    }

    private void ToolbarMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_closed) _hideTimer.Start();
    }

    private void HideToolbar(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        if (!_closed && !IsMouseOver) Collapse();
    }

    private void ToolbarSourceInitialized(object? sender, EventArgs e) => Collapse();

    protected override void OnClosed(EventArgs e)
    {
        // Set the guard before detaching the content: reparenting FocusStrip can
        // generate a late MouseLeave, and a timer callback may already be queued.
        _closed = true;
        _hideTimer.Stop();
        _hideTimer.Tick -= HideToolbar;
        MouseEnter -= ToolbarMouseEnter;
        MouseLeave -= ToolbarMouseLeave;
        SourceInitialized -= ToolbarSourceInitialized;
        _border.Child = null;
        Content = null;
        base.OnClosed(e);
    }

    internal static void Position(Window window, Rectangle bounds)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!SetWindowPos(handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            0x0004 | 0x0010)) // SWP_NOZORDER | SWP_NOACTIVATE; coordinates are physical pixels.
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
