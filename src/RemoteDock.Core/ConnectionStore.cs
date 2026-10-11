using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDock.Core;

public sealed class ConnectionStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public IReadOnlyList<ConnectionProfile> Load() => LoadCatalog().Connections;

    public ConnectionCatalog LoadCatalog()
    {
        if (!File.Exists(path)) return new();
        var document = JsonSerializer.Deserialize<ConnectionCatalog>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("The connection file is empty.");
        if (document.Version is not (1 or 2)) throw new InvalidDataException("Unsupported connection file version.");
        document.Validate();
        return document with { Version = 2 };
    }

    public void Save(IEnumerable<ConnectionProfile> connections) => SaveCatalog(LoadCatalog() with { Connections = connections.ToArray() });

    public void SaveCatalog(ConnectionCatalog catalog)
    {
        catalog.Validate();
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(catalog with { Version = 2 }, Options));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

}
