using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace RemoteDock.Core;

public sealed class ConnectionStore(string path)
{
    private bool _loaded;
    private string? _snapshot;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public IReadOnlyList<ConnectionProfile> Load() => LoadCatalog().Connections;

    public ConnectionCatalog LoadCatalog()
    {
        byte[]? bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (bytes is null) { _loaded = true; _snapshot = null; return new(); }
        var document = JsonSerializer.Deserialize<ConnectionCatalog>(bytes, Options)
            ?? throw new InvalidDataException("The connection file is empty.");
        if (document.Version is not (1 or 2)) throw new InvalidDataException("Unsupported connection file version.");
        document.Validate();
        _snapshot = Convert.ToHexString(SHA256.HashData(bytes));
        _loaded = true;
        return document with { Version = 2 };
    }

    public void Save(IEnumerable<ConnectionProfile> connections) => SaveCatalog(LoadCatalog() with { Connections = connections.ToArray() });

    public void SaveCatalog(ConnectionCatalog catalog)
    {
        catalog.Validate();
        if (!_loaded) _ = LoadCatalog();
        var current = File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
        if (current != _snapshot)
            throw new IOException("Connections changed in another RemoteDock window. Reopen this window before saving to avoid overwriting those changes.");
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(catalog with { Version = 2 }, Options);
            File.WriteAllText(temporary, json);
            File.Move(temporary, fullPath, overwrite: true);
            _snapshot = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath)));
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

}
