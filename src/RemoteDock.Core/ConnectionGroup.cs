namespace RemoteDock.Core;

[Flags]
public enum InheritedSettings
{
    None = 0,
    UserName = 1,
    Port = 2,
    Display = 4,
    Clipboard = 8,
    Domain = 16,
    Password = 32,
    WindowsKeys = 64,
    SignIn = UserName | Domain | Password,
    Redirection = Clipboard | WindowsKeys,
    All = SignIn | Port | Display | Redirection
}

public sealed record ConnectionGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "New group";
    public Guid? ParentId { get; init; }
    public bool InheritPassword { get; init; } = true;
    public ConnectionDefaults Defaults { get; init; } = new();
}

/// <summary>Null means inherit from the parent group. Empty strings and false are explicit defaults.</summary>
public sealed record ConnectionDefaults
{
    public string? UserName { get; init; }
    public string? Domain { get; init; }
    public int? Port { get; init; }
    public DisplayMode? DisplayMode { get; init; }
    public ResolutionScale? Scale { get; init; }
    public int? DesktopWidth { get; init; }
    public int? DesktopHeight { get; init; }
    public bool? RedirectClipboard { get; init; }
    public bool? RedirectWindowsKeys { get; init; }

    public ConnectionProfile Apply(ConnectionProfile profile, InheritedSettings inherit = InheritedSettings.All) => profile with
    {
        UserName = inherit.HasFlag(InheritedSettings.UserName) ? UserName ?? profile.UserName : profile.UserName,
        Domain = inherit.HasFlag(InheritedSettings.Domain) ? Domain ?? profile.Domain : profile.Domain,
        Port = inherit.HasFlag(InheritedSettings.Port) ? Port ?? profile.Port : profile.Port,
        DisplayMode = inherit.HasFlag(InheritedSettings.Display) ? DisplayMode ?? profile.DisplayMode : profile.DisplayMode,
        Scale = inherit.HasFlag(InheritedSettings.Display) ? Scale ?? profile.Scale : profile.Scale,
        DesktopWidth = inherit.HasFlag(InheritedSettings.Display) ? DesktopWidth ?? profile.DesktopWidth : profile.DesktopWidth,
        DesktopHeight = inherit.HasFlag(InheritedSettings.Display) ? DesktopHeight ?? profile.DesktopHeight : profile.DesktopHeight,
        RedirectClipboard = inherit.HasFlag(InheritedSettings.Clipboard) ? RedirectClipboard ?? profile.RedirectClipboard : profile.RedirectClipboard,
        RedirectWindowsKeys = inherit.HasFlag(InheritedSettings.WindowsKeys) ? RedirectWindowsKeys ?? profile.RedirectWindowsKeys : profile.RedirectWindowsKeys
    };

    public void Validate() => Apply(new ConnectionProfile { Host = "validation.invalid" }).Validate();
}
