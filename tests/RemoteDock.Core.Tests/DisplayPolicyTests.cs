using RemoteDock.Core;

namespace RemoteDock.Core.Tests;

public sealed class DisplayPolicyTests
{
    [Theory]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(1280, 720, 640, 360)]
    [InlineData(2560, 1440, 1280, 720)]
    [InlineData(0, 0, 200, 200)]
    public void ScaledFitTracksPanelAtHalfResolution(int width, int height, int expectedWidth, int expectedHeight)
    {
        var profile = new ConnectionProfile { Host = "example", DesktopWidth = 1920, DesktopHeight = 1080 };
        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), DisplayPolicy.RequestedDesktop(profile, new(width, height)));
        Assert.True(DisplayPolicy.AllowsRemoteResize(profile));
    }

    [Theory]
    [InlineData(25, 960, 540)]
    [InlineData(75, 2880, 1620)]
    [InlineData(100, 3840, 2160)]
    public void ScaledFitHonorsResolutionScale(int scale, int width, int height)
    {
        var profile = new ConnectionProfile { ResolutionScalePercent = scale };
        Assert.Equal(new PixelSize(width, height), DisplayPolicy.RequestedDesktop(profile, new(3840, 2160)));
    }

    [Theory]
    [InlineData(1, 2, 3840, 2160, 1920, 1080)]
    [InlineData(2, 3, 3840, 2160, 2560, 1440)]
    [InlineData(2, 3, 2560, 1440, 1706, 960)]
    [InlineData(2, 3, 1500, 900, 1000, 600)]
    [InlineData(1, 3, 3840, 2160, 1280, 720)]
    public void FractionalScaleUsesExactRatioWhenResizing(int numerator, int denominator,
        int panelWidth, int panelHeight, int width, int height)
    {
        var profile = new ConnectionProfile { Scale = new(numerator, denominator) };
        Assert.Equal(new PixelSize(width, height), DisplayPolicy.RequestedDesktop(profile, new(panelWidth, panelHeight)));
    }

    [Fact]
    public void OddPanelDimensionsRoundDownToFit()
    {
        Assert.Equal(new PixelSize(750, 450), DisplayPolicy.RequestedDesktop(new(), new(1501, 901)));
    }

    [Fact]
    public void BasicHyperVDoesNotRenegotiateInScaledFit()
    {
        var profile = new ConnectionProfile { Kind = ConnectionKind.HyperV };
        Assert.False(DisplayPolicy.AllowsRemoteResize(profile));
        Assert.Equal(new PixelSize(1920, 1080), DisplayPolicy.RequestedDesktop(profile, new(3840, 2010)));
        Assert.Equal(new PixelSize(1920, 1005), DisplayPolicy.RequestedDesktop(profile with { EnhancedSession = true }, new(3840, 2010)));
    }

    [Fact]
    public void ScaledFitUpscales1080pTo4k()
    {
        Assert.Equal(new PixelRect(0, 0, 3840, 2160), DisplayPolicy.Fit(new(1920, 1080), new(3840, 2160)));
    }

    [Fact]
    public void UltrawidePanelLetterboxesWithoutStretching()
    {
        Assert.Equal(new PixelRect(440, 0, 2560, 1440), DisplayPolicy.Fit(new(1920, 1080), new(3440, 1440)));
    }

    [Fact]
    public void PortraitPanelPreservesAspectRatioAndStaysWithinBounds()
    {
        var bounds = DisplayPolicy.Fit(new(1920, 1080), new(1080, 1920));
        Assert.Equal(1080, bounds.Width);
        Assert.Equal(607, bounds.Height);
        Assert.True(bounds.Y > 0);
        Assert.True(bounds.Y + bounds.Height <= 1920);
    }

    [Fact]
    public void MatchPanelCanResizeOrdinaryRdpButNotBasicHyperV()
    {
        var profile = new ConnectionProfile { DisplayMode = DisplayMode.MatchPanel };
        Assert.True(DisplayPolicy.AllowsRemoteResize(profile));
        Assert.Equal(new PixelSize(2560, 1440), DisplayPolicy.RequestedDesktop(profile, new(2560, 1440)));
        var basic = profile with { Kind = ConnectionKind.HyperV };
        Assert.False(DisplayPolicy.AllowsRemoteResize(basic));
        Assert.Equal(new PixelSize(1920, 1080), DisplayPolicy.RequestedDesktop(basic, new(2560, 1440)));
        Assert.True(DisplayPolicy.AllowsRemoteResize(basic with { EnhancedSession = true }));
    }

    [Theory]
    [InlineData(0, 0, 200, 200)]
    [InlineData(16000, 9000, 8192, 8192)]
    public void MatchPanelClampsUnsupportedDimensions(int width, int height, int expectedWidth, int expectedHeight)
    {
        var profile = new ConnectionProfile { DisplayMode = DisplayMode.MatchPanel };
        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), DisplayPolicy.RequestedDesktop(profile, new(width, height)));
    }

    [Fact]
    public void ActualSizeDoesNotPermitDynamicResize()
    {
        Assert.False(DisplayPolicy.AllowsRemoteResize(new() { DisplayMode = DisplayMode.ActualSize }));
    }
}
