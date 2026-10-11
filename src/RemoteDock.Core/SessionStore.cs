using System.Text.Json;

namespace RemoteDock.Core;

/// <summary>Remembers open connection IDs independently of connection settings and credentials.</summary>
public sealed class SessionStore(string path)
{
    public Guid[] Load()
    {
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<Guid[]>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("The saved session list is empty.");
    }

    public void Save(IEnumerable<Guid> connectionIds)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, connectionIds.ToArray());
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
