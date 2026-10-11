using System.Text.Json;
using RemoteDock.Core;

namespace RemoteDock.Core.Tests;

public sealed class RemoteDpiTests
{
    [Theory]
    [InlineData(96, 100, 100)]
    [InlineData(120, 125, 140)]
    [InlineData(144, 150, 140)]
    [InlineData(168, 175, 140)]
    [InlineData(192, 200, 180)]
    [InlineData(288, 300, 180)]
    public void DefaultFollowsLocalMonitorScaling(int dpi, int desktopScale, int deviceScale)
    {
        Assert.Equal(desktopScale, DisplayPolicy.RemoteDesktopScale(new(), dpi));
        Assert.Equal(deviceScale, DisplayPolicy.RemoteDeviceScale(desktopScale));
    }

    [Fact]
    public void ExplicitRemoteScaleDoesNotChangeWithLocalDpiOrResolutionFraction()
    {
        var profile = new ConnectionProfile { RemoteDpiScalePercent = 125, Scale = new(2, 3) };
        Assert.Equal(125, DisplayPolicy.RemoteDesktopScale(profile, 192));
        Assert.Equal(125, DisplayPolicy.RemoteDesktopScale(profile with { Scale = new(1, 2) }, 144));
        Assert.Equal(new PixelSize(1000, 600), DisplayPolicy.RequestedDesktop(profile, new(1500, 900)));
    }

    [Fact]
    public void GroupDpiIsInheritedAndConnectionsCanOverrideIt()
    {
        var parent = new ConnectionGroup { Defaults = new() { RemoteDpiScalePercent = 175 } };
        var child = new ConnectionGroup { ParentId = parent.Id };
        var profile = new ConnectionProfile { Host = "example", GroupId = child.Id, Inherit = InheritedSettings.Display };
        var catalog = new ConnectionCatalog { Groups = [parent, child], Connections = [profile] };
        Assert.Equal(175, catalog.Resolve(profile).RemoteDpiScalePercent);
        Assert.Equal(125, catalog.Resolve(profile with { Inherit = InheritedSettings.None, RemoteDpiScalePercent = 125 }).RemoteDpiScalePercent);
        catalog = catalog with { Groups = [parent, child with { Defaults = new() { RemoteDpiScalePercent = 0 } }] };
        Assert.Equal(0, catalog.Resolve(profile).RemoteDpiScalePercent);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(501)]
    public void UnsupportedDpiValuesAreRejected(int value)
    {
        Assert.Throws<ArgumentException>(() => new ConnectionProfile { Host = "example", RemoteDpiScalePercent = value }.Validate());
        Assert.Throws<ArgumentException>(() => new ConnectionDefaults { RemoteDpiScalePercent = value }.Validate());
    }

    [Fact]
    public void DpiSurvivesSerializationAndOldProfilesFollowTheLocalPc()
    {
        var profile = new ConnectionProfile { Host = "example", RemoteDpiScalePercent = 150 };
        Assert.Equal(profile, JsonSerializer.Deserialize<ConnectionProfile>(JsonSerializer.Serialize(profile)));
        Assert.Equal(0, JsonSerializer.Deserialize<ConnectionProfile>("{\"Host\":\"example\"}")!.RemoteDpiScalePercent);
    }
}
