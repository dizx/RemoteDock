using System.Globalization;

namespace RemoteDock.Core;

public static class RdpFileImporter
{
    public static ConnectionImport Read(string path, bool includePasswords = true, bool scaledFit = true)
    {
        if (new FileInfo(path).Length > 4_000_000) throw new InvalidDataException("The RDP file is too large.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(path))
        {
            string[] parts = line.Split(':', 3);
            if (parts.Length == 3) values[parts[0].Trim()] = parts[2];
        }
        string Get(string key, string fallback = "") => values.GetValueOrDefault(key, fallback);
        int Number(string key, int fallback) => values.TryGetValue(key, out string? text) ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;
        if (Get("remoteapplicationmode") == "1") throw new NotSupportedException("RemoteApp files are not supported; import a desktop RDP connection instead.");
        if (Get("gatewayhostname").Length > 0 && Get("gatewayusagemethod", "1") != "0")
            throw new NotSupportedException("This RDP file requires RD Gateway, which is not supported yet.");
        string address = Get("full address").Trim();
        int port = Number("server port", 3389);
        if (address.StartsWith('['))
        {
            int close = address.IndexOf(']');
            if (close < 0) throw new InvalidDataException("Invalid bracketed IPv6 address.");
            if (address.Length > close + 1)
            {
                if (address[close + 1] != ':') throw new InvalidDataException("Invalid RDP endpoint.");
                port = int.Parse(address[(close + 2)..], CultureInfo.InvariantCulture);
            }
            address = address[1..close];
        }
        else if (address.Count(c => c == ':') == 1)
        {
            int split = address.LastIndexOf(':');
            port = int.Parse(address[(split + 1)..], CultureInfo.InvariantCulture);
            address = address[..split];
        }
        var group = new ConnectionGroup { Id = ConnectionImport.StableId("rdp", "root"), Name = "RDP files", InheritPassword = false,
            Defaults = scaledFit ? new() { DisplayMode = DisplayMode.ScaledFit, Scale = new(1, 2) } : new() };
        var profile = new ConnectionProfile
        {
            Id = ConnectionImport.StableId(Path.GetFullPath(path).ToUpperInvariant(), "rdp"),
            Name = Path.GetFileNameWithoutExtension(path), Host = address, Port = port, GroupId = group.Id,
            Inherit = scaledFit ? InheritedSettings.Display : InheritedSettings.None,
            UserName = Get("username"), Domain = Get("domain"),
            DisplayMode = scaledFit || Get("smart sizing") == "1" ? DisplayMode.ScaledFit
                : Get("dynamic resolution") == "1" ? DisplayMode.MatchPanel : DisplayMode.ActualSize,
            DesktopWidth = Number("desktopwidth", 1920), DesktopHeight = Number("desktopheight", 1080),
            RemoteDpiScalePercent = Number("desktopscalefactor", 0),
            Scale = new(1, 2), RedirectClipboard = Number("redirectclipboard", 1) != 0,
            RedirectWindowsKeys = Number("keyboardhook", 0) != 0,
            AdministrativeSession = Get("administrative session") == "1" || Get("connect to console") == "1"
        };
        profile.Validate();
        var passwords = new List<ImportedPassword>();
        if (includePasswords)
        {
            if (Get("password 51").Length > 0)
                passwords.Add(new(profile.Id, false, windowsProtected: Convert.FromHexString(Get("password 51"))));
            else if (Get("password").Length > 0) passwords.Add(new(profile.Id, false, Get("password")));
            else passwords.Add(new(profile.Id, false));
        }
        var warnings = new List<string>();
        if (Number("use multimon", 0) != 0) warnings.Add($"{profile.Name}: multi-monitor layout is shown in a single RemoteDock panel.");
        if (Get("drivestoredirect").Length > 0 || Get("redirectdrives") == "1") warnings.Add($"{profile.Name}: drive sharing remains disabled.");
        return new([group], [profile], passwords, warnings, 0);
    }
}
