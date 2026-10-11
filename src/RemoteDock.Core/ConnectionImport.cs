using System.Security.Cryptography;
using System.Text;

namespace RemoteDock.Core;

public sealed class ImportedPassword(Guid ownerId, bool isGroup, string? password = null, byte[]? windowsProtected = null)
{
    public Guid OwnerId { get; } = ownerId;
    public bool IsGroup { get; } = isGroup;
    public string? Password { get; } = password;
    public byte[]? WindowsProtected { get; } = windowsProtected;
}

public sealed record ConnectionImport(ConnectionGroup[] Groups, ConnectionProfile[] Connections,
    IReadOnlyList<ImportedPassword> Passwords, IReadOnlyList<string> Warnings, int SkippedCount)
{
    public ConnectionCatalog MergeInto(ConnectionCatalog existing)
    {
        var groupIds = Groups.Select(g => g.Id).ToHashSet();
        var connectionIds = Connections.Select(p => p.Id).ToHashSet();
        var history = existing.Connections.ToDictionary(p => p.Id, p => p.LastConnectedAt);
        var merged = existing with
        {
            Groups = existing.Groups.Where(g => !groupIds.Contains(g.Id)).Concat(Groups).ToArray(),
            Connections = existing.Connections.Where(p => !connectionIds.Contains(p.Id))
                .Concat(Connections.Select(p => p with { LastConnectedAt = history.GetValueOrDefault(p.Id) ?? p.LastConnectedAt })).ToArray()
        };
        merged.Validate();
        return merged;
    }

    internal static Guid StableId(string source, string node) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"RemoteDock/import/{source}/{node}"))[..16]);
}

public sealed class ImportPasswordException(string message, Exception? inner = null) : Exception(message, inner);
