namespace RemoteDock.Core;

public readonly record struct PixelSize(int Width, int Height);
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public static class DisplayPolicy
{
    public static int RemoteDesktopScale(ConnectionProfile profile, int localDpi) =>
        profile.RemoteDpiScalePercent == 0 ? Math.Clamp((int)Math.Round(localDpi * 100.0 / 96), 100, 500) : profile.RemoteDpiScalePercent;

    public static int RemoteDeviceScale(int desktopScale) => desktopScale >= 200 ? 180 : desktopScale >= 125 ? 140 : 100;

    public static bool AllowsRemoteResize(ConnectionProfile profile) =>
        profile.DisplayMode is DisplayMode.ScaledFit or DisplayMode.MatchPanel &&
        (profile.Kind == ConnectionKind.Rdp || profile.EnhancedSession);

    public static PixelSize RequestedDesktop(ConnectionProfile profile, PixelSize panel)
    {
        if (!AllowsRemoteResize(profile)) return new(profile.DesktopWidth, profile.DesktopHeight);
        var scale = profile.DisplayMode == DisplayMode.ScaledFit ? profile.EffectiveScale : new ResolutionScale(1, 1);
        return new(scale.ScalePixels(panel.Width), scale.ScalePixels(panel.Height));
    }

    public static PixelRect Fit(PixelSize desktop, PixelSize panel)
    {
        if (desktop.Width <= 0 || desktop.Height <= 0 || panel.Width <= 0 || panel.Height <= 0)
            return new(0, 0, 0, 0);

        double scale = Math.Min((double)panel.Width / desktop.Width, (double)panel.Height / desktop.Height);
        int width = Math.Max(1, (int)Math.Floor(desktop.Width * scale));
        int height = Math.Max(1, (int)Math.Floor(desktop.Height * scale));
        return new((panel.Width - width) / 2, (panel.Height - height) / 2, width, height);
    }
}
