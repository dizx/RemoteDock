namespace RemoteDock.Core;

/// <summary>A ratio of remote desktop pixels to local panel pixels.</summary>
public readonly record struct ResolutionScale(int Numerator, int Denominator)
{
    public static ResolutionScale FromPercent(int percent)
    {
        int a = percent, b = 100;
        while (b != 0) (a, b) = (b, a % b);
        return new(percent / a, 100 / a);
    }

    public void Validate()
    {
        if (Denominator <= 0 || Numerator <= 0 || Numerator > Denominator || (long)Numerator * 4 < Denominator)
            throw new ArgumentException("Resolution scale must be between 1/4 and 1/1.");
    }

    public int ScalePixels(int pixels) => (int)Math.Clamp((long)pixels * Numerator / Denominator, 200, 8192);

    public override string ToString() => $"{Numerator}/{Denominator}";
}
