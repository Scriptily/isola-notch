namespace Isola.Island;

/// <summary>
/// A mouse press on the island's body, followed until release, in DIPs. It becomes a drag once the
/// pointer has moved <see cref="Start"/> in any direction, and from then it's either up-and-down (the
/// island tugs) or sideways (a swipe, judged on release). The speed it reports is how fast it was moving
/// when released: a pointer that stopped before letting go has no fling left, so a slow pull that pauses
/// doesn't throw the island open or away.
/// </summary>
public sealed class MouseDrag
{
    /// <summary>Movement that turns a press into a drag (less is a click with a shaky hand).</summary>
    public const float Start = 6;
    /// <summary>How far sideways a drag has to go to change track or tab: further than a finger, since a mouse slips sideways more.</summary>
    public const float SideSwipe = 60;
    /// <summary>A pause this long before release means no fling.</summary>
    public const double StillBeforeRelease = 0.08;

    float x0, y0, lastY, vel;
    double lastT;
    public bool Dragging { get; private set; }
    public bool Sideways { get; private set; }

    public void Down(float x, float y, double t) { x0 = x; y0 = y; lastY = y; lastT = t; vel = 0; Dragging = Sideways = false; }

    /// <returns>True the moment it becomes a drag.</returns>
    public bool Move(float x, float y, double t)
    {
        float dx = x - x0, dy = y - y0;
        bool began = false;
        if (!Dragging && dx * dx + dy * dy > Start * Start) { Dragging = began = true; Sideways = Math.Abs(dx) > Math.Abs(dy); }
        if (t > lastT) vel = vel * 0.6f + 0.4f * (float)((y - lastY) / (t - lastT));
        lastY = y; lastT = t;
        return began;
    }

    public (float dx, float dy) Offset(float x, float y) => (x - x0, y - y0);

    /// <returns>How far it went, and its vertical speed at release (DIP/s; 0 if it had stopped).</returns>
    public (float dx, float dy, float vy) Up(float x, float y, double t) => (x - x0, y - y0, t - lastT > StillBeforeRelease ? 0 : vel);
}
