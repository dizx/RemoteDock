using System.Text.Json.Serialization;

namespace RemoteDock.Core;

public enum ConnectionSortOrder { Alphabetical, LastConnected }

public sealed record ConnectionCatalog
{
    [JsonRequired]
    public int Version { get; init; } = 2;
    [JsonRequired]
    public ConnectionProfile[] Connections { get; init; } = [];
    public ConnectionGroup[] Groups { get; init; } = [];
    public bool AutoHideConnections { get; init; }
    public ConnectionSortOrder SortOrder { get; init; }
    public WindowPlacement? WindowPlacement { get; init; }

    public IEnumerable<ConnectionProfile> OrderedConnections(Guid? groupId) =>
        OrderConnections(Connections.Where(p => p.GroupId == groupId));

    public IEnumerable<ConnectionProfile> OrderConnections(IEnumerable<ConnectionProfile> profiles) =>
        profiles.OrderByDescending(p => SortOrder == ConnectionSortOrder.LastConnected ? p.LastConnectedAt : null)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id);

    public IEnumerable<ConnectionGroup> OrderedGroups(Guid? parentId)
    {
        var recent = new Dictionary<Guid, DateTimeOffset>();
        if (SortOrder == ConnectionSortOrder.LastConnected)
        {
            foreach (var profile in Connections.Where(p => p.LastConnectedAt.HasValue))
                foreach (var group in Ancestors(profile.GroupId))
                    if (!recent.TryGetValue(group.Id, out var previous) || profile.LastConnectedAt!.Value > previous)
                        recent[group.Id] = profile.LastConnectedAt!.Value;
        }
        return Groups.Where(g => g.ParentId == parentId)
            .OrderByDescending(g => recent.TryGetValue(g.Id, out var time) ? (DateTimeOffset?)time : null)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Id);
    }

    public ConnectionCatalog RecordConnected(Guid connectionId, DateTimeOffset connectedAt) => this with
    {
        Connections = Connections.Select(p => p.Id == connectionId && (p.LastConnectedAt is null || p.LastConnectedAt < connectedAt)
            ? p with { LastConnectedAt = connectedAt.ToUniversalTime() } : p).ToArray()
    };

    public ConnectionProfile Resolve(ConnectionProfile profile)
    {
        var resolved = profile;
        foreach (var group in Ancestors(profile.GroupId))
            resolved = group.Defaults.Apply(resolved, profile.Inherit);
        return resolved;
    }

    public ConnectionProfile GroupDefaults(Guid? groupId)
    {
        var profile = new ConnectionProfile { Host = "defaults.invalid" };
        foreach (var group in Ancestors(groupId)) profile = group.Defaults.Apply(profile);
        return profile;
    }

    public string GroupPath(Guid groupId) => string.Join(" / ", Ancestors(groupId).Select(g => g.Name));

    public IReadOnlyList<ConnectionGroup> GroupChain(Guid? groupId) => Ancestors(groupId).ToArray();

    public bool IsDescendant(Guid candidateId, Guid ancestorId) => Ancestors(candidateId).Any(g => g.Id == ancestorId);

    private IEnumerable<ConnectionGroup> Ancestors(Guid? groupId)
    {
        var chain = new Stack<ConnectionGroup>();
        var seen = new HashSet<Guid>();
        while (groupId is { } id)
        {
            if (!seen.Add(id)) throw new InvalidDataException("Groups cannot contain a parent cycle.");
            var group = Groups.FirstOrDefault(g => g.Id == id)
                ?? throw new InvalidDataException("A referenced connection group is missing.");
            chain.Push(group);
            groupId = group.ParentId;
        }
        return chain;
    }

    public void Validate()
    {
        if (!Enum.IsDefined(SortOrder)) throw new InvalidDataException("Unknown connection sort order.");
        if (Connections is null || Groups is null) throw new InvalidDataException("Connections or groups are missing.");
        if (Groups.Any(g => g is null) || Connections.Any(p => p is null))
            throw new InvalidDataException("A connection or group is null.");
        var ids = Groups.Select(g => g.Id).Concat(Connections.Select(p => p.Id)).ToArray();
        if (ids.Distinct().Count() != ids.Length) throw new InvalidDataException("Duplicate connection or group IDs.");
        foreach (var group in Groups)
        {
            if (group.Id == Guid.Empty || string.IsNullOrWhiteSpace(group.Name))
                throw new ArgumentException("Each group needs a name and ID.");
            if (group.Defaults is null) throw new InvalidDataException("Group defaults are missing.");
            group.Defaults.Validate();
            _ = Ancestors(group.Id);
        }
        foreach (var profile in Connections)
        {
            profile.Validate();
            Resolve(profile).Validate();
        }
    }
}
