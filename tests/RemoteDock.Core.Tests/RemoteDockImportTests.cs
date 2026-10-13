using RemoteDock.Core;

namespace RemoteDock.Core.Tests;

public sealed class RemoteDockImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "opencode", "RemoteDock.ImportTests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "connections.json");

    [Fact]
    public void NativeImportKeepsHierarchyAndEndpointsWithoutPersonalInformation()
    {
        var parent = new ConnectionGroup
        {
            Name = "Lab", Defaults = new() { UserName = "group-user", Domain = "PRIVATE", Port = 3390,
                DisplayMode = DisplayMode.ActualSize, Scale = new(2, 3), RemoteDpiScalePercent = 150,
                DesktopWidth = 1600, DesktopHeight = 900, RedirectClipboard = false, RedirectWindowsKeys = true }
        };
        var child = new ConnectionGroup { Name = "Desktops", ParentId = parent.Id };
        var rdp = new ConnectionProfile
        {
            Name = "Desktop", Host = "desktop.example", GroupId = child.Id, Inherit = InheritedSettings.All,
            UserName = "private-user", Domain = "PRIVATE", LastConnectedAt = DateTimeOffset.UtcNow, AdministrativeSession = true
        };
        var vm = new ConnectionProfile
        {
            Name = "VM", Host = "hyperv.example", Kind = ConnectionKind.HyperV, Port = 2179,
            VirtualMachineId = Guid.NewGuid(), VirtualMachineName = "Build VM", EnhancedSession = true,
            UserName = "legacy-user", Domain = "PRIVATE", LastConnectedAt = DateTimeOffset.UtcNow,
            DisplayMode = DisplayMode.MatchPanel, Scale = new(3, 4)
        };
        var source = new ConnectionCatalog
        {
            Groups = [parent, child], Connections = [rdp, vm], AutoHideConnections = true,
            SortOrder = ConnectionSortOrder.LastConnected, WindowPlacement = new(100, 100, 1200, 800)
        };
        new ConnectionStore(FilePath).SaveCatalog(source);
        string original = File.ReadAllText(FilePath);

        var imported = RemoteDockImporter.Read(FilePath);
        var catalog = imported.MergeInto(new());

        Assert.Empty(imported.Passwords);
        Assert.Equal(0, imported.SkippedCount);
        Assert.Equal(parent with { InheritPassword = false, Defaults = parent.Defaults with { UserName = "", Domain = "" } }, catalog.Groups[0]);
        Assert.Equal(child with { InheritPassword = false, Defaults = child.Defaults with { UserName = "", Domain = "" } }, catalog.Groups[1]);
        Assert.Equal(rdp with { UserName = "", Domain = "", LastConnectedAt = null, Inherit = InheritedSettings.All & ~InheritedSettings.SignIn }, catalog.Connections[0]);
        Assert.Equal(vm with { UserName = "", Domain = "", LastConnectedAt = null }, catalog.Connections[1]);
        var resolved = catalog.Resolve(catalog.Connections[0]);
        Assert.Equal("", resolved.UserName);
        Assert.Equal("", resolved.Domain);
        Assert.Equal(3390, resolved.Port);
        Assert.Equal(DisplayMode.ActualSize, resolved.DisplayMode);
        Assert.Equal(new ResolutionScale(2, 3), resolved.EffectiveScale);
        Assert.Equal(150, resolved.RemoteDpiScalePercent);
        Assert.False(catalog.AutoHideConnections);
        Assert.Equal(ConnectionSortOrder.Alphabetical, catalog.SortOrder);
        Assert.Null(catalog.WindowPlacement);
        Assert.Equal(original, File.ReadAllText(FilePath));
    }

    [Fact]
    public void ReimportFromAnotherPathUpdatesIdsAndKeepsLocalHistoryAndPreferences()
    {
        var group = new ConnectionGroup { Name = "Lab" };
        var profile = new ConnectionProfile { Host = "old.example", GroupId = group.Id };
        var unrelated = new ConnectionProfile { Host = "other.example" };
        var placement = new WindowPlacement(200, 200, 1000, 700);
        var store = new ConnectionStore(FilePath);
        store.SaveCatalog(new() { Groups = [group], Connections = [profile] });
        var first = RemoteDockImporter.Read(FilePath).MergeInto(new()
        {
            Connections = [unrelated], AutoHideConnections = true,
            SortOrder = ConnectionSortOrder.LastConnected, WindowPlacement = placement
        }).RecordConnected(profile.Id, DateTimeOffset.UnixEpoch);
        store.SaveCatalog(new() { Groups = [group with { Name = "Renamed" }], Connections = [profile with { Host = "new.example" }] });
        string copiedPath = Path.Combine(_directory, "copied.json");
        File.Copy(FilePath, copiedPath);

        var second = RemoteDockImporter.Read(copiedPath).MergeInto(first);

        Assert.Equal("Renamed", Assert.Single(second.Groups).Name);
        Assert.Equal(2, second.Connections.Length);
        Assert.Contains(unrelated, second.Connections);
        var updated = Assert.Single(second.Connections, p => p.Id == profile.Id);
        Assert.Equal("new.example", updated.Host);
        Assert.Equal(DateTimeOffset.UnixEpoch, updated.LastConnectedAt);
        Assert.True(second.AutoHideConnections);
        Assert.Equal(ConnectionSortOrder.LastConnected, second.SortOrder);
        Assert.Equal(placement, second.WindowPlacement);
    }

    [Fact]
    public void LegacyVersionOneFilesAreSupportedWithoutCredentials()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """
            {"Version":1,"Connections":[{"Id":"310b7670-1b34-43bb-8a1f-7c67407c7354","Name":"Legacy","Host":"legacy.example","UserName":"private-user","Domain":"PRIVATE","Password":"ignored-secret"}]}
            """);
        var imported = RemoteDockImporter.Read(FilePath);
        var profile = Assert.Single(imported.Connections);
        Assert.Equal("legacy.example", profile.Host);
        Assert.Equal("", profile.UserName);
        Assert.Equal("", profile.Domain);
        Assert.Empty(imported.Passwords);
        Assert.Empty(imported.Groups);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("{\"Version\":99,\"Connections\":[]}")]
    [InlineData("{\"Version\":2,\"Connections\":null}")]
    [InlineData("{\"Version\":2,\"Connections\":[{\"Host\":\"server.example\",\"GroupId\":\"310b7670-1b34-43bb-8a1f-7c67407c7354\"}]}")]
    public void InvalidCatalogsAreRejectedWithoutChangingTheSource(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, json);
        Assert.ThrowsAny<Exception>(() => RemoteDockImporter.Read(FilePath));
        Assert.Equal(json, File.ReadAllText(FilePath));
    }

    [Fact]
    public void MissingFileIsNotTreatedAsAnEmptyImport()
    {
        Assert.Throws<FileNotFoundException>(() => RemoteDockImporter.Read(FilePath));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
