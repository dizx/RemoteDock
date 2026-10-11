using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace RemoteDock.Core;

public static class MRemoteNgImporter
{
    public static ConnectionImport Read(string path, bool includePasswords = true, string? masterPassword = null, bool scaledFit = true)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000
        });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("The XML file has no root element.");
        if (root.Name.LocalName != "Connections") throw new InvalidDataException("This is not an mRemoteNG connections file.");
        if (IsTrue(Attr(root, "FullFileEncryption")))
            throw new NotSupportedException("Export the mRemoteNG file without full-file encryption before importing it.");
        int iterations = ParseInt(Attr(root, "KdfIterations"), 1000);
        string key = string.IsNullOrEmpty(masterPassword) ? "mR3m" : masterPassword;
        string Decrypt(string value)
        {
            if (Attr(root, "EncryptionEngine") != "AES" || Attr(root, "BlockCipherMode") != "GCM")
                throw new NotSupportedException("Password import currently supports mRemoteNG AES/GCM files. Uncheck password import to import settings only.");
            return DecryptPassword(value, key, iterations);
        }
        if (includePasswords && !string.IsNullOrEmpty(Attr(root, "Protected"))) _ = Decrypt(Attr(root, "Protected"));

        string source = Path.GetFullPath(path).ToUpperInvariant();
        var importRoot = new ConnectionGroup { Id = ConnectionImport.StableId(source, "root"), Name = "mRemoteNG", InheritPassword = false,
            Defaults = scaledFit ? new() { DisplayMode = DisplayMode.ScaledFit, Scale = new(1, 2) } : new() };
        var groups = new List<ConnectionGroup> { importRoot };
        var connections = new List<ConnectionProfile>();
        var passwords = new List<ImportedPassword>();
        var warnings = new List<string>();
        int skipped = 0;

        void SavePassword(XElement node, Guid id, bool isGroup)
        {
            if (!includePasswords) return;
            string encrypted = Attr(node, "Password");
            passwords.Add(new(id, isGroup, encrypted.Length > 0 && !IsTrue(Attr(node, "InheritPassword")) ? Decrypt(encrypted) : null));
        }

        void Visit(XElement parent, Guid parentId, string parentPath)
        {
            int index = 0;
            foreach (var node in parent.Elements().Where(e => e.Name.LocalName == "Node"))
            {
                string nodePath = $"{parentPath}/{index++}";
                string identity = Attr(node, "Id");
                Guid id = ConnectionImport.StableId(source, identity.Length > 0 ? identity : nodePath);
                string name = Attr(node, "Name");
                if (string.IsNullOrWhiteSpace(name)) name = "Imported connection";
                bool Inherits(string property) => IsTrue(Attr(node, "Inherit" + property));
                var display = scaledFit ? DisplayMode.ScaledFit : MapDisplay(Effective(node, "Resolution"), Effective(node, "AutomaticResize"));
                var scale = new ResolutionScale(1, 2);
                bool clipboard = ParseBool(Effective(node, "RedirectClipboard"), true);
                bool keys = IsTrue(Effective(node, "RedirectKeys"));
                int port = ParseInt(Effective(node, "Port"), IsTrue(Effective(node, "UseVmId")) ? 2179 : 3389);
                if (Attr(node, "Type").Equals("Container", StringComparison.OrdinalIgnoreCase))
                {
                    groups.Add(new()
                    {
                        Id = id, Name = name, ParentId = parentId, InheritPassword = Inherits("Password"),
                        Defaults = new()
                        {
                            UserName = Inherits("Username") ? null : Attr(node, "Username"),
                            Domain = Inherits("Domain") ? null : Attr(node, "Domain"),
                            Port = Inherits("Port") || Attr(node, "Port").Length == 0 || port == 0 ? null : port,
                            DisplayMode = scaledFit || Inherits("Resolution") ? null : display,
                            Scale = scaledFit || Inherits("Resolution") ? null : scale,
                            RedirectClipboard = Inherits("RedirectClipboard") || node.Attribute("RedirectClipboard") is null ? null : clipboard,
                            RedirectWindowsKeys = Inherits("RedirectKeys") ? null : keys
                        }
                    });
                    SavePassword(node, id, true);
                    Visit(node, id, nodePath);
                    continue;
                }
                if (!Effective(node, "Protocol").Equals("RDP", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                if (Effective(node, "RDGatewayHostname").Length > 0 && Effective(node, "RDGatewayUsageMethod") is not ("" or "Never" or "0"))
                {
                    skipped++;
                    warnings.Add($"{name}: skipped because RD Gateway is not supported yet.");
                    continue;
                }
                var inherit = InheritedSettings.None;
                if (Inherits("Username")) inherit |= InheritedSettings.UserName;
                if (Inherits("Domain")) inherit |= InheritedSettings.Domain;
                if (Inherits("Password")) inherit |= InheritedSettings.Password;
                if (Inherits("Port")) inherit |= InheritedSettings.Port;
                if (scaledFit || Inherits("Resolution")) inherit |= InheritedSettings.Display;
                if (Inherits("RedirectClipboard")) inherit |= InheritedSettings.Clipboard;
                if (Inherits("RedirectKeys")) inherit |= InheritedSettings.WindowsKeys;
                bool hyperV = IsTrue(Effective(node, "UseVmId"));
                Guid? vmId = Guid.TryParse(Effective(node, "VmId"), out var parsedVm) ? parsedVm : null;
                var profile = new ConnectionProfile
                {
                    Id = id, Name = name, GroupId = parentId, Inherit = inherit,
                    Host = Effective(node, "Hostname"), Port = port,
                    Kind = hyperV ? ConnectionKind.HyperV : ConnectionKind.Rdp, VirtualMachineId = vmId,
                    EnhancedSession = IsTrue(Effective(node, "UseEnhancedMode")),
                    UserName = Effective(node, "Username"), Domain = Effective(node, "Domain"),
                    DisplayMode = display, Scale = scale, RedirectClipboard = clipboard, RedirectWindowsKeys = keys,
                    AdministrativeSession = IsTrue(Effective(node, "ConnectToConsole")) || IsTrue(Effective(node, "UseConsoleSession"))
                };
                profile.Validate();
                connections.Add(profile);
                SavePassword(node, id, false);
            }
        }

        Visit(root, importRoot.Id, "");
        if (root.Descendants().Any(n => Attr(n, "RedirectDiskDrives") is "true" or "True"))
            warnings.Add("Drive redirection settings are not imported; RemoteDock keeps drive sharing disabled.");
        var result = new ConnectionImport(groups.ToArray(), connections.ToArray(), passwords, warnings, skipped);
        _ = result.MergeInto(new());
        return result;
    }

    private static string Attr(XElement element, string name) => (string?)element.Attribute(name) ?? "";
    private static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
    private static bool ParseBool(string value, bool fallback) => value.Length == 0 ? fallback : IsTrue(value);
    private static int ParseInt(string value, int fallback) => value.Length == 0 ? fallback : int.Parse(value, CultureInfo.InvariantCulture);
    private static string Effective(XElement node, string property)
    {
        XElement? current = node;
        while (current is not null && IsTrue(Attr(current, "Inherit" + property))) current = current.Parent;
        return current is null ? "" : Attr(current, property);
    }
    private static DisplayMode MapDisplay(string resolution, string automaticResize) => resolution switch
    {
        "SmartSize" => DisplayMode.ScaledFit,
        "FitToWindow" when !IsTrue(automaticResize) => DisplayMode.ActualSize,
        _ => DisplayMode.MatchPanel
    };

    internal static string DecryptPassword(string encoded, string password, int iterations)
    {
        if (iterations is < 1 or > 5_000_000) throw new InvalidDataException("Unsupported mRemoteNG key derivation iteration count.");
        byte[] data = Convert.FromBase64String(encoded);
        if (data.Length < 48) throw new InvalidDataException("An encrypted mRemoteNG value is incomplete.");
        // mRemoteNG's PKCS#5 conversion uses the low byte of each password character.
        byte[] passwordBytes = password.Select(c => (byte)c).ToArray();
        byte[] salt = data[..16];
        byte[] nonce = data[16..32];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA1, 32);
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(false, new AeadParameters(new KeyParameter(key), 128, nonce, salt));
        byte[] plain = new byte[cipher.GetOutputSize(data.Length - 32)];
        try
        {
            int length = cipher.ProcessBytes(data, 32, data.Length - 32, plain, 0);
            length += cipher.DoFinal(plain, length);
            return Encoding.UTF8.GetString(plain, 0, length);
        }
        catch (InvalidCipherTextException ex)
        {
            throw new ImportPasswordException("Enter the mRemoteNG master password, or uncheck password import to import settings only. The supplied key could not authenticate the encrypted data.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
