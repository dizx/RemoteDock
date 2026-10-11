using System.Text;
using RemoteDock.Core;

namespace RemoteDock.Core.Tests;

public sealed class ConnectionImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "opencode", "RemoteDock.ImportTests", Guid.NewGuid().ToString("N"));
    // Independent Node.js/OpenSSL AES-256-GCM fixture: 16-byte salt + 16-byte nonce + ciphertext + tag.
    private const string Encrypted = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh+zyt5kijQ5RawkPxr+NA0c40ogLLK8gc2t0Gvc5Hg6iw==";
    private const string CustomEncrypted = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh9G/9+WhsQi5VYkeU+jFDk6d/rBnoIP+/HxLOBRrAxndg==";

    private string Write(string name, string contents, Encoding? encoding = null)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, contents, encoding ?? Encoding.UTF8);
        return path;
    }

    private static string Xml(string password) => $"""
        <Connections ConfVersion="2.6" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="1000" FullFileEncryption="false">
          <Node Type="Container" Id="group" Name="Lab" Username="operator" Domain="PARENT" Password="{password}" Port="3389">
            <Node Type="Connection" Id="rdp" Name="Desktop" Protocol="RDP" Hostname="desktop.example" Port="3389"
                  InheritUsername="true" Domain="LOCAL" InheritDomain="false" InheritPassword="true" Resolution="FitToWindow" AutomaticResize="true" />
            <Node Type="Connection" Id="ssh" Name="Shell" Protocol="SSH2" Hostname="shell.example" />
          </Node>
        </Connections>
        """;

    [Fact]
    public void MRemoteNgPreservesFoldersIndependentInheritanceAndPasswords()
    {
        var imported = MRemoteNgImporter.Read(Write("connections.xml", Xml(Encrypted)));
        Assert.Equal(2, imported.Groups.Length);
        Assert.Equal(1, imported.SkippedCount);
        var profile = Assert.Single(imported.Connections);
        var effective = imported.MergeInto(new()).Resolve(profile);
        Assert.Equal("operator", effective.UserName);
        Assert.Equal("LOCAL", effective.Domain);
        Assert.True(profile.Inherit.HasFlag(InheritedSettings.UserName));
        Assert.False(profile.Inherit.HasFlag(InheritedSettings.Domain));
        Assert.True(profile.Inherit.HasFlag(InheritedSettings.Password));
        Assert.Equal("Import-test-only", Assert.Single(imported.Passwords.Where(p => p.Password is not null)).Password);
        Assert.Equal(DisplayMode.ScaledFit, effective.DisplayMode);
        Assert.Equal(new ResolutionScale(1, 2), effective.EffectiveScale);
    }

    [Fact]
    public void CustomMasterPasswordAndSettingsOnlyImportAreSupported()
    {
        string path = Write("protected.xml", Xml(CustomEncrypted));
        Assert.Throws<ImportPasswordException>(() => MRemoteNgImporter.Read(path));
        Assert.Equal("Import-test-only", Assert.Single(MRemoteNgImporter.Read(path, masterPassword: "test-master").Passwords.Where(p => p.Password is not null)).Password);
        Assert.Empty(MRemoteNgImporter.Read(path, includePasswords: false).Passwords);
    }

    [Fact]
    public void TamperedEncryptedValuesAreRejected()
    {
        byte[] blob = Convert.FromBase64String(Encrypted);
        blob[^1] ^= 1;
        Assert.Throws<ImportPasswordException>(() => MRemoteNgImporter.Read(Write("tampered.xml", Xml(Convert.ToBase64String(blob)))));
    }

    [Fact]
    public void ReimportUpdatesByStableIdAndKeepsUnrelatedConnections()
    {
        string path = Write("connections.xml", Xml(""));
        var unrelated = new ConnectionProfile { Host = "other.example" };
        var first = MRemoteNgImporter.Read(path).MergeInto(new() { Connections = [unrelated] });
        Write("connections.xml", Xml("").Replace("desktop.example", "renamed.example"));
        var second = MRemoteNgImporter.Read(path).MergeInto(first);
        Assert.Equal(2, second.Connections.Length);
        Assert.Equal(2, second.Groups.Length);
        Assert.Contains(unrelated, second.Connections);
        Assert.Contains(second.Connections, p => p.Host == "renamed.example");
    }

    [Fact]
    public void XmlExternalEntitiesAreRejected()
    {
        Assert.ThrowsAny<Exception>(() => MRemoteNgImporter.Read(Write("unsafe.xml", "<!DOCTYPE Connections [<!ENTITY x SYSTEM 'file:///C:/secret'>]><Connections>&x;</Connections>")));
    }

    [Fact]
    public void Utf16RdpFilesPreserveEndpointCredentialsAndDisplay()
    {
        string path = Write("Server.rdp", "full address:s:server.example:3390\r\nusername:s:DOMAIN\\operator\r\ndomain:s:DOMAIN\r\ndesktopwidth:i:1600\r\ndesktopheight:i:900\r\nredirectclipboard:i:0\r\nadministrative session:i:1\r\n", Encoding.Unicode);
        var imported = RdpFileImporter.Read(path, scaledFit: false);
        var profile = Assert.Single(imported.Connections);
        Assert.Equal("server.example", profile.Host);
        Assert.Equal(3390, profile.Port);
        Assert.Equal("DOMAIN\\operator", profile.UserName);
        Assert.Equal(DisplayMode.ActualSize, profile.DisplayMode);
        Assert.Equal(1600, profile.DesktopWidth);
        Assert.False(profile.RedirectClipboard);
        Assert.True(profile.AdministrativeSession);
    }

    [Fact]
    public void RdpIpv6AndProtectedPasswordAreParsedWithoutWritingASecretToTheProfile()
    {
        var imported = RdpFileImporter.Read(Write("IPv6.rdp", "full address:s:[2001:db8::1]:3391\npassword 51:b:01020304"));
        Assert.Equal("2001:db8::1", imported.Connections[0].Host);
        Assert.Equal(3391, imported.Connections[0].Port);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Assert.Single(imported.Passwords).WindowsProtected);
    }

    [Theory]
    [InlineData("remoteapplicationmode:i:1")]
    [InlineData("gatewayhostname:s:gateway.example\ngatewayusagemethod:i:1")]
    public void UnsupportedRdpModesAreNotSilentlyTurnedIntoDirectDesktopConnections(string extra)
    {
        Assert.Throws<NotSupportedException>(() => RdpFileImporter.Read(Write("unsupported.rdp", "full address:s:host.example\n" + extra)));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
