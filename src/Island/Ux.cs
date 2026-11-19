using System.Numerics;
using Isola.Render;
using Vortice.Mathematics;
using Box = Isola.Render.Box;
using FontWeight = Vortice.DirectWrite.FontWeight;

namespace Isola.Island;

public enum CursorKind { Arrow, Hand, IBeam }

/// <summary>
/// Immediate-mode interaction: each frame, views declare their interactive
/// zones while drawing; input between frames is hit-tested against the zones of
/// the last drawn frame. Every zone gets hover and press springs for free, which
/// is what makes buttons react the way Apple's do (lift on hover, squish on
/// press, spring back on release).
/// </summary>
public sealed class Ux
{
    public sealed class Zone
    {
        public string Id; public Box Box; public float Radius;
        public Action Click; public Action RightClick; public Action<float, float, bool> Drag;   // x, y, final
        public Action<float, bool> Wheel;   // delta, horizontal
        public CursorKind Cursor = CursorKind.Hand;
        public bool Horizontal;   // also takes sideways scrolling (otherwise that falls through to tab swiping)
        public string Tooltip;
        /// <summary>How far one wheel notch (120) scrolls this zone, in DIPs: a finger scrolls it at the same rate.</summary>
        public float ScrollPx = 50;
        /// <summary>Scrolls by steps whatever the delta (search results): a finger sends a notch every ScrollPx.</summary>
        public bool Notched;
        internal float PanRest;
    }

    readonly List<Zone> building = new();
    List<Zone> live = new();
    readonly Dictionary<string, Spring> springs = new();
    readonly HashSet<string> touched = new();
    public Canvas C;
    public float MouseX = -1e4f, MouseY = -1e4f;
    public bool MouseInside;
    public string Hot { get; private set; }
    public string Pressed { get; private set; }
    public string Dragging { get; private set; }
    float pressX, pressY;
    public bool ReduceMotion;
    public Action RequestFrame = () => { };
    public Func<double> Now = () => 0;

    public void BeginFrame(Canvas c) { C = c; building.Clear(); touched.Clear(); }

    /// <summary>
    /// Forget the last frame's click targets right now - called the moment a
    /// page or tab changes, so the old page's buttons can't be pressed in the
    /// gap before the next frame is drawn.
    /// </summary>
    public void ClearZones() { live = new List<Zone>(); Pressed = null; Dragging = null; Hot = null; }
    /// <summary>
    /// Test harness only (ISOLA_ZONES): after each frame whose click
    /// targets changed, write them to this file in screen pixels, so a live
    /// click-through can press every control by its id instead of guessing
    /// where a spring-animated layout put it.
    /// </summary>
    public string DumpFile;
    public Func<(float x, float y, float scale)> Origin;
    string dumped;

    void Dump()
    {
        if (DumpFile == null || Origin == null) return;
        var (ox, oy, sc) = Origin();
        var sb = new System.Text.StringBuilder();
        foreach (var z in live)
            sb.Append(z.Id).Append('\t').Append((int)(ox + z.Box.CX * sc)).Append('\t').Append((int)(oy + z.Box.CY * sc))
              .Append('\t').Append((int)(z.Box.W * sc)).Append('\t').Append((int)(z.Box.H * sc)).Append('\n');
        string s = sb.ToString();
        if (s == dumped) return;
        dumped = s;
        try { File.WriteAllText(DumpFile + ".tmp", s); File.Move(DumpFile + ".tmp", DumpFile, true); } catch { }
    }

    public void EndFrame()
    {
        live = new List<Zone>(building);
        Dump();
        // keep springs for zones still drawn; let the rest finish and be dropped
        foreach (var k in springs.Keys.ToList())
            if (!touched.Contains(k) && springs[k].Settled) springs.Remove(k);
        RecomputeHot();
    }

    public Zone Add(string id, Box b, Action click = null, float radius = 8, CursorKind cursor = CursorKind.Hand)
    {
        var z = new Zone { Id = id, Box = b, Click = click, Radius = radius, Cursor = cursor };
        building.Add(z);
        return z;
    }

    /// <summary>A spring keyed by name, retargeted every frame it is used.</summary>
    public float Anim(string key, double target, SpringSpec spec, double eps = 0.002)
    {
        touched.Add(key);
        if (!springs.TryGetValue(key, out var s)) { s = new Spring(target, spec, eps); springs[key] = s; }
        if (ReduceMotion) spec = SpringSpec.Reduced;
        if (s.Target != target || s.Spec != spec) s.To(target, spec);
        return (float)s.Value;
    }
    /// <summary>Starts at <paramref name="from"/> the first time it is seen (entrance animations).</summary>
    public float AnimFrom(string key, double from, double target, SpringSpec spec)
    {
        if (!springs.ContainsKey(key)) springs[key] = new Spring(from, spec);
        return Anim(key, target, spec);
    }
    public void Kick(string key, double v) { if (springs.TryGetValue(key, out var s)) s.Kick(v); RequestFrame(); }
    public void Forget(string prefix) { foreach (var k in springs.Keys.Where(k => k.StartsWith(prefix)).ToList()) springs.Remove(k); }

    public bool Step(double dt)
    {
        bool moving = false;
        foreach (var s in springs.Values) moving |= s.Step(dt);
        return moving;
    }

    public string MovingKeys() => string.Join(",", springs.Where(kv => !kv.Value.Settled).Select(kv => kv.Key + "=" + kv.Value).Take(6));

    // A control inside a row or card (its id extends the parent's: "note:123:del"
    // in "note:123") keeps the parent hovered. Otherwise pointing at a hover-only
    // button ended the hover that drew it: a note's trash icon and a shelf item's
    // close button faded out as the pointer reached them, and the click missed.
    public float HoverT(string id) => Anim("hover:" + id, Hot != null && (Hot == id || Hot.StartsWith(id + ":", StringComparison.Ordinal)) ? 1 : 0, SpringSpec.Snappy);
    public float PressT(string id) => Anim("press:" + id, Pressed == id && Hot == id ? 1 : 0, SpringSpec.Press);

    // ---- input -------------------------------------------------------------
    /// <summary>
    /// Topmost interactive zone under the point. Wheel-only zones (scroll areas) never take clicks. With
    /// <see cref="Slop"/> set (a finger is down), a point just outside every control goes to the nearest one.
    /// </summary>
    public Zone HitTest(float x, float y)
    {
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var z = live[i];
            if (z.Click == null && z.Drag == null && z.RightClick == null) continue;
            if (z.Box.Contains(x, y)) return z;
        }
        if (Slop <= 0) return null;
        Zone best = null; float bestD = Slop;
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var z = live[i];
            if (z.Click == null && z.Drag == null) continue;
            float dx = Math.Max(0, Math.Max(z.Box.X - x, x - z.Box.Right)), dy = Math.Max(0, Math.Max(z.Box.Y - y, y - z.Box.Bottom));
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d <= bestD) { best = z; bestD = d; }
        }
        return best;
    }

    /// <summary>A fingertip covers more than a pointer: while one is down, controls take presses this far outside them.</summary>
    public float Slop;
    /// <summary>
    /// The last input was a finger (until the mouse really moves). Controls that otherwise appear only on
    /// hover (a shelf item's remove button, a note's delete) stay visible: a finger can't hover to find them.
    /// </summary>
    public bool TouchMode;
    public Zone PressedZone => Pressed == null ? null : live.FirstOrDefault(q => q.Id == Pressed);
    /// <summary>A zone by id, from this frame if it's been added yet, else the last.</summary>
    public Zone Find(string id) => building.FirstOrDefault(q => q.Id == id) ?? live.FirstOrDefault(q => q.Id == id);
    /// <summary>The topmost scroll area under the point, if any.</summary>
    public Zone ScrollZoneAt(float x, float y)
    {
        for (int i = live.Count - 1; i >= 0; i--) if (live[i].Wheel != null && live[i].Box.Contains(x, y)) return live[i];
        return null;
    }
    /// <summary>A finger moved <paramref name="dy"/> DIPs over a scroll area: the content follows it (down shows what's above).</summary>
    public void Pan(Zone z, float dy)
    {
        if (z?.Wheel == null || dy == 0) return;
        if (!z.Notched) z.Wheel(dy * 120 / z.ScrollPx, false);
        else
        {
            z.PanRest += dy;
            while (Math.Abs(z.PanRest) >= z.ScrollPx) { float s = Math.Sign(z.PanRest); z.Wheel(s * 120, false); z.PanRest -= s * z.ScrollPx; }
        }
        RequestFrame();
    }
    /// <summary>Puts a spring at <paramref name="v"/> without animating (a finger is holding it there).</summary>
    public void Snap(string key, double v) { if (springs.TryGetValue(key, out var s)) s.Snap(v); RequestFrame(); }
    /// <summary>Sets a spring moving at <paramref name="v"/> per second (a fling).</summary>
    public void SetVelocity(string key, double v) { if (springs.TryGetValue(key, out var s)) s.Velocity = v; RequestFrame(); }

    // ---- tooltips: the first waits, the ones after it show at once ----------
    string tipId; double tipSince; double tipCoolUntil = -1; bool tipShown, tipInstant;
    /// <summary>The first tooltip waits this long, so tips don't flash while the pointer passes over controls.</summary>
    public const double TipDelay = 0.6;
    /// <summary>After a tip closes, the next one within this long shows at once.</summary>
    public const double TipWarm = 0.3;
    /// <summary>
    /// The tooltip to draw now, if any, and whether it should animate in (only the first after a pause
    /// does; moving along a row of controls, each tip just appears). Never while pressing, or for touch.
    /// </summary>
    public (Zone zone, bool animate)? Tip(double now)
    {
        var z = Hot == null || !MouseInside || Pressed != null ? null : live.FirstOrDefault(q => q.Id == Hot && !string.IsNullOrEmpty(q.Tooltip));
        if (z == null)
        {
            if (tipShown) tipCoolUntil = now + TipWarm;
            tipShown = false; tipId = null;
            return null;
        }
        if (z.Id != tipId)
        {
            tipInstant = tipShown || now < tipCoolUntil;
            tipId = z.Id; tipSince = now; tipShown = false;
        }
        if (!tipInstant && now - tipSince < TipDelay) return null;
        tipShown = true;
        return (z, !tipInstant);
    }
    /// <summary>When a waiting tooltip is due (to wake for it), or never.</summary>
    public double TipDue => tipId != null && !tipShown && !tipInstant ? tipSince + TipDelay : double.PositiveInfinity;

    void RecomputeHot()
    {
        string h = MouseInside ? HitTest(MouseX, MouseY)?.Id : null;
        if (Dragging != null) h = Dragging;
        if (h != Hot) { Hot = h; RequestFrame(); }
    }

    public CursorKind CursorAt(float x, float y) => HitTest(x, y)?.Cursor ?? CursorKind.Arrow;

    public void Move(float x, float y)
    {
        MouseX = x; MouseY = y; MouseInside = true;
        if (Dragging != null)
        {
            var z = live.FirstOrDefault(q => q.Id == Dragging);
            z?.Drag?.Invoke(x, y, false);
            RequestFrame();
        }
        else if (Pressed != null && Math.Abs(x - pressX) + Math.Abs(y - pressY) > 4)
        {
            var z = live.FirstOrDefault(q => q.Id == Pressed);
            if (z?.Drag != null) { Dragging = Pressed; z.Drag(x, y, false); }
        }
        RecomputeHot();
    }

    public void Leave() { MouseInside = false; if (Dragging == null) { Pressed = null; } RecomputeHot(); }

    /// <returns>true when a zone took the press.</returns>
    public bool Down(float x, float y)
    {
        Move(x, y);
        var z = HitTest(x, y);
        if (z == null) return false;
        Pressed = z.Id; pressX = x; pressY = y;
        if (z.Drag != null && z.Click == null) { Dragging = z.Id; z.Drag(x, y, false); }
        RequestFrame();
        return true;
    }

    /// <returns>true when a zone handled the release.</returns>
    public bool Up(float x, float y)
    {
        bool handled = false;
        if (Dragging != null)
        {
            var z = live.FirstOrDefault(q => q.Id == Dragging);
            z?.Drag?.Invoke(x, y, true);
            handled = true;
        }
        else if (Pressed != null)
        {
            var z = HitTest(x, y);
            if (z != null && z.Id == Pressed) { z.Click?.Invoke(); handled = true; }
        }
        Pressed = null; Dragging = null;
        RecomputeHot();
        RequestFrame();
        return handled;
    }

    public bool RightUp(float x, float y)
    {
        var z = HitTest(x, y);
        if (z?.RightClick == null) return false;
        z.RightClick(); return true;
    }

    public bool WheelAt(float x, float y, float delta, bool horizontal)
    {
        for (int i = live.Count - 1; i >= 0; i--)
            if (live[i].Wheel != null && (!horizontal || live[i].Horizontal) && live[i].Box.Contains(x, y)) { live[i].Wheel(delta, horizontal); RequestFrame(); return true; }
        return false;
    }

    public void CancelPress() { Pressed = null; Dragging = null; }

    // ---- widgets -----------------------------------------------------------
    /// <summary>
    /// Round icon button: soft highlight on hover, squish on press, a little
    /// glow when <paramref name="accent"/> is set.
    /// </summary>
    public void IconButton(string id, float cx, float cy, float size, char glyph, Action click, Color4? accent = null, float glyphSize = 0,
                           bool enabled = true, Color4? fg = null)
    {
        // the target is at least MinHit across, however small the drawn circle:
        // a 26 px button was easy to miss by a pixel or two
        float hit = Math.Max(size, MinHit);
        if (enabled) Add(id, Box.Centered(cx, cy, hit, hit), click, hit / 2);
        float h = HoverT(id), p = PressT(id);
        // the highlight stays inside the button's own footprint (plus a hair of
        // glow), so a button near an edge never bleeds past the island
        float s = (1 + 0.04f * h) * (1 - 0.03f * p);   // a press takes 3% off, hovered or not
        var bg = accent ?? Col.White;
        if (accent.HasValue) C.Circle(cx, cy, size / 2 * s * (1.08f + 0.04f * h), accent.Value.A(0.14f + 0.12f * h));
        C.Circle(cx, cy, size / 2 * s, bg.A(accent.HasValue ? 0.22f + 0.12f * h : 0.0f + 0.13f * h + 0.08f * p));
        SwapIcon(id, glyph, cx, cy, (glyphSize > 0 ? glyphSize : size * 0.42f) * s, (fg ?? Col.White).A(enabled ? 0.92f + 0.08f * h : 0.35f));
    }

    /// <summary>Smallest click target for a round icon button, in DIPs.</summary>
    public const float MinHit = 36;

    readonly Dictionary<string, (char now, char was)> glyphs = new();

    /// <summary>
    /// An icon that changes (play to pause, muted to not) crossfades with a
    /// little blur, so it reads as one shape changing rather than two icons
    /// overlapping for a frame.
    /// </summary>
    public void SwapIcon(string id, char glyph, float cx, float cy, float size, Color4 color)
    {
        if (!glyphs.TryGetValue(id, out var g)) g = (glyph, glyph);
        if (g.now != glyph) { g = (glyph, ReduceMotion ? glyph : g.now); springs.Remove("swap:" + id); if (!ReduceMotion) AnimFrom("swap:" + id, 0, 1, SpringSpec.Snappy); }
        glyphs[id] = g;
        float t = g.was == glyph || ReduceMotion ? 1 : Anim("swap:" + id, 1, SpringSpec.Snappy);
        if (t >= 0.995f) { C.Icon(glyph, cx, cy, size, color); return; }
        t = Math.Clamp(t, 0, 1);
        C.Blurred(4 * t, () => C.Icon(g.was, cx, cy, size, color), 1 - t);
        C.Blurred(4 * (1 - t), () => C.Icon(glyph, cx, cy, size, color), t);
    }

    /// <summary>Text button / chip.</summary>
    public void Pill(string id, Box b, string text, Action click, Color4 tint, char glyph = '\0', bool selected = false, float fontSize = 12.5f)
    {
        Add(id, b, click, b.H / 2);
        float h = HoverT(id), p = PressT(id);
        float s = 1 - 0.03f * p;
        var bb = b.Scale(s);
        C.Fill(bb, tint.A(selected ? 0.9f : 0.16f + 0.12f * h), bb.H / 2);
        var fg = selected ? Col.Black : Col.White;
        float tx = bb.X + 12;
        if (glyph != '\0') { C.Icon(glyph, bb.X + 16, bb.CY, 13, fg); tx += 14; }
        C.Text(text, new Box(tx, bb.Y, bb.Right - tx - 10, bb.H), fontSize, fg.A(0.95f), FontWeight.SemiBold, glyph == '\0' ? HAlign.Center : HAlign.Left);
    }

    /// <summary>Horizontal slider / progress scrubber. Calls onChange with 0..1 while dragging and once more with final=true.</summary>
    public float Slider(string id, Box track, float value, Action<float, bool> onChange, Color4 fill, float thickness = 5, bool knobAlways = false)
    {
        var z = Add(id, track.Inset(0, -8), null, 6);
        z.Drag = (x, y, fin) => onChange(Math.Clamp((x - track.X) / track.W, 0, 1), fin);
        float h = HoverT(id), d = Anim("drag:" + id, Dragging == id ? 1 : 0, SpringSpec.Snappy);
        float th = thickness * (1 + 0.6f * Math.Max(h, d));
        var bar = new Box(track.X, track.CY - th / 2, track.W, th);
        C.Fill(bar, Col.White.A(0.18f + 0.06f * h), th / 2);
        float v = Math.Clamp(value, 0, 1);
        if (v > 0) C.Fill(new Box(bar.X, bar.Y, Math.Max(th, bar.W * v), th), fill, th / 2);
        float knob = knobAlways ? 1 : Math.Max(h, d);
        if (knob > 0.01f) C.Circle(bar.X + bar.W * v, bar.CY, (th * 0.9f + 3) * knob, Col.White.A(knob));
        return v;
    }

    /// <summary>iOS-style switch.</summary>
    public void Switch(string id, float x, float cy, bool on, Action toggle, Color4 accent)
    {
        var b = new Box(x, cy - 11, 40, 22);
        Add(id, b.Inset(-4, -7), toggle, 18);   // a target taller than the 22 px track
        float t = Anim("sw:" + id, on ? 1 : 0, SpringSpec.Snappy);
        float p = PressT(id);
        C.Fill(b, Col.Mix(Col.Gray(0.32f), accent, t), 11);
        float kw = 18 + 5 * p;
        float kx = b.X + 2 + (b.W - 4 - kw) * t;
        C.Fill(new Box(kx, b.Y + 2, kw, 18), Col.White, 9);
    }
}

/// <summary>
/// Editable text: single-line (search) or multi-line (notes). Caret, selection,
/// word jumps, clipboard, undo. Typed input arrives through the island window,
/// which takes keyboard focus only while one of these is focused.
/// </summary>
public sealed class TextBox
{
    public string Text { get; private set; } = "";
    public int Caret { get; private set; }
    public int Anchor { get; private set; }
    public bool Multiline;
    public int MaxLength = 4000;
    public string Placeholder = "";
    public Action Changed;
    public Action Submit;
    public Action Escape;
    public Func<int, bool> SpecialKey;   // arrows etc. a view wants first (search list navigation)
    readonly Stack<(string, int)> undo = new();
    double lastTypeTime;

    public void Set(string s, bool select = false)
    {
        Text = s ?? ""; Caret = Text.Length; Anchor = select ? 0 : Caret;
        Changed?.Invoke();
    }

    bool HasSel => Anchor != Caret;
    (int a, int b) Sel => (Math.Min(Anchor, Caret), Math.Max(Anchor, Caret));

    void Snapshot(double now) { if (now - lastTypeTime > 0.8 || undo.Count == 0) undo.Push((Text, Caret)); lastTypeTime = now; if (undo.Count > 200) { var keep = undo.Take(100).Reverse().ToList(); undo.Clear(); foreach (var k in keep) undo.Push(k); } }

    void Replace(string ins, double now)
    {
        Snapshot(now);
        var (a, b) = Sel;
        if (!Multiline) ins = ins.Replace("\r", "").Replace("\n", " ");
        else ins = ins.Replace("\r\n", "\n").Replace('\r', '\n');
        string t = Text[..a] + ins + Text[b..];
        if (t.Length > MaxLength) return;
        Text = t; Caret = Anchor = a + ins.Length;
        Changed?.Invoke();
    }

    public void Char(char ch, double now)
    {
        if (ch < 32 && ch != '\t') return;
        Replace(ch.ToString(), now);
    }

    static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
    int WordLeft(int i) { while (i > 0 && !IsWord(Text[i - 1])) i--; while (i > 0 && IsWord(Text[i - 1])) i--; return i; }
    int WordRight(int i) { while (i < Text.Length && !IsWord(Text[i])) i++; while (i < Text.Length && IsWord(Text[i])) i++; return i; }
    int LineStart(int i) { while (i > 0 && Text[i - 1] != '\n') i--; return i; }
    int LineEnd(int i) { while (i < Text.Length && Text[i] != '\n') i++; return i; }

    /// <returns>true when handled.</returns>
    public bool Key(int vk, bool ctrl, bool shift, double now)
    {
        if (SpecialKey != null && SpecialKey(vk)) return true;
        void MoveTo(int p) { Caret = Math.Clamp(p, 0, Text.Length); if (!shift) Anchor = Caret; }
        switch (vk)
        {
            case 0x25: // left
                if (HasSel && !shift) MoveTo(Sel.a); else MoveTo(ctrl ? WordLeft(Caret) : Caret - 1); return true;
            case 0x27: // right
                if (HasSel && !shift) MoveTo(Sel.b); else MoveTo(ctrl ? WordRight(Caret) : Caret + 1); return true;
            case 0x24: MoveTo(ctrl || !Multiline ? 0 : LineStart(Caret)); return true;            // home
            case 0x23: MoveTo(ctrl || !Multiline ? Text.Length : LineEnd(Caret)); return true;    // end
            case 0x26: if (!Multiline) return false; { int ls = LineStart(Caret), col = Caret - ls; if (ls == 0) { MoveTo(0); return true; } int ps = LineStart(ls - 1); MoveTo(Math.Min(ps + col, ls - 1)); } return true;
            case 0x28: if (!Multiline) return false; { int ls = LineStart(Caret), col = Caret - ls, le = LineEnd(Caret); if (le >= Text.Length) { MoveTo(Text.Length); return true; } int ns = le + 1; MoveTo(Math.Min(ns + col, LineEnd(ns))); } return true;
            case 0x08: // backspace
                if (HasSel) Replace("", now);
                else if (Caret > 0) { Anchor = ctrl ? WordLeft(Caret) : Caret - 1; Replace("", now); }
                return true;
            case 0x2E: // delete
                if (HasSel) Replace("", now);
                else if (Caret < Text.Length) { Anchor = ctrl ? WordRight(Caret) : Caret + 1; Replace("", now); }
                return true;
            case 0x0D: // enter
                if (Multiline && !ctrl) { Replace("\n", now); return true; }
                Submit?.Invoke(); return true;
            case 0x1B: Escape?.Invoke(); return true;
            case 0x41 when ctrl: Anchor = 0; Caret = Text.Length; return true;                    // ctrl+A
            case 0x43 when ctrl: if (HasSel) Native.Win32.SetClipboardText(Text[Sel.a..Sel.b]); return true;
            case 0x58 when ctrl: if (HasSel) { Native.Win32.SetClipboardText(Text[Sel.a..Sel.b]); Replace("", now); } return true;
            case 0x56 when ctrl: { var s = Native.Win32.GetClipboardText(); if (!string.IsNullOrEmpty(s)) Replace(s, now); } return true;
            case 0x5A when ctrl: if (undo.Count > 0) { var (t, c) = undo.Pop(); Text = t; Caret = Anchor = Math.Min(c, t.Length); Changed?.Invoke(); } return true;
        }
        return false;
    }

    public void ClickAt(int index, bool shift) { Caret = Math.Clamp(index, 0, Text.Length); if (!shift) Anchor = Caret; }
    public void SelectTo(int index) { Caret = Math.Clamp(index, 0, Text.Length); }

    /// <summary>Draws the field; returns the text layout's index hit test for clicks.</summary>
    public void Draw(Canvas c, Box b, float size, bool focused, double now, Color4 caretColor)
    {
        var fmt = c.Format(size, FontWeight.Normal, null, false, HAlign.Left, Multiline, false);
        bool empty = Text.Length == 0;
        var layout = c.Layout(empty ? Placeholder : Text, fmt, Multiline ? b.W : 100000, Multiline ? b.H : 1000);
        var m = layout.Metrics;
        float y = Multiline ? b.Y : b.CY - m.Height / 2;
        float scroll = 0;
        if (!empty && !Multiline)
        {
            var cp = layout.HitTestTextPosition((uint)Caret, false, out Vector2 pt);
            if (pt.X > b.W - 4) scroll = pt.X - b.W + 4;
        }
        c.ClipRect(b.Inset(-2, -2));
        if (!empty && HasSel)
        {
            var (a, bb) = Sel;
            for (int i = a; i < bb; i++)
            {
                var hm = layout.HitTestTextPosition((uint)i, false, out Vector2 p0);
                c.Fill(new Box(b.X + p0.X - scroll, y + hm.Top, Math.Max(2, hm.Width), hm.Height), caretColor.A(0.35f));
            }
        }
        c.C.DrawTextLayout(new Vector2(b.X - scroll, y), layout, c.Brush(empty ? Col.White.A(0.38f) : Col.White), Vortice.Direct2D1.DrawTextOptions.EnableColorFont);
        if (focused)
        {
            var hm = layout.HitTestTextPosition((uint)(empty ? 0 : Caret), false, out Vector2 cp);
            float blink = (float)(0.5 + 0.5 * Math.Cos((now - lastTypeTime) * Math.PI * 2 / 1.06));
            if (now - lastTypeTime < 0.5) blink = 1;
            c.Fill(new Box(b.X + cp.X - scroll, y + hm.Top + 1, 1.6f, Math.Max(size, hm.Height) - 2), caretColor.A(0.3f + 0.7f * blink), 0.8f);
        }
        c.Unclip();
        lastLayout = layout; lastOrigin = new Vector2(b.X - scroll, y);
    }

    Vortice.DirectWrite.IDWriteTextLayout lastLayout; Vector2 lastOrigin;
    public int IndexAt(float x, float y)
    {
        if (lastLayout == null || Text.Length == 0) return 0;
        var hm = lastLayout.HitTestPoint(x - lastOrigin.X, y - lastOrigin.Y, out var trailing, out _);
        return (int)hm.TextPosition + (trailing ? 1 : 0);
    }
    public void Touch(double now) => lastTypeTime = now;
}
