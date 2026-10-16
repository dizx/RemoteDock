using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace RemoteDock.App;

public partial class MainWindow
{
    private void UpdateFocusChrome()
    {
        FullscreenFocusPopup.IsOpen = false;
        FocusTitleHost.Content = null;
        FullscreenFocusHost.Content = null;

        bool fullscreenFocus = _focusMode && _fullScreen;
        FocusStrip.Visibility = _focusMode ? Visibility.Visible : Visibility.Collapsed;
        FullscreenFocusEdge.Visibility = fullscreenFocus ? Visibility.Visible : Visibility.Collapsed;
        FocusWindowButtons.Visibility = fullscreenFocus ? Visibility.Collapsed : Visibility.Visible;

        if (fullscreenFocus)
            FullscreenFocusHost.Content = FocusStrip;
        else
            FocusTitleHost.Content = FocusStrip;

        WindowChrome.SetWindowChrome(this, _focusMode && !_fullScreen ? new WindowChrome
        {
            CaptionHeight = 36,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
            // Keep DWM composition enabled so Windows can round the outer frame.
            GlassFrameThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            UseAeroCaptionButtons = false
        } : null);
        WindowAppearance.UpdateCornerPreference(this, _focusMode, _fullScreen);
        ScheduleFocusCaptionLayout();
    }

    private void ScheduleFocusCaptionLayout() => Dispatcher.BeginInvoke(
        new Action(UpdateFocusCaptionLayout), DispatcherPriority.Loaded);

    private void UpdateFocusCaptionLayout()
    {
        if (!_focusMode || _fullScreen || WindowState != WindowState.Maximized)
        {
            WindowLayout.Margin = new Thickness(0);
            return;
        }
        if (PresentationSource.FromVisual(this) is null) return;

        // Maximized windows can extend their invisible resize frame beyond the
        // monitor work area. Inset custom caption content by the actual overhang,
        // rather than assuming a fixed border size at every DPI and monitor.
        var margin = WindowLayout.Margin;
        var topLeft = WindowLayout.PointToScreen(new Point(-margin.Left, -margin.Top));
        var bottomRight = WindowLayout.PointToScreen(new Point(
            WindowLayout.ActualWidth + margin.Right, WindowLayout.ActualHeight + margin.Bottom));
        var workArea = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        WindowLayout.Margin = new Thickness(
            Math.Max(0, workArea.Left - topLeft.X) / dpi.DpiScaleX,
            Math.Max(0, workArea.Top - topLeft.Y) / dpi.DpiScaleY,
            Math.Max(0, bottomRight.X - workArea.Right) / dpi.DpiScaleX,
            Math.Max(0, bottomRight.Y - workArea.Bottom) / dpi.DpiScaleY);
    }

    private void FocusMinimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void FocusMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void FocusClose_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void FullscreenFocusEdge_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_focusMode || !_fullScreen) return;
        FullscreenFocusPopup.IsOpen = true;
        RevealFocusSession();
    }

    private void FullscreenFocusHost_MouseLeave(object sender, MouseEventArgs e)
    {
        FullscreenFocusPopup.IsOpen = false;
    }

    private void FocusSessions_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        FocusSessionsScroll.ScrollToHorizontalOffset(FocusSessionsScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
