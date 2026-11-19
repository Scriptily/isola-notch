namespace Isola.Island;

/// <summary>What a finger works on: the island, or anything else built from <see cref="Ux"/> zones.</summary>
public interface ITouchTarget
{
    Ux TouchUx { get; }
    void MouseMove(float x, float y);
    void MouseDown(float x, float y);
    void MouseUp(float x, float y);
    /// <summary>Press-and-drag on the target's own body (the island tugs, rubber-banded).</summary>
    void DragTo(float dx, float dy);
    void DragEnd(float dx, float dy, float velocityY);
    void Swipe(float dx, float dy);
    /// <summary>The finger lifted. Not a hover leaving: touch has no hover.</summary>
    void TouchLifted();
}

/// <summary>
/// One finger (or pen) on the island, fed from the host's pointer messages. It decides what the finger
/// is doing once it has moved a little: a tap presses like a click; sideways on a slider or scrubber it
/// drags it; up or down over a list it scrolls the list under it 1:1 and flings on release; on the
/// island's own body it tugs the island like the mouse does; anything else is a swipe. A press that just
/// misses a control goes to the nearest one within <see cref="Slop"/>, since a fingertip covers more
/// than a pointer.
/// </summary>
public sealed class TouchInput
{
    /// <summary>How far outside a control a fingertip may land and still press it, in DIPs.</summary>
    public const float Slop = 10;
    /// <summary>How far a finger travels before it's a drag, scroll or swipe instead of a tap, in DIPs.</summary>
    public const float Decide = 10;

    enum Kind { Undecided, Drag, Scroll, Tug, Swipe }
    readonly ITouchTarget target;
    Kind kind;
    float x0, y0, lastY, vel;
    double lastT;
    Ux.Zone scroll;

    public TouchInput(ITouchTarget target) { this.target = target; }

    public void Down(float x, float y, double t)
    {
        var ux = target.TouchUx;
        kind = Kind.Undecided; x0 = x; y0 = y; lastY = y; lastT = t; vel = 0;
        ux.Slop = Slop; ux.TouchMode = true;
        target.MouseMove(x, y);
        target.MouseDown(x, y);
        scroll = ux.ScrollZoneAt(x, y);
        if (ux.Dragging != null) kind = Kind.Drag;   // a slider took the press: it follows the finger from here
    }

    public void Move(float x, float y, double t)
    {
        var ux = target.TouchUx;
        float dx = x - x0, dy = y - y0;
        if (t > lastT) vel = vel * 0.6f + 0.4f * (float)((y - lastY) / (t - lastT));
        if (kind == Kind.Undecided && Math.Abs(dx) + Math.Abs(dy) > Decide)
        {
            var pressed = ux.PressedZone;
            bool vertical = Math.Abs(dy) > Math.Abs(dx);
            if (!vertical && pressed?.Drag != null) kind = Kind.Drag;
            else
            {
                kind = vertical && scroll != null ? Kind.Scroll : vertical && pressed == null ? Kind.Tug : Kind.Swipe;
                ux.CancelPress();
                if (kind == Kind.Scroll) ux.Pan(scroll, dy);   // the distance it took to decide counts too
            }
        }
        else if (kind == Kind.Scroll) ux.Pan(scroll, y - lastY);
        if (kind == Kind.Drag) target.MouseMove(x, y);
        else if (kind == Kind.Tug) target.DragTo(dx, dy);
        lastY = y; lastT = t;
    }

    public void Up(float x, float y, double t)
    {
        var ux = target.TouchUx;
        if (t - lastT > 0.08) vel = 0;   // it stopped before lifting: no throw
        switch (kind)
        {
            case Kind.Undecided: case Kind.Drag: target.MouseUp(x, y); break;
            case Kind.Scroll: ux.Pan(scroll, vel * 0.2f); break;   // a flick carries on, about as far as it was going in 0.2 s
            case Kind.Tug: target.DragEnd(x - x0, y - y0, vel); break;
            case Kind.Swipe: ux.Up(-1e4f, -1e4f); target.Swipe(x - x0, y - y0); break;
        }
        ux.Slop = 0;
        target.TouchLifted();
    }
}

/// <summary>
/// One finger scrolling a whole page (Settings): it follows the finger 1:1; past either end it
/// rubber-bands (nothing for the first 12 px, then less the further it's pulled, never past 64 px); let
/// go, it keeps the finger's speed into a spring that comes to rest inside the page.
/// </summary>
public sealed class PagePan
{
    float startScroll, startY, lastY, at, vel, max;
    double lastT;

    public void Begin(float scroll, float maxScroll, float y, double t)
    {
        startScroll = at = scroll; max = Math.Max(0, maxScroll); startY = lastY = y; lastT = t; vel = 0;
    }

    /// <returns>Where the page is now, rubber-banded past its ends.</returns>
    public float Move(float y, double t)
    {
        if (t > lastT) vel = vel * 0.6f + 0.4f * (float)(-(y - lastY) / (t - lastT));
        lastY = y; lastT = t;
        return at = Band(startScroll - (y - startY));
    }

    /// <returns>Where the spring should take the page, and the speed it starts at (px/s, scrolling down positive).</returns>
    public (float target, float velocity) End(double t)
    {
        float v = t - lastT > 0.08 ? 0 : vel;     // it stopped before lifting: no throw
        if (at < 0 || at > max) v *= 0.2f;        // already past an end: just come back
        return (Math.Clamp(at + v * 0.25f, 0, max), v);
    }

    public static float Rubber(float pull) { float over = Math.Max(0, pull - 12); return 64 * over / (over + 150); }
    float Band(float raw) => raw < 0 ? -Rubber(-raw) : raw > max ? max + Rubber(raw - max) : raw;
}
