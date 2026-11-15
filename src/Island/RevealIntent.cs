namespace Isola.Island;

/// <summary>
/// When a hidden island comes back for a pointer at the top of the screen.
///
/// The top strip is also where browser tabs are: thrown up there to pick a
/// tab, the pointer revealed the island after 0.12 s, on top of the tab. Now
/// it has to rest there (within a few pixels) for 0.35 s with no button held,
/// and a click up there means the user is working: nothing for 1.5 s after.
/// </summary>
public sealed class RevealIntent
{
    public const double Dwell = 0.35, AfterClick = 1.5;
    public const float StillRadius = 6;
    double since = -1, quietUntil = double.NegativeInfinity;
    float ax, ay;

    public void Reset() => since = -1;

    /// <summary>Called while the pointer is in the reveal strip. True once: reveal now.</summary>
    public bool Update(double now, float x, float y, bool buttonDown)
    {
        if (buttonDown) { quietUntil = now + AfterClick; since = -1; return false; }
        if (now < quietUntil) { since = -1; return false; }
        if (since < 0 || MathF.Abs(x - ax) + MathF.Abs(y - ay) > StillRadius) { since = now; ax = x; ay = y; return false; }
        if (now - since < Dwell) return false;
        since = -1;
        return true;
    }
}
