using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RemoteDock.App;

public static class WindowAppearance
{
    public static readonly DependencyProperty DarkCaptionProperty = DependencyProperty.RegisterAttached(
        "DarkCaption", typeof(bool), typeof(WindowAppearance), new PropertyMetadata(false, OnDarkCaptionChanged));

    public static bool GetDarkCaption(DependencyObject target) => (bool)target.GetValue(DarkCaptionProperty);
    public static void SetDarkCaption(DependencyObject target, bool value) => target.SetValue(DarkCaptionProperty, value);

    private static void OnDarkCaptionChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Window window) return;
        window.SourceInitialized -= OnSourceInitialized;
        if ((bool)e.NewValue) window.SourceInitialized += OnSourceInitialized;
        Apply(window);
    }

    private static void OnSourceInitialized(object? sender, EventArgs e) => Apply((Window)sender!);

    private static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        bool dark = GetDarkCaption(window) && !SystemParameters.HighContrast;
        SetAttribute(handle, 20, dark ? 1u : 0u);                  // DWMWA_USE_IMMERSIVE_DARK_MODE
        SetAttribute(handle, 35, dark ? 0x00000000u : 0xFFFFFFFFu); // DWMWA_CAPTION_COLOR: black
        SetAttribute(handle, 36, dark ? 0x00F7F2EDu : 0xFFFFFFFFu); // DWMWA_TEXT_COLOR: #EDF2F7 (COLORREF)
        SetAttribute(handle, 34, dark ? 0x00352A20u : 0xFFFFFFFFu); // DWMWA_BORDER_COLOR: subtle charcoal
    }

    private static void SetAttribute(IntPtr handle, int attribute, uint value)
    {
        // Unsupported caption-color attributes on older Windows versions retain native defaults.
        _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(uint));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}
