using RemoteDock.Core;

namespace RemoteDock.Core.Tests;

public sealed class ConnectionSortingTests
{
    private static readonly DateTimeOffset Earlier = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SortToggleUsesNewestFirstThenAlphabeticalWithUnusedConnectionsLast()
    {
        var alpha = new ConnectionProfile { Name = "alpha", Host = "alpha.example" };
        var beta = new ConnectionProfile { Name = "Beta", Host = "beta.example", LastConnectedAt = Earlier };
        var gamma = new ConnectionProfile { Name = "Gamma", Host = "gamma.example", LastConnectedAt = Earlier.AddHours(1) };
        var delta = new ConnectionProfile { Name = "Delta", Host = "delta.example", LastConnectedAt = Earlier };
        var catalog = new ConnectionCatalog { Connections = [gamma, delta, alpha, beta] };
        Assert.Equal(new[] { alpha.Id, beta.Id, delta.Id, gamma.Id }, catalog.OrderedConnections(null).Select(p => p.Id));
        catalog = catalog with { SortOrder = ConnectionSortOrder.LastConnected };
        Assert.Equal(new[] { gamma.Id, beta.Id, delta.Id, alpha.Id }, catalog.OrderedConnections(null).Select(p => p.Id));
    }

    [Fact]
    public void RecentFoldersIncludeNestedConnectionsAndKeepTheirHierarchy()
    {
        var old = new ConnectionGroup { Name = "A folder" };
        var recent = new ConnectionGroup { Name = "Z folder" };
        var child = new ConnectionGroup { Name = "Nested", ParentId = recent.Id };
        var empty = new ConnectionGroup { Name = "Empty" };
        var catalog = new ConnectionCatalog
        {
            SortOrder = ConnectionSortOrder.LastConnected, Groups = [old, recent, child, empty],
            Connections = [
                new() { Host = "old.example", GroupId = old.Id, LastConnectedAt = Earlier },
                new() { Host = "recent.example", GroupId = child.Id, LastConnectedAt = Earlier.AddDays(1) }
            ]
        };
        Assert.Equal(new[] { recent.Id, old.Id, empty.Id }, catalog.OrderedGroups(null).Select(g => g.Id));
        Assert.Equal(child, Assert.Single(catalog.OrderedGroups(recent.Id)));
        Assert.Empty(catalog.OrderedConnections(recent.Id));
        Assert.Equal("recent.example", Assert.Single(catalog.OrderedConnections(child.Id)).Host);
    }

    [Fact]
    public void RecordingAConnectionPreservesSettingsAndNeverMovesItsHistoryBackwards()
    {
        var profile = new ConnectionProfile { Host = "desktop.example", UserName = "operator", Scale = new(2, 3) };
        var catalog = new ConnectionCatalog { Connections = [profile] };
        var updated = catalog.RecordConnected(profile.Id, Earlier).RecordConnected(profile.Id, Earlier.AddDays(-1));
        Assert.Equal(profile with { LastConnectedAt = Earlier }, Assert.Single(updated.Connections));
        Assert.Null(profile.LastConnectedAt);
        Assert.Equal(updated.Connections, updated.RecordConnected(Guid.NewGuid(), Earlier).Connections);
    }

    [Fact]
    public void ReimportKeepsLastConnectedHistoryAndSortPreference()
    {
        var profile = new ConnectionProfile { Host = "old.example", LastConnectedAt = Earlier };
        var catalog = new ConnectionCatalog { SortOrder = ConnectionSortOrder.LastConnected, Connections = [profile] };
        var imported = new ConnectionImport([], [profile with { Host = "new.example", LastConnectedAt = null }], [], [], 0);
        var result = imported.MergeInto(catalog);
        Assert.Equal(ConnectionSortOrder.LastConnected, result.SortOrder);
        Assert.Equal(profile with { Host = "new.example" }, Assert.Single(result.Connections));
    }
}
