namespace RemoteDock.Core;

public sealed record ConnectionCatalog
{
    public int Version { get; init; } = 2;
    public ConnectionProfile[] Connections { get; init; } = [];
    public ConnectionGroup[] Groups { get; init; } = [];
    public bool AutoHideConnections { get; init; }

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
