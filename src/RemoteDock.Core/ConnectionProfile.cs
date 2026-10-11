using System.Text.Json.Serialization;

namespace RemoteDock.Core;

public enum ConnectionKind { Rdp, HyperV }
public enum DisplayMode { ScaledFit, ActualSize, MatchPanel }

public sealed record ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "New connection";
    public string Host { get; init; } = "";
    public DateTimeOffset? LastConnectedAt { get; init; }
    public Guid? GroupId { get; init; }
    public InheritedSettings Inherit { get; init; }
    public int Port { get; init; } = 3389;
    public ConnectionKind Kind { get; init; }
    public string UserName { get; init; } = "";
    public string Domain { get; init; } = "";
    public Guid? VirtualMachineId { get; init; }
    public bool EnhancedSession { get; init; }
    public bool AdministrativeSession { get; init; }
    public DisplayMode DisplayMode { get; init; } = DisplayMode.ScaledFit;
    // Zero follows the local monitor's Windows scaling.
    public int RemoteDpiScalePercent { get; init; }
    // Retained for compatibility with profiles saved by the percentage-only version.
    public int ResolutionScalePercent { get; init; } = 50;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResolutionScale? Scale { get; init; }
    [JsonIgnore]
    public ResolutionScale EffectiveScale => Scale ?? ResolutionScale.FromPercent(ResolutionScalePercent);
    public int DesktopWidth { get; init; } = 1920;
    public int DesktopHeight { get; init; } = 1080;
    public bool RedirectClipboard { get; init; } = true;
    public bool RedirectWindowsKeys { get; init; }

    public void Validate()
    {
        if (Id == Guid.Empty) throw new ArgumentException("Connection ID is missing.");
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Enter a connection name.");
        if (string.IsNullOrWhiteSpace(Host) || Host.Any(char.IsWhiteSpace))
            throw new ArgumentException("Enter a hostname or IP address without spaces.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(DisplayMode))
            throw new ArgumentException("Unknown connection or display mode.");
        if ((Inherit & ~InheritedSettings.All) != 0)
            throw new ArgumentException("Unknown inherited connection settings.");
        if (ResolutionScalePercent is < 25 or > 100)
            throw new ArgumentException("Resolution scale must be between 25 and 100 percent.");
        EffectiveScale.Validate();
        if (RemoteDpiScalePercent != 0 && RemoteDpiScalePercent is < 100 or > 500)
            throw new ArgumentException("Remote DPI scale must follow the local PC or be between 100 and 500 percent.");
        if (Kind == ConnectionKind.HyperV && (VirtualMachineId is null || VirtualMachineId == Guid.Empty))
            throw new ArgumentException("Enter the Hyper-V virtual machine ID (GUID).");
        if (DesktopWidth is < 200 or > 8192 || DesktopHeight is < 200 or > 8192)
            throw new ArgumentException("Desktop dimensions must be between 200 and 8192 pixels.");
    }
}
