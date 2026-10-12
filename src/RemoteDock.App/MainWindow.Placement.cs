using System;
using System.Windows;
using RemoteDock.Core;

namespace RemoteDock.App;

public partial class MainWindow
{
    private WindowPlacement? _preFullscreenPlacement;
    private WindowState _lastNonMinimizedState;

    private void RestoreWindowPlacement()
    {
        if (_catalog.WindowPlacement is not { IsValid: true } saved) return;
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Width = Math.Clamp(saved.Width, MinWidth, Math.Max(MinWidth, desktop.Width));
        Height = Math.Clamp(saved.Height, MinHeight, Math.Max(MinHeight, desktop.Height));
        // Keep the caption reachable if the saved monitor has been removed.
        var caption = new Rect(saved.Left, saved.Top, Math.Min(Width, 160), 32);
        if (desktop.Contains(caption))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left;
            Top = saved.Top;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (saved.Maximized) WindowState = WindowState.Maximized;
    }

    private WindowPlacement CaptureWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            WindowState == WindowState.Maximized || WindowState == WindowState.Minimized && _lastNonMinimizedState == WindowState.Maximized);
    }

    private void SaveWindowPlacement()
    {
        if (!_storeReadable || !IsLoaded) return;
        var placement = _fullScreen ? _preFullscreenPlacement : CaptureWindowPlacement();
        if (placement is not { IsValid: true }) return;
        try
        {
            var updated = _catalog with { WindowPlacement = placement };
            _store.SaveCatalog(updated);
            _catalog = updated;
        }
        catch (Exception ex) { SetStatusText($"Window size could not be saved: {ex.Message}"); }
    }
}
