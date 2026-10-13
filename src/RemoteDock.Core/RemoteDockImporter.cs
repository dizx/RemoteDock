namespace RemoteDock.Core;

public static class RemoteDockImporter
{
    public static ConnectionImport Read(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The RemoteDock connection file was not found.", path);
        var catalog = new ConnectionStore(path).LoadCatalog();
        var groups = catalog.Groups.Select(group => group with
        {
            InheritPassword = false,
            Defaults = group.Defaults with { UserName = "", Domain = "" }
        }).ToArray();
        var connections = catalog.Connections.Select(profile => profile with
        {
            UserName = "",
            Domain = "",
            LastConnectedAt = null,
            Inherit = profile.Inherit & ~InheritedSettings.SignIn
        }).ToArray();
        return new(groups, connections, [], [], 0);
    }
}
