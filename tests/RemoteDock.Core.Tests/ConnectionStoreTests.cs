using System.Text.Json;
using RemoteDock.Core;

namespace RemoteDock.Core.Tests;

public sealed class ConnectionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RemoteDock.Tests", Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_directory, "connections.json");

    [Fact]
    public void MissingStoreLoadsEmpty()
    {
        Assert.Empty(new ConnectionStore(StorePath).Load());
    }

    [Fact]
    public void HyperVConnectionAndDisplayPolicyRoundTrip()
    {
        var profile = new ConnectionProfile
        {
            Name = "Build VM", Host = "hyperv.example", Kind = ConnectionKind.HyperV,
            Port = 2179, VirtualMachineId = Guid.NewGuid(), EnhancedSession = true,
            DesktopWidth = 1600, DesktopHeight = 900, ResolutionScalePercent = 75, UserName = "operator"
        };
        var store = new ConnectionStore(StorePath);
        store.Save([profile]);
        Assert.Equal(profile, Assert.Single(store.Load()));
        Assert.DoesNotContain("password", File.ReadAllText(StorePath), StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void InvalidSavePreservesExistingFile()
    {
        var store = new ConnectionStore(StorePath);
        store.Save([new() { Host = "valid.example" }]);
        string original = File.ReadAllText(StorePath);
        Assert.Throws<ArgumentException>(() => store.Save([new() { Host = "", DesktopWidth = 0 }]));
        Assert.Equal(original, File.ReadAllText(StorePath));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("{\"Version\":99,\"Connections\":[]}")]
    [InlineData("{\"Version\":1,\"Connections\":null}")]
    public void CorruptOrUnknownFilesAreNotTreatedAsAnEmptyStore(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, content);
        Assert.ThrowsAny<Exception>(() => new ConnectionStore(StorePath).Load());
        Assert.Equal(content, File.ReadAllText(StorePath));
    }

    [Fact]
    public void DuplicateIdsAreRejected()
    {
        var profile = new ConnectionProfile { Host = "example" };
        Assert.Throws<InvalidDataException>(() => new ConnectionStore(StorePath).Save([profile, profile]));
    }

    [Fact]
    public void HyperVRequiresANonEmptyVmId()
    {
        var profile = new ConnectionProfile { Host = "hyperv", Kind = ConnectionKind.HyperV };
        Assert.Throws<ArgumentException>(profile.Validate);
        Assert.Throws<ArgumentException>((profile with { VirtualMachineId = Guid.Empty }).Validate);
        (profile with { VirtualMachineId = Guid.NewGuid() }).Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    [InlineData(101)]
    public void InvalidResolutionScaleIsRejected(int percent)
    {
        Assert.Throws<ArgumentException>(new ConnectionProfile { Host = "example", ResolutionScalePercent = percent }.Validate);
    }

    [Fact]
    public void ProfilesSavedBeforeAdaptiveScalingDefaultToFiftyPercent()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, "{\"Version\":1,\"Connections\":[{\"Host\":\"example\",\"DisplayMode\":\"ScaledFit\"}]}");
        Assert.Equal(50, Assert.Single(new ConnectionStore(StorePath).Load()).ResolutionScalePercent);
    }

    [Fact]
    public void FractionalScaleRoundTripsWithoutRoundingToAnIntegerPercentage()
    {
        var store = new ConnectionStore(StorePath);
        store.Save([new() { Host = "example", Scale = new(2, 3) }]);
        var profile = Assert.Single(store.Load());
        Assert.Equal(new ResolutionScale(2, 3), profile.EffectiveScale);
        Assert.Equal(new PixelSize(2560, 1440), DisplayPolicy.RequestedDesktop(profile, new(3840, 2160)));
    }

    [Fact]
    public void LegacyPercentageRemainsUsableAsAFraction()
    {
        Assert.Equal(new ResolutionScale(1, 2), new ConnectionProfile().EffectiveScale);
        Assert.Equal(new ResolutionScale(3, 4), new ConnectionProfile { ResolutionScalePercent = 75 }.EffectiveScale);
    }

    [Fact]
    public void GroupsInheritanceAndAutoHideRoundTrip()
    {
        var group = new ConnectionGroup { Name = "Production", Defaults = new() { Domain = "EXAMPLE", Scale = new(2, 3) } };
        var profile = new ConnectionProfile { Host = "server", GroupId = group.Id, Inherit = InheritedSettings.All };
        var store = new ConnectionStore(StorePath);
        store.SaveCatalog(new() { Groups = [group], Connections = [profile], AutoHideConnections = true });
        var loaded = store.LoadCatalog();
        Assert.Equal(group, Assert.Single(loaded.Groups));
        Assert.Equal(profile, Assert.Single(loaded.Connections));
        Assert.Equal("EXAMPLE", loaded.Resolve(profile).Domain);
        Assert.True(loaded.AutoHideConnections);
    }

    [Fact]
    public void SortPreferenceAndConnectionHistorySurviveRestart()
    {
        var profile = new ConnectionProfile { Host = "recent.example", LastConnectedAt = DateTimeOffset.UtcNow };
        new ConnectionStore(StorePath).SaveCatalog(new() { SortOrder = ConnectionSortOrder.LastConnected, Connections = [profile] });
        var loaded = new ConnectionStore(StorePath).LoadCatalog();
        Assert.Equal(ConnectionSortOrder.LastConnected, loaded.SortOrder);
        Assert.Equal(profile, Assert.Single(loaded.Connections));
    }

    [Fact]
    public void VersionOneProfilesMigrateWithoutInventingInheritance()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, "{\"Version\":1,\"Connections\":[{\"Host\":\"server\",\"UserName\":\"local-user\"}]}");
        var catalog = new ConnectionStore(StorePath).LoadCatalog();
        Assert.Equal(2, catalog.Version);
        Assert.Equal(ConnectionSortOrder.Alphabetical, catalog.SortOrder);
        Assert.Null(catalog.Connections[0].LastConnectedAt);
        Assert.Empty(catalog.Groups);
        Assert.Equal(InheritedSettings.None, Assert.Single(catalog.Connections).Inherit);
        Assert.Equal("local-user", catalog.Resolve(catalog.Connections[0]).UserName);
    }

    [Fact]
    public void AStaleWindowCannotOverwriteAnotherWindowsGroups()
    {
        var first = new ConnectionStore(StorePath);
        var second = new ConnectionStore(StorePath);
        _ = first.LoadCatalog();
        _ = second.LoadCatalog();
        first.SaveCatalog(new() { Groups = [new() { Name = "Other window's group" }] });
        Assert.Throws<IOException>(() => second.SaveCatalog(new()));
        Assert.Equal("Other window's group", Assert.Single(new ConnectionStore(StorePath).LoadCatalog().Groups).Name);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 5)]
    [InlineData(2, 1)]
    public void InvalidFractionIsRejected(int numerator, int denominator)
    {
        Assert.Throws<ArgumentException>(new ConnectionProfile { Host = "example", Scale = new(numerator, denominator) }.Validate);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
