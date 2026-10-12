using System.Text.Json.Serialization;

namespace RemoteDock.Core;

public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized = false)
{
    [JsonIgnore]
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height) &&
        Width > 0 && Height > 0;
}
