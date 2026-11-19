using System.Numerics;
using Isola.App;
using Isola.Render;
using Isola.Services;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Box = Isola.Render.Box;

namespace Isola.Island;

public enum Mode
{
    Hello,      // startup greeting
    Rest,       // idle pill, or compact live activity when something is going on
    Peek,       // hover: the medium card
    Expanded,   // click: the dashboard
    Search,     // Spotlight-style search (keyboard)
    Alert,      // transient banner
    Hud,        // transient volume / caps-lock bar
    Hidden,     // auto-hidden after inactivity: slid up, indicator left behind
    Off,        // fullscreen on the laptop, or no laptop screen: nothing at all
}

/// <summary>What the island needs from the windows that host it (fakeable in tests).</summary>
public interface IIslandHost
{
    float WindowWidth { get; }
    float WindowHeight { get; }
    void RequestFrame();
    /// <summary>Where the island takes input (DIPs, window coordinates). Empty = none.</summary>
    void SetHitArea(Box island, float radius);
    /// <summary>Show or hide the host windows entirely (Off saves all composition work).</summary>
    void SetShown(bool shown);
    /// <summary>Take or release keyboard focus for typing.</summary>
    void TakeKeyboard(bool on);
    void SetCursor(CursorKind k);
    /// <summary>Cursor in window DIPs, and whether it is on the laptop screen at all.</summary>
    (float x, float y, bool onScreen) Cursor();
    void OpenSettings(string section = null);
    void ShowContextMenu(float x, float y);
    void StartFileDrag(IReadOnlyList<string> paths);
    void StartTextDrag(string text);
    /// <summary>A mouse button is held right now (dragging a window or selecting under the island).</summary>
    bool MouseButtonDown => false;
}

/// <summary>A piece of content the island can show. Pages are cheap; views swap them.</summary>
public abstract class Page
{
    public abstract string Key { get; }
    /// <summary>Desired size (DIPs) and corner radius for this content.</summary>
    public abstract (float w, float h, float r) Size(Island isl);
    /// <param name="t">entrance progress 0..1 (content cross-fade)</param>
    public abstract void Draw(Island isl, Canvas c, Ux ux, Box area, float t);
    public virtual bool WantsContinuousFrames(Island isl) => false;
    /// <summary>Lower frame rate for ambient motion (waveform, caret blink), seconds per frame; 0 = none.</summary>
    public virtual double AmbientInterval(Island isl) => 0;
    public virtual bool OnKey(Island isl, int vk, bool ctrl, bool shift) => false;
    public virtual bool OnChar(Island isl, char ch) => false;
    public virtual void Shown(Island isl) { }
    public virtual void Hidden(Island isl) { }
    public virtual bool WantsKeyboard => false;
}

public sealed partial class Island : ITouchTarget
{
    public Config Cfg;
    public readonly IIslandHost Host;
    public readonly IClock Clock;
    public readonly Ux Ux = new();
    public Services.Hub Svc;   // null in tests that do not need services
    public Action SettingsChanged;   // a command changed Cfg; persist it

    public Mode Mode { get; private set; } = Mode.Off;
    Mode modeBeforeTransient = Mode.Rest;   // where an alert/HUD returns to
    bool hiddenBeforeTransient;

    // geometry springs (DIPs)
    readonly Spring W, H, R, Y, styleT, presence, indicator, glowAmt, squish, lean, pull, reactive;
    public float CX => Host.WindowWidth / 2;
    public const float PillGap = 8;   // floats clear of the bezel, like the PROLED island

    // content
    Page page, prevPage;
    readonly Spring xfade;
    readonly Spring prevFade;
    Box prevArea;              // where the outgoing page was laid out: it fades in place
    double incomingAt;         // the incoming page waits a beat for the outgoing one to clear
    static readonly SpringSpec ExitSpec = new(0.14, 1.0), EnterSpec = new(0.30, 1.0);
    public Page CurrentPage => page;

    // pages
    public readonly Pages P;

    // state
    public bool Fullscreen { get; private set; }
    public bool LaptopPresent { get; private set; } = true;
    public bool Hovering { get; private set; }
    public bool KeyboardTaken { get; private set; }
    bool helloDone;
    double lastActivity;
    long hoverTimer, collapseTimer, autoHideTimer, transientTimer, helloTimer;
    readonly List<Alert> alertQueue = new();
    public Alert CurrentAlert { get; private set; }
    public (HudKind kind, float value, string label) CurrentHud { get; private set; }
    public int Tab;   // dashboard tab index
    public string LastModeReason { get; private set; } = "";

    // glow colors (lerped toward targets)
    readonly Color4[] glow = { Col.Hex(0x14d4c4), Col.Hex(0x2f6bff), Col.Hex(0x8b5cf6) };
    Color4[] glowTarget = { Col.Hex(0x14d4c4), Col.Hex(0x2f6bff), Col.Hex(0x8b5cf6) };

    public Island(Config cfg, IIslandHost host, IClock clock)
    {
        Cfg = cfg; Host = host; Clock = clock;
        W = new Spring(8, SpringSpec.Expand, 0.05); H = new Spring(8, SpringSpec.Expand, 0.05);
        R = new Spring(4, SpringSpec.Expand, 0.05); Y = new Spring(-60, SpringSpec.Expand, 0.05);
        styleT = new Spring(cfg.Style == "notch" ? 1 : 0, SpringSpec.Smooth, 0.002);
        presence = new Spring(0, SpringSpec.Smooth, 0.002);
        indicator = new Spring(0, SpringSpec.Smooth, 0.002);
        glowAmt = new Spring(0, SpringSpec.Slow, 0.002);
        squish = new Spring(0, SpringSpec.Press, 0.001);
        lean = new Spring(0, SpringSpec.Snappy, 0.01);
        pull = new Spring(0, SpringSpec.Wobbly, 0.05);
        reactive = new Spring(0, new SpringSpec(0.6, 1.0), 0.002);
        xfade = new Spring(1, SpringSpec.Content, 0.002);
        prevFade = new Spring(0, SpringSpec.Content, 0.002);
        Ux.RequestFrame = () => Host.RequestFrame();
        Ux.Now = () => Clock.Now;
        Ux.ReduceMotion = cfg.ReduceMotion;
        P = new Pages(this);
        lastActivity = Clock.Now;
    }

    // ====================================================================
    // lifecycle
    // ====================================================================

    // The Home tab shows only while Home Assistant is reachable, and the tab
    // itself polls only while it's open. Without something else asking, a
    // closed island never learned Home Assistant was there (at start, or back
    // after an outage), so the tab never appeared. One request a minute while
    // the open Home tab isn't polling; a quick TCP probe fails fast away from home.
    long haWatch;
    void WatchHa()
    {
        haWatch = Ui.After(60, WatchHa);
        if (!Cfg.HomeAssistant || Svc?.Ha is not { Configured: true } ha || ha.Busy) return;
        bool tabPolling = Mode == Mode.Expanded && P.Dashboard.CurrentTabId == "home";
        if (!tabPolling && ha.Stale(50)) _ = ha.RefreshAsync();
    }

    public void Start()
    {
        Ui.Cancel(ref haWatch);
        haWatch = Ui.After(1.0, WatchHa);
        Log.I("island", $"start: style={Cfg.Style} hello={Cfg.HelloOnLaunch} fullscreen={Fullscreen} laptop={LaptopPresent}");
        if (Fullscreen || !LaptopPresent) { SetMode(Mode.Off, "start blocked"); return; }
        if (Cfg.HelloOnLaunch && !helloDone)
        {
            helloDone = true;
            // start as a tiny dot just below the top edge, then bloom
            W.Snap(10); H.Snap(10); R.Snap(5); Y.Snap(0);
            SetMode(Mode.Hello, "launch");
            helloTimer = Ui.After(3.4, () => { if (Mode == Mode.Hello) SetMode(Mode.Rest, "hello done"); });
        }
        else SetMode(Mode.Rest, "launch");
    }

    public void ApplyConfig(Config c)
    {
        Cfg = c;
        styleT.To(c.Style == "notch" ? 1 : 0);
        Ux.ReduceMotion = c.ReduceMotion;
        ScheduleAutoHide();
        Retarget();
    }

    // ====================================================================
    // mode machine
    // ====================================================================

    public void SetMode(Mode m, string why)
    {
        var old = Mode;
        if (InlineAlert != null && m is not (Mode.Expanded or Mode.Search)) EndInline("island closed");   // it was seen
        if (old == m && m != Mode.Alert && m != Mode.Hud) { Retarget(); return; }
        LastModeReason = why;
        Log.D("island", $"mode {old} -> {m} ({why})");
        if (old == Mode.Hello || m == Mode.Hello) Ui.Post(UpdateGlowColors);
        if (m != Mode.Peek && m != Mode.Expanded && m != Mode.Search) Ui.Cancel(ref collapseTimer);
        if (old == Mode.Search && m != Mode.Search) ReleaseKeyboard();
        // An alert's sound belongs to the alert being on screen. Leaving it any
        // other way than EndTransient (opened, hovered into, hidden for a
        // fullscreen app) used to leave a ringtone looping with nothing showing,
        // and the alert's timer then skipped its cleanup: it rang forever.
        if (old == Mode.Alert && m != Mode.Alert && CurrentAlert != null && SoundOff != null)
        {
            var silenced = CurrentAlert;
            Ui.Guard("alert sound", () => SoundOff(silenced));
        }
        Mode = m;
        if (m == Mode.Off) { Host.SetHitArea(default, 0); }
        else Host.SetShown(true);
        if (m == Mode.Expanded || m == Mode.Peek) MarkActive();
        Retarget();
        ScheduleAutoHide();
    }

    /// <summary>The page the current mode shows.</summary>
    Page PageFor(Mode m) => m switch
    {
        Mode.Hello => P.Hello,
        Mode.Rest => P.Compact.HasActivity(this) ? P.Compact : P.Idle,
        Mode.Peek => P.Peek,
        Mode.Expanded => P.Dashboard,
        Mode.Search => P.Search,
        Mode.Alert => AlertPageNow(),
        Mode.Hud => P.Hud,
        _ => null,
    };

    // Two alert pages take turns: a notice replacing another crossfades from the old one, instead of
    // the old page redrawing with the new text while it faded (it looked like the text just changed).
    string alertPageFor; bool alertFlip;
    internal Page ShownPage => PageFor(Mode);
    Page AlertPageNow()
    {
        string k = CurrentAlert?.Key ?? "";
        if (k != alertPageFor) { alertPageFor = k; alertFlip = !alertFlip; }
        return alertFlip ? P.AlertPage2 : P.AlertPage;
    }

    /// <summary>Opens the Shelf tab on its Downloads panel (after starting one from search, or the "downloads" command).</summary>
    public void ShowDownloads()
    {
        P.Dashboard.OpenDownloads();
        Expand(P.Dashboard.TabIndex("shelf"));
    }

    /// <summary>Recompute targets: which page, what size, where.</summary>
    string glowKeyFor;

    /// <summary>
    /// The height's spring for growing <paramref name="by"/> DIPs. The bounce is a share of the distance, so opening
    /// onto a tall tab swelled 15 px past it and settled back (it looked too big, then shrank): a long way up is
    /// damped to overshoot by about 4 px, while short moves keep the bounce of Apple's (and the width keeps it all).
    /// </summary>
    internal static SpringSpec HeightSpec(SpringSpec spec, double by)
    {
        if (by <= 60 || spec.Damping >= 1) return spec;
        double f = Math.Min(Math.Exp(-spec.Damping * Math.PI / Math.Sqrt(1 - spec.Damping * spec.Damping)), 4 / by);
        double lf = Math.Log(f);
        return new SpringSpec(spec.Response, Math.Max(spec.Damping, -lf / Math.Sqrt(Math.PI * Math.PI + lf * lf)));
    }

    public void Retarget()
    {
        // Hidden/Off keep whatever was showing, so it slides away with the body
        // instead of blinking out before the shape has moved.
        var np = Mode is Mode.Hidden or Mode.Off ? page : PageFor(Mode);
        if (np != page)
        {
            if (page != null && np != null)
            {
                // Apple's morph: the old content leaves quickly and in place
                // (it does not ride the shape), the new one arrives just behind
                // it as the shape settles - so the two never sit on top of each other.
                prevPage = page; prevArea = Shape; prevFade.Snap(xfade.Value); prevFade.To(0, ExitSpec);
                Ux.ClearZones();
                page.Hidden(this);
            }
            page = np;
            if (page != null)
            {
                xfade.Snap(0);
                xfade.To(1, EnterSpec);
                incomingAt = Clock.Now + (prevPage != null ? 0.06 : 0);
                page.Shown(this);
                if (page.WantsKeyboard) TakeKeyboard(); else if (KeyboardTaken && Mode != Mode.Search) ReleaseKeyboard();
            }
        }
        if (page?.Key != glowKeyFor) { glowKeyFor = page?.Key; UpdateGlowColors(); }
        bool shown = Mode is not (Mode.Hidden or Mode.Off);
        var spec = SpringSpec.Expand;
        if (page != null && shown)
        {
            var (w, h, r) = page.Size(this);
            // shrinking uses the calmer collapse spring, as in Apple's footage
            spec = (w * h < W.Target * H.Target) ? SpringSpec.Collapse : SpringSpec.Expand;
            if (Cfg.ReduceMotion) spec = SpringSpec.Reduced;
            W.To(w, spec); H.To(h, HeightSpec(spec, h - H.Value)); R.To(r, spec);
        }
        float hiddenY = -(float)(H.Target + PillGap + 14);
        Y.To(shown ? 0 : hiddenY, shown ? (Cfg.ReduceMotion ? SpringSpec.Reduced : new SpringSpec(0.5, 0.74)) : new SpringSpec(0.42, 1.0));
        presence.To(Mode == Mode.Off ? 0 : 1);
        indicator.To(Mode == Mode.Hidden && Cfg.ShowIndicatorWhenHidden ? 1 : 0);
        glowAmt.To(GlowTarget());
        Host.RequestFrame();
    }

    double GlowTarget()
    {
        if (!Cfg.Glow || Mode is Mode.Hidden or Mode.Off) return 0;
        double k = Cfg.GlowIntensity;
        return Mode switch { Mode.Rest => 0.75 * k, Mode.Peek => 0.9 * k, Mode.Hello => 1.0 * k, Mode.Alert => 0.85 * k, Mode.Hud => 0.6 * k, _ => 0.55 * k };
    }

    /// <summary>Something happened that counts as "the island is in use".</summary>
    public void MarkActive() { lastActivity = Clock.Now; ScheduleAutoHide(); }

    void ScheduleAutoHide()
    {
        Ui.Cancel(ref autoHideTimer);
        if (Cfg.AutoHideMinutes <= 0 || Mode != Mode.Rest) return;
        double due = lastActivity + Cfg.AutoHideMinutes * 60 - Clock.Now;
        autoHideTimer = Ui.After(Math.Max(0.05, due), () =>
        {
            if (Mode == Mode.Rest && !Hovering && Clock.Now - lastActivity >= Cfg.AutoHideMinutes * 60 - 0.01)
                SetMode(Mode.Hidden, $"no activity for {Cfg.AutoHideMinutes:0.#} min");
            else ScheduleAutoHide();
        });
    }

    // ---- external state ---------------------------------------------------

    public void SetFullscreen(bool on, string what)
    {
        if (Fullscreen == on) return;
        Fullscreen = on;
        Log.I("island", on ? $"fullscreen on the laptop ({what}) - stepping aside" : $"fullscreen ended ({what})");
        if (!Cfg.HideInFullscreen) return;
        if (on) { if (Mode != Mode.Off) { wasHiddenBeforeOff = Mode == Mode.Hidden; SetMode(Mode.Off, "fullscreen"); } }
        else if (Mode == Mode.Off && LaptopPresent) SetMode(wasHiddenBeforeOff ? Mode.Hidden : Mode.Rest, "fullscreen ended");
    }
    bool wasHiddenBeforeOff;

    public void SetLaptopPresent(bool on)
    {
        if (LaptopPresent == on) return;
        LaptopPresent = on;
        Log.I("island", on ? "laptop screen is back" : "laptop screen is off or not part of the desktop - island off");
        if (!on) SetMode(Mode.Off, "no laptop screen");
        else if (!Fullscreen || !Cfg.HideInFullscreen) { if (!helloDone && Cfg.HelloOnLaunch) Start(); else SetMode(Mode.Rest, "laptop screen back"); }
    }

    public bool Blocked => (Fullscreen && Cfg.HideInFullscreen) || !LaptopPresent;
    /// <summary>Whether a notification would actually appear (mirrors the check in Alert).</summary>
    public bool CanShowAlerts => !Blocked || (Cfg.AlertsOverFullscreen && Fullscreen && LaptopPresent);

    /// <summary>Hotkey: show if hidden, hide if shown.</summary>
    public void Toggle()
    {
        if (Blocked) return;
        if (Mode == Mode.Hidden) { MarkActive(); SetMode(Mode.Rest, "toggle"); }
        else { RevealSuppressed = true; SetMode(Mode.Hidden, "toggle"); }
    }

    public void Reveal(string why)
    {
        if (Mode != Mode.Hidden) return;
        MarkActive();
        SetMode(Mode.Rest, why);
    }

    // ---- live activity hooks ------------------------------------------------

    /// <summary>
    /// What the island shows changed (another app's media, a download starting). At rest that can change the
    /// page; open, it can change the height: switching to Spotify adds its volume row, and without re-sizing
    /// the island squeezed the row against the controls until it was closed and opened again.
    /// </summary>
    public void ActivityChanged()
    {
        if (Mode is Mode.Rest or Mode.Peek or Mode.Expanded) Retarget();
        else Host.RequestFrame();
    }
    internal double TargetHeight => H.Target;

    public void TrackChanged(MediaInfo old, MediaInfo now)
    {
        UpdateGlowColors();
        Ux.Kick("trackbump", 1);
        if (Blocked) return;
        if (Mode == Mode.Rest) { Retarget(); squish.Kick(-2.2); }
        if (Cfg.ShowOnTrackChange && now != null && now.Playing && !string.IsNullOrEmpty(now.Title))
        {
            // announced only once it has settled (see Announce)
            Ui.Cancel(ref trackTimer);
            trackTimer = Ui.After(TrackSettle, Announce);
        }
    }

    // A website autoplaying silent previews (hovering videos on a profile page)
    // opens and closes a media session every few hundred milliseconds, and
    // "what's playing" flipped between it and Spotify each time: every flip was
    // a popup, the site's placeholder title included, Spotify's track again and
    // again. A track is now announced only once it has played for a moment,
    // never as a site's placeholder, once per track in a while, and with a
    // gap between announcements.
    internal const double TrackSettle = 1.5, TrackGap = 8, TrackForget = 600;
    long trackTimer;
    double lastTrackAt = double.NegativeInfinity;
    readonly Dictionary<string, double> announced = new();

    internal static bool IsPlaceholder(MediaInfo m) => m == null || m.IsPlaceholder;

    void Announce()
    {
        trackTimer = 0;
        var m = Svc?.Media?.Current;
        double now = Clock.Now;
        if (m == null || !m.Playing || IsPlaceholder(m)) return;
        // hidden or resting (the pill has one line: the popup shows the artists in full, then tucks away);
        // open or hovered, the track is already on screen
        if (Mode is not (Mode.Hidden or Mode.Rest) || Blocked) return;
        if (now - lastTrackAt < TrackGap) return;
        string key = m.AppName + "|" + m.Title + "|" + m.Artist;   // not the session: a reopened session is the same song
        if (announced.TryGetValue(key, out double at) && now - at < TrackForget) return;
        foreach (var old in announced.Where(kv => now - kv.Value >= TrackForget).Select(kv => kv.Key).ToList()) announced.Remove(old);
        announced[key] = now; lastTrackAt = now;
        // peek out with the new track, then tuck away again
        Alert(App.Alerts.Track(m.Title, m.Artist, m.Art, m.Palette?.Length > 0 ? m.Palette[0] : Col.Hex(0x8b5cf6)));
    }

    public void UpdateGlowColors()
    {
        // hiding (or off), the glow keeps its colors while it fades with the island: re-picked as it slid away,
        // the song's colors turned to the theme's mid-slide. They're picked again when it comes back.
        if (Mode is Mode.Hidden or Mode.Off) return;
        Color4[] t = PaletteFor(Cfg.GlowColors);
        var m = Svc?.Media?.Current;
        // paused still counts: the cover is still what the island is about
        // (until nothing has played for ten minutes); hello always gets aurora
        bool mediaRecent = m != null && (m.Playing || Clock.Now - Svc.Media.LastPlayingAt < 600);
        // a notification or call on screen lends the glow its app's colors;
        // when it goes, the glow returns to the music (whatever is playing by then)
        var alertPalette = Mode == Mode.Alert ? CurrentAlert?.Palette : null;
        if (Cfg.GlowColors == "auto" && alertPalette is { Length: > 0 }) t = Three(alertPalette);
        else if (Cfg.GlowColors == "auto" && Mode != Mode.Hello && mediaRecent && ShowsMedia() && m.Palette is { Length: > 0 }) t = Three(m.Palette);
        glowTarget = t;
        Host.RequestFrame();
    }

    /// <summary>
    /// The island is showing the media: the pill with the song, the card on hover, or the island opened. Resting
    /// on its eyes (or the plain island) it isn't, and the glow returns to the theme's colors: a paused song's
    /// colors stayed on the eyes.
    /// </summary>
    internal bool ShowsMedia() => Mode switch
    {
        Mode.Peek or Mode.Expanded or Mode.Search => true,
        // a volume HUD over the rest keeps what the rest would have; a notice or a call is its own moment: its
        // app's colors if it brings them, the theme's if not (it had the song's), then back to the song when it goes
        Mode.Rest or Mode.Hud => P.Compact.HasActivity(this) && P.Compact.Kind == "media",
        Mode.Alert => CurrentAlert?.Kind == "track",   // a new song's popup is the media: its colors stay
        _ => false,
    };

    static Color4[] Three(Color4[] p) => new[] { p[0], p.Length > 1 ? p[1] : p[0], p.Length > 2 ? p[2] : (p.Length > 1 ? p[1] : p[0]) };

    /// <summary>Colors for an alert's glow: from its picture, for the kinds whose picture is an app icon or a caller.</summary>
    static void PaletteForAlert(Alert a)
    {
        if (a.Palette != null || a.Image == null || a.Kind is not ("notification" or "call")) return;
        try { a.Palette = Services.MediaService.PaletteOf(a.Image) ?? Array.Empty<Color4>(); }
        catch { a.Palette = Array.Empty<Color4>(); }
    }

    /// <summary>The color the first glow blob is heading to (tests read it).</summary>
    internal Color4 GlowTarget0 => glowTarget[0];

    public static Color4[] PaletteFor(string name) => name switch
    {
        "sunset" => new[] { Col.Hex(0xff8a3d), Col.Hex(0xff3d77), Col.Hex(0xa23dff) },
        "ocean" => new[] { Col.Hex(0x00c2ff), Col.Hex(0x0066ff), Col.Hex(0x00ffc6) },
        "mono" => new[] { Col.Hex(0x9aa4b2), Col.Hex(0xe6e9ef), Col.Hex(0x9aa4b2) },
        _ => new[] { Col.Hex(0x14d4c4), Col.Hex(0x2f6bff), Col.Hex(0x8b5cf6) },   // aurora: the teal-blue-violet of the reference
    };

    // ---- transient: alerts and HUDs ----------------------------------------

    // A burst of notifications must neither flood the island for minutes nor
    // lose anything: while others wait, each shows briefly; past the newest few,
    // or after waiting a minute, they fold into one "N more notifications".
    internal const int BacklogLimit = 4;
    internal const double BriskSeconds = 3, StaleAfter = 60;
    public Action OpenNotificationCenter;
    /// <summary>Settings' "Preview popups", reachable from search.</summary>
    public Action PreviewPopups;
    /// <summary>An alert came on screen / left it: the app plays its sound, and stops a looping one.</summary>
    public Action<Alert> SoundOn, SoundOff;

    // ---- a notice while the island is open: a banner at its top -------------------------------
    public Alert InlineAlert { get; private set; }
    long inlineTimer;
    Box inlineBox;

    void ShowInline(Alert a)
    {
        InlineAlert = a;
        PaletteForAlert(a);
        Ux.Forget("inline:in");
        Log.D("alert", $"{a.Kind} as a banner (the island is open)");
        if (SoundOn != null) Ui.Guard("alert sound", () => SoundOn(a));
        if (a.Shown != null) Ui.Guard("alert shown", a.Shown);
        ArmInline(Math.Min(a.Seconds, 5));
        Host.RequestFrame();
    }

    // it waits while the pointer is on it, as a notice does
    void ArmInline(double seconds)
    {
        Ui.Cancel(ref inlineTimer);
        inlineTimer = Ui.After(seconds, () => { if (Ux.PointerIn(inlineBox)) ArmInline(1.5); else EndInline("timeout"); });
    }

    public void EndInline(string why)
    {
        var a = InlineAlert;
        if (a == null) return;
        InlineAlert = null;
        Ui.Cancel(ref inlineTimer);
        Log.D("alert", $"banner {a.Kind} gone ({why})");
        if (SoundOff != null) Ui.Guard("alert sound", () => SoundOff(a));
        Host.RequestFrame();
        Ui.Post(PumpAlerts);   // the next one waiting, if any
    }

    void DrawInline(Canvas c, Box box)
    {
        var al = InlineAlert;
        if (al == null || Mode is not (Mode.Expanded or Mode.Search)) return;
        float a = Ux.AnimFrom("inline:in", 0, 1, SpringSpec.Content);
        var b = new Box(box.X + 12, box.Y + 10 - (1 - a) * 16, box.W - 24, 58);
        inlineBox = b;
        c.Push(null, a);
        c.DropShadow(16, Col.Black.A(0.5f), 6, () => c.Fill(b, Col.Black, 18));
        c.Fill(b, Col.Hex(0x2c2c31), 18);   // a step lighter than the island: it floats above
        c.Stroke(b, Col.White.A(0.1f), 18, 1);
        var ib = new Box(b.X + 11, b.CY - 18, 36, 36);
        var img = Paint.Image(c, "inline:" + al.Key + al.Created, al.Image);
        if (img != null) c.Image(img, ib, 1, al.Kind == "call" ? 18 : 9);
        else
        {
            c.Fill(ib, Col.Legible(al.Accent, 0.22f), al.Kind == "call" ? 18 : 9);
            c.Icon(al.Glyph == '\0' ? Glyph.Bell : al.Glyph, ib.CX, ib.CY, 16, Col.White);
        }
        float tx = ib.Right + 12, tw = b.Right - 14 - tx;
        c.Marquee(al.Title, new Box(tx, b.Y + 9, tw, 20), 13.5f, Col.White, Vortice.DirectWrite.FontWeight.SemiBold, Clock.Now);
        string sub = string.IsNullOrEmpty(al.Detail) ? al.Subtitle : (string.IsNullOrEmpty(al.Subtitle) ? al.Detail : al.Subtitle + " · " + al.Detail);
        c.Marquee(sub?.Replace('\n', ' ') ?? "", new Box(tx, b.Y + 30, tw, 18), 12, Col.White.A(0.6f), Vortice.DirectWrite.FontWeight.Medium, Clock.Now);
        c.Pop();
        Ux.Add("inline:alert", b, () => EndInline("dismissed"), 18);   // a click dismisses it
    }

    // ---- what showed this session (the Recent tab) -------------------------------------------
    public readonly List<(Alert alert, DateTime at)> History = new();
    internal const int HistoryMax = 300;

    void Remember(Alert a)
    {
        if (a.Kind is "hud" || a.Key == "updated") return;
        // the same notice updated in place (a download's, a count) is one entry
        if (a.Key != "" && History.Count > 0 && History[^1].alert.Key == a.Key) History[^1] = (a, DateTime.Now);
        else History.Add((a, DateTime.Now));
        if (History.Count > HistoryMax) History.RemoveAt(0);
    }
    public IReadOnlyList<Alert> QueuedAlerts => alertQueue;
    double alertShownAt, transientEndsAt;

    public void Alert(Alert a)
    {
        if (a == null) return;
        a.Created = Clock.Now;
        // notifications and calls carry people's names and words: the log keeps only what kind and from which app
        Log.I("alert", a.Kind is "notification" or "call" or "summary" ? $"{a.Kind} from {a.Subtitle}" : $"{a.Kind}: {a.Title} | {a.Subtitle}");
        if (!CanShowAlerts && a.Priority < 2) { Log.D("alert", "suppressed (island off)"); return; }
        Remember(a);
        alertQueue.RemoveAll(q => q.Key == a.Key && a.Key != "");
        if (CurrentAlert != null && CurrentAlert.Key == a.Key && a.Key != "" && Mode == Mode.Alert)
        {
            CurrentAlert = a; PaletteForAlert(a); UpdateGlowColors(); Ux.Kick("alertbump", 1); squish.Kick(-1.5); ArmTransient(a.Sticky ? a.MaxSeconds : a.Seconds); Retarget(); return;
        }
        Enqueue(a);
        Fold();
        PumpAlerts();
        Hurry();
    }

    /// <summary>Highest priority first; equal priorities keep their order of arrival.</summary>
    void Enqueue(Alert a)
    {
        int i = alertQueue.FindIndex(q => q.Priority < a.Priority);
        if (i < 0) alertQueue.Add(a); else alertQueue.Insert(i, a);
    }

    /// <summary>Folds stale notifications, and all but the newest few, into one summary.</summary>
    void Fold()
    {
        var waiting = alertQueue.Where(q => q.Kind == "notification").ToList();
        var stale = waiting.Where(q => Clock.Now - q.Created > StaleAfter).ToList();
        var fresh = waiting.Except(stale).ToList();
        var fold = stale.Concat(fresh.Take(Math.Max(0, fresh.Count - BacklogLimit))).ToList();
        if (fold.Count == 0) return;
        int at = alertQueue.IndexOf(fold.OrderBy(alertQueue.IndexOf).First());
        foreach (var f in fold) alertQueue.Remove(f);
        var sum = alertQueue.FirstOrDefault(q => q.Kind == "summary");
        if (sum == null)
        {
            sum = new Alert { Key = "notif:summary", Kind = "summary", Glyph = Glyph.Bell, Accent = Col.Hex(0x0a84ff), Seconds = 4, Created = Clock.Now, Sound = Services.Sounds.Notification };
            alertQueue.Insert(Math.Min(at, alertQueue.Count), sum);
        }
        sum.Clicked = OpenNotificationCenter;
        sum.Folded += fold.Count;
        sum.FoldedFrom.AddRange(fold.Select(f => f.Subtitle).Where(s => !string.IsNullOrEmpty(s)));
        var apps = sum.FoldedFrom.Distinct().ToList();
        sum.Title = sum.Folded == 1 ? "1 more notification" : $"{sum.Folded} more notifications";
        sum.Subtitle = apps.Count == 0 ? "" : string.Join(", ", apps.Take(3)) + (apps.Count > 3 ? $" +{apps.Count - 3}" : "");
        sum.Detail = "In the notification center";
        Log.D("alert", $"folded {fold.Count} notification(s) into the summary ({sum.Folded} in all)");
    }

    /// <summary>While others wait, a showing notification makes way after a few seconds.</summary>
    void Hurry()
    {
        if (Mode != Mode.Alert || CurrentAlert == null || CurrentAlert.Sticky || CurrentAlert.Kind is not ("notification" or "summary")) return;
        if (!alertQueue.Any(q => q.Priority <= CurrentAlert.Priority)) return;
        double left = transientEndsAt - Clock.Now, brisk = Math.Max(0.8, BriskSeconds - (Clock.Now - alertShownAt));
        if (brisk < left) ArmTransient(brisk);
    }

    public void DismissAlert(string key)
    {
        alertQueue.RemoveAll(q => q.Key == key);
        // also when the island was opened over it: the call ended, so it's gone
        if (CurrentAlert?.Key == key && Mode != Mode.Hud) EndTransient("dismissed");
    }

    void PumpAlerts()
    {
        Fold();
        if (alertQueue.Count == 0) return;
        var next = alertQueue[0];
        // the island open on a tab or in search: a banner drops in at its top instead of the notice waiting
        // unseen until it closed; hovering the pill isn't being busy (and hovering a notice pauses it anyway)
        if (Mode is Mode.Expanded or Mode.Search && next.Priority < 2)
        {
            if (InlineAlert != null) return;   // one banner at a time; the next follows it
            alertQueue.Remove(next);
            ShowInline(next);
            return;
        }
        if (Mode == Mode.Alert && CurrentAlert != null)
        {
            if (next.Priority <= CurrentAlert.Priority) return;  // let the current one finish
            // Something more urgent: the current alert steps aside and comes back
            // after it. It used to be dropped, never shown again.
            var cut = CurrentAlert;
            int i = alertQueue.FindIndex(1, q => q.Priority <= cut.Priority);
            alertQueue.Insert(i < 0 ? alertQueue.Count : i, cut);
            Log.D("alert", $"{cut.Kind} steps aside for {next.Kind}");
            if (cut.Suspended != null) Ui.Guard("alert suspended", cut.Suspended);
            if (SoundOff != null) Ui.Guard("alert sound", () => SoundOff(cut));
        }
        alertQueue.Remove(next);
        EnterTransient();
        CurrentAlert = next;
        alertShownAt = Clock.Now;
        if (Blocked) Host.SetShown(true);
        SetMode(Mode.Alert, "alert " + next.Kind);
        PaletteForAlert(next);
        UpdateGlowColors();
        squish.Kick(-2.5);
        bool others = next.Kind is "notification" or "summary" && alertQueue.Any(q => q.Priority <= next.Priority);
        ArmTransient(next.Sticky ? next.MaxSeconds : others ? Math.Min(next.Seconds, BriskSeconds) : next.Seconds);
        if (next.Shown != null) Ui.Guard("alert shown", next.Shown);
        if (SoundOn != null) Ui.Guard("alert sound", () => SoundOn(next));
    }

    public void Hud(HudKind kind, float value, string label = null)
    {
        if (Blocked) return;
        if (Mode is Mode.Expanded or Mode.Search or Mode.Hello) return;
        if (Mode == Mode.Alert) return;          // an alert outranks a volume tick
        bool already = Mode == Mode.Hud && CurrentHud.kind == kind;
        CurrentHud = (kind, value, label);
        if (!already) { EnterTransient(); SetMode(Mode.Hud, "hud " + kind); }
        else Host.RequestFrame();
        ArmTransient(kind is HudKind.CapsLock or HudKind.Layout ? 1.6 : 1.8);
    }

    void EnterTransient()
    {
        if (Mode is not (Mode.Alert or Mode.Hud))
        {
            hiddenBeforeTransient = Mode == Mode.Hidden || Mode == Mode.Off;
            modeBeforeTransient = Mode is Mode.Peek ? Mode.Peek : Mode.Rest;
        }
    }

    void ArmTransient(double seconds)
    {
        Ui.Cancel(ref transientTimer);
        transientEndsAt = seconds > 0 ? Clock.Now + seconds : double.PositiveInfinity;
        if (seconds <= 0) return;
        transientTimer = Ui.After(seconds, () => { if (!Hovering) EndTransient("timeout"); else ArmTransient(1.5); });
    }

    public void EndTransient(string why)
    {
        if (Mode is not (Mode.Alert or Mode.Hud))
        {
            // the alert was already off screen (the island was opened over it):
            // it's over all the same, so nothing of it (a sound, its hooks) lingers
            Ui.Cancel(ref transientTimer);
            var stale = CurrentAlert;
            CurrentAlert = null;
            if (stale != null && SoundOff != null) Ui.Guard("alert sound", () => SoundOff(stale));
            if (stale?.Ended != null) Ui.Guard("alert ended", stale.Ended);
            return;
        }
        Ui.Cancel(ref transientTimer);
        var leaving = CurrentAlert;
        CurrentAlert = null;
        if (leaving != null && SoundOff != null) Ui.Guard("alert sound", () => SoundOff(leaving));
        if (leaving?.Ended != null) Ui.Guard("alert ended", leaving.Ended);
        // the ended hook may itself have raised an alert that is now showing:
        // carrying on would put the island straight back to rest over it
        if (CurrentAlert != null && Mode == Mode.Alert) return;
        if (alertQueue.Count > 0) { Mode = Mode.Rest; PumpAlerts(); if (Mode == Mode.Alert) return; }
        if (Blocked) { SetMode(Mode.Off, "transient over, still blocked"); UpdateGlowColors(); return; }
        if (hiddenBeforeTransient && !Hovering) SetMode(Mode.Hidden, "back to hidden after " + why);
        else SetMode(Hovering && modeBeforeTransient == Mode.Peek ? Mode.Peek : Mode.Rest, "transient " + why);
        UpdateGlowColors();   // back to the music's colors (or the default)
    }

    // ---- search / expand ----------------------------------------------------

    public void OpenSearch()
    {
        if (!Cfg.Search) return;
        if (Blocked && !Fullscreen) return;
        MarkActive();
        if (Mode == Mode.Search) { CloseToRest("search toggled"); return; }
        if (Blocked) Host.SetShown(true);
        SetMode(Mode.Search, "search");
        TakeKeyboard();
    }

    public void Expand(int tab = -1)
    {
        if (Blocked) return;
        if (tab >= 0) Tab = tab;
        MarkActive();
        SetMode(Mode.Expanded, "expand");
        squish.Kick(1.8);
    }

    /// <summary>
    /// Development only (ISOLA_SHOWCASE): open a state for screenshots without
    /// input and without taking the keyboard from whatever has it.
    /// "expand:weather", "search:12*4.5", "popups".
    /// </summary>
    internal void Showcase(string what, int tries = 0)
    {
        string head = what.Split(':')[0], arg = what.Contains(':') ? what[(what.IndexOf(':') + 1)..] : "";
        MarkActive();
        if (head == "expand")
        {
            var ids = P.Dashboard.TabIds;
            int t = ids.ToList().IndexOf(arg);
            // a tab that appears once its service connects (Home): wait for it
            if (t < 0 && tries < 20) { Ui.After(0.5, () => Showcase(what, tries + 1)); return; }
            SetMode(Mode.Expanded, "showcase");
            if (t >= 0) Tab = t;
        }
        else if (head == "hud") Hud(arg == "mute" ? HudKind.Mute : HudKind.Volume, 0.62f);
        // "download:<link>": the Shelf, the link typed into its bar as if pasted, then downloaded (a real download)
        else if (head == "download" || head == "audio")
        {
            Showcase("expand:shelf");
            Ui.After(0.9, () => P.Dashboard.ShowcaseLink(arg, head == "audio"));
        }
        // "downloads": the Shelf's Downloads panel
        else if (head == "downloads") ShowDownloads();
        // "rest": just the island at rest (with whatever live activity it has)
        else if (head == "rest") SetMode(Mode.Rest, "showcase");
        // "next": the next track (ShowcaseMedia), for the track change
        else if (head == "next") Svc?.Media?.Next();
        // "ha:light.kitchen": a Home Assistant tile tapped, exactly as a click taps it
        else if (head == "ha" && Svc?.Ha?.Entities.TryGetValue(arg, out var ent) == true) Svc.Ha.Tap(ent);
        else if (head == "search")
        {
            SetMode(Mode.Search, "showcase");
            for (int i = 1; i <= arg.Length; i++) { string part = arg[..i]; Ui.After(0.6 + i * 0.11, () => P.Search.Type(part)); }   // as if typed
        }
    }

    public void CloseToRest(string why)
    {
        ReleaseKeyboard();
        if (Blocked) { SetMode(Mode.Off, why); return; }
        SetMode(Mode.Rest, why);
        PumpAlerts();
    }

    public void TakeKeyboard() { if (!KeyboardTaken) { KeyboardTaken = true; Host.TakeKeyboard(true); } }
    public void ReleaseKeyboard() { if (KeyboardTaken) { KeyboardTaken = false; Host.TakeKeyboard(false); } }

    // ====================================================================
    // input (window DIPs)
    // ====================================================================

    public void MouseMove(float x, float y)
    {
        bool wasHover = Hovering;
        Hovering = true;
        Ux.Move(x, y);
        Host.SetCursor(Ux.CursorAt(x, y));
        Ui.Cancel(ref collapseTimer);
        if (!wasHover) { hoverX = x; hoverY = y; OnEnter(); }
        else if (MathF.Abs(x - hoverX) + MathF.Abs(y - hoverY) > StillRadius && (Mode == Mode.Rest && Cfg.HoverToExpand || Mode == Mode.Hidden && !RevealSuppressed))
        {
            // still traveling: the wait starts again from here
            hoverX = x; hoverY = y;
            if (Mode == Mode.Hidden) ArmReveal(); else ArmPeek();
        }
    }

    // The peek waits for the pointer to settle on the island, not merely to be
    // over it: on the way to a tab or a button behind or beside the island, it
    // passed over for longer than the delay and the island opened in the way.
    internal const float StillRadius = 6;
    float hoverX, hoverY;

    void ArmPeek()
    {
        Ui.Cancel(ref hoverTimer);
        hoverTimer = Ui.After(Cfg.HoverDelayMs / 1000.0, () => { if (Hovering && Mode == Mode.Rest && !Busy()) SetMode(Mode.Peek, "hover"); });
    }

    /// <summary>The hidden island comes back for a pointer resting on its sliver this long, not for one passing over it.</summary>
    internal const double HoverRevealDwell = 0.5;

    void ArmReveal()
    {
        Ui.Cancel(ref hoverTimer);
        hoverTimer = Ui.After(HoverRevealDwell, () => { if (Hovering && Mode == Mode.Hidden && !RevealSuppressed && !Busy()) Reveal("hover"); });
    }

    /// <summary>After a click near the top of the screen (not on the island), hover leaves the island alone this long.</summary>
    internal const double QuietAfterClick = 1.5;
    double quietUntil = double.NegativeInfinity;

    /// <summary>
    /// A mouse button went down near the top of the screen, beside the island: someone is working up
    /// there (tabs, a toolbar, a window's title), so for a moment hovering doesn't open or reveal it.
    /// </summary>
    public void WorkingNearby() { quietUntil = Clock.Now + QuietAfterClick; Ui.Cancel(ref hoverTimer); }

    /// <summary>Hover shouldn't open anything now: a button is held (a drag passing under it) or the user just clicked nearby.</summary>
    bool Busy() => Host.MouseButtonDown || Clock.Now < quietUntil;

    /// <summary>
    /// Set when the user hides the island (drag up, swipe, hotkey). The pointer
    /// is at the top of the screen at that moment, which would reveal it again
    /// at once; it has to leave the top first.
    /// </summary>
    public bool RevealSuppressed;

    void OnEnter()
    {
        MarkActive();
        // the hidden island waits for the pointer to rest on it: passing over the top edge on the way to a tab brought it back
        if (Mode == Mode.Hidden) { if (!RevealSuppressed) ArmReveal(); else Log.D("island", "hover on the indicator ignored: just hidden from here"); return; }
        if (Mode == Mode.Rest && Cfg.HoverToExpand) ArmPeek();
    }

    public void MouseLeave()
    {
        bool was = Hovering;
        Hovering = false;
        // (RevealSuppressed is NOT cleared here: hiding reshapes the input
        // region, which itself sends a leave + enter. It clears when the polled
        // pointer has really left the top strip - see App.PollCursor.)
        Ux.Leave();
        Ui.Cancel(ref hoverTimer);
        if (Ux.Dragging != null) return;
        if (Mode is Mode.Peek or Mode.Expanded)
        {
            if (Mode == Mode.Expanded && KeyboardTaken) return;   // typing a note: stay
            // A second leave for the same departure (Windows' own notice arriving
            // after the polled one) must not restart the countdown.
            if (!was && collapseTimer != 0) return;
            double delay = Cfg.CollapseDelayMs / 1000.0 + (Mode == Mode.Expanded ? 0.35 : 0);
            Ui.Cancel(ref collapseTimer);
            collapseTimer = Ui.After(delay, () => { if (!Hovering && Mode is Mode.Peek or Mode.Expanded) CloseToRest("pointer left"); });
        }
        MarkActive();
    }

    public void MouseDown(float x, float y)
    {
        MarkActive();
        Ui.Cancel(ref hoverTimer);
        if (Ux.Down(x, y)) return;
        squish.Kick(-1.2);
        pressedBackground = true;
    }
    bool pressedBackground;

    public void MouseUp(float x, float y)
    {
        if (Ux.Up(x, y)) { pressedBackground = false; return; }
        if (!pressedBackground) return;
        pressedBackground = false;
        switch (Mode)
        {
            case Mode.Rest: case Mode.Peek: case Mode.Hidden: Expand(); break;
            case Mode.Hud: Expand(); break;
            case Mode.Alert:
                // a click dismisses it (it used to open the app, or the island); its own buttons still act
                EndTransient("dismissed");
                break;
        }
    }

    /// <summary>
    /// Right-click: a control's own right-click first (a shelf item shows its file); otherwise it hides the
    /// island, quicker than dragging it up, or brings a hidden one back.
    /// </summary>
    public void RightClick(float x, float y)
    {
        MarkActive();
        if (Ux.RightUp(x, y)) return;
        switch (Mode)
        {
            case Mode.Hidden: RevealSuppressed = false; Reveal("right-click"); break;
            case Mode.Alert: EndTransient("right-clicked"); break;
            default:
                Log.D("island", "right-click -> hide");
                ReleaseKeyboard(); RevealSuppressed = true; SetMode(Mode.Hidden, "right-click");
                break;
        }
    }

    public void Wheel(float x, float y, float delta, bool horizontal)
    {
        MarkActive();
        if (Ux.WheelAt(x, y, delta, horizontal)) return;
        if (Mode == Mode.Expanded && horizontal) { P.Dashboard.Swipe(this, delta > 0 ? 1 : -1); return; }
    }

    /// <summary>
    /// The island follows a press-and-drag 1:1 through a rubber band (harder to
    /// pull the further it goes), the way the iPhone island can be tugged.
    /// </summary>
    public void DragTo(float dx, float dy)
    {
        Ui.Cancel(ref hoverTimer);
        pull.Snap(Rubber(dy, 140));
        Host.RequestFrame();
    }

    /// <summary>Released: far enough down opens search, up hides; anything less springs back.</summary>
    public void DragEnd(float dx, float dy, float velocityY)
    {
        // judge by where the flick was heading, not just where it stopped
        float projected = dy + velocityY * 0.12f;
        pull.To(0, SpringSpec.Wobbly);
        // the island keeps the speed it was really moving at: the finger's speed through the rubber band's slope there
        pull.Velocity = Math.Clamp(velocityY * RubberSlope(dy, 140), -1500, 1500);
        Ux.CancelPress();
        // a real pull, not a nudge: 10 px up used to hide it, which a shaky click could do
        if (projected > 50 && dy > 12) { Log.D("island", $"dragged down {dy:0} (projected {projected:0}) -> search"); OpenSearch(); }
        else if (dy < -28 || (projected < -40 && dy < -8))
        {
            Log.D("island", $"dragged up {dy:0} (projected {projected:0}) -> hide");
            if (Mode is Mode.Alert) EndTransient("swiped away");
            else { ReleaseKeyboard(); RevealSuppressed = true; SetMode(Mode.Hidden, "dragged up"); }
        }
        Host.RequestFrame();
    }

    static float Rubber(float x, float dim)
    {
        const float c = 0.55f;
        float a = Math.Abs(x);
        return Math.Sign(x) * (a * dim * c) / (dim + c * a);
    }
    /// <summary>How fast the rubber-banded island moves per unit of pointer movement at <paramref name="x"/>.</summary>
    internal static float RubberSlope(float x, float dim)
    {
        const float c = 0.55f;
        float d = dim + c * Math.Abs(x);
        return c * dim * dim / (d * d);
    }
    internal double PullVelocity => pull.Velocity;

    Ux ITouchTarget.TouchUx => Ux;

    /// <summary>An island opened by a tap stays open this long after the last touch (touch has no hover to leave).</summary>
    internal const double TouchIdleClose = 8;

    /// <summary>
    /// A finger lifted. That isn't the pointer leaving: treated as one, an island opened with a tap
    /// closed a second later. It stays until it's left alone for a while, swiped up, or closed.
    /// </summary>
    public void TouchLifted()
    {
        Hovering = false;
        Ux.Leave();   // no hover highlight left behind where the finger was
        Ui.Cancel(ref hoverTimer);
        Ui.Cancel(ref collapseTimer);
        if (Mode is Mode.Peek or Mode.Expanded)
            collapseTimer = Ui.After(TouchIdleClose, () => { if (!Hovering && !KeyboardTaken && Mode is Mode.Peek or Mode.Expanded) CloseToRest("left alone after touch"); });
        MarkActive();
    }

    /// <summary>Touch / pen gesture recognized by the host: a swipe in DIPs (dx, dy) or a tap.</summary>
    public void Swipe(float dx, float dy)
    {
        MarkActive();
        if (Math.Abs(dy) > Math.Abs(dx))
        {
            if (dy > 24 && Mode is Mode.Rest or Mode.Peek or Mode.Hidden) { OpenSearch(); return; }
            if (dy < -24)
            {
                if (Mode is Mode.Expanded or Mode.Peek or Mode.Search) CloseToRest("swipe up");
                else if (Mode == Mode.Rest) { RevealSuppressed = true; SetMode(Mode.Hidden, "swipe up"); }
                else if (Mode == Mode.Alert) EndTransient("swiped away");
                return;
            }
        }
        else if (Math.Abs(dx) > 30)
        {
            if (Mode == Mode.Expanded) { P.Dashboard.Swipe(this, dx < 0 ? 1 : -1); return; }
            var m = Svc?.Media;
            if (m?.Current != null && Mode is Mode.Rest or Mode.Peek) { if (dx < 0) m.Next(); else m.Previous(); lean.Kick(dx < 0 ? -40 : 40); }
        }
    }

    public bool KeyDown(int vk, bool ctrl, bool shift)
    {
        MarkActive();
        if (page != null && page.OnKey(this, vk, ctrl, shift)) { Host.RequestFrame(); return true; }
        if (vk == 0x1B) { CloseToRest("escape"); return true; }
        if (Mode == Mode.Expanded)
        {
            if (vk == 0x09) { P.Dashboard.Swipe(this, shift ? -1 : 1); return true; }
        }
        return false;
    }

    public bool CharIn(char ch)
    {
        if (page != null && page.OnChar(this, ch)) { Host.RequestFrame(); return true; }
        return false;
    }

    /// <summary>The window lost keyboard focus (user clicked elsewhere).</summary>
    public void KeyboardLost()
    {
        if (!KeyboardTaken) return;
        KeyboardTaken = false;
        if (Mode == Mode.Search) CloseToRest("focus lost");
        else if (!Hovering && Mode is Mode.Expanded) CloseToRest("focus lost");
    }

    // ---- files dragged over the island (shelf) ------------------------------
    public bool DragHover { get; private set; }
    public void DragEnter()
    {
        if (!Cfg.Shelf || Blocked) return;
        DragHover = true; MarkActive();
        if (Mode == Mode.Hidden) Reveal("file drag");
        Expand(P.Dashboard.TabIndex("shelf"));
    }
    public void DragLeave() { DragHover = false; Host.RequestFrame(); }
    public void Drop(Shell.DropPayload d)
    {
        DragHover = false;
        var sh = Svc?.Shelf;
        if (sh != null)
        {
            if (d.Files.Count > 0) sh.Add(d.Files);
            else if (d.Url != null) sh.AddLink(d.Url, d.UrlTitle);
            else if (d.Png != null) sh.AddImage(d.Png);
            else if (d.Text != null) sh.AddText(d.Text);
        }
        squish.Kick(2.5);
        Host.RequestFrame();
    }

    // ====================================================================
    // frame
    // ====================================================================

    /// <summary>Advance animations. Returns true while anything still moves.</summary>
    public bool Step(double dt)
    {
        bool m = false;
        m |= W.Step(dt); m |= H.Step(dt); m |= R.Step(dt); m |= Y.Step(dt);
        m |= styleT.Step(dt); m |= presence.Step(dt); m |= indicator.Step(dt); m |= glowAmt.Step(dt);
        m |= squish.Step(dt); m |= lean.Step(dt); m |= pull.Step(dt);
        reactive.To(ReactiveShowing() ? 1 : 0); m |= reactive.Step(dt);
        if (Clock.Now >= incomingAt) m |= xfade.Step(dt); else m = true;
        m |= prevFade.Step(dt);
        if (prevPage != null && prevFade.Settled && prevFade.Value <= 0.001) prevPage = null;
        m |= Ux.Step(dt);
        // glow colors drift toward their targets
        float k = 1 - (float)Math.Exp(-dt * 5);
        for (int i = 0; i < 3; i++)
        {
            var a = glow[i]; var b = glowTarget[i];
            if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 0.004f) { glow[i] = Col.Mix(a, b, k); m = true; }
            else glow[i] = b;
        }
        if (Mode == Mode.Off && !m && presence.Value <= 0.001) Host.SetShown(false);
        return m;
    }

    /// <summary>Which springs are still moving (diagnostics).</summary>
    public string MovingWhat()
    {
        var l = new List<string>();
        void C(string n, Spring sp) { if (!sp.Settled) l.Add($"{n}={sp}"); }
        C("W", W); C("H", H); C("R", R); C("Y", Y); C("style", styleT); C("presence", presence); C("indicator", indicator);
        C("glow", glowAmt); C("squish", squish); C("lean", lean); C("pull", pull); C("xfade", xfade); C("prevFade", prevFade); C("reactive", reactive);
        if (Ux.Step(0)) l.Add("ux:" + Ux.MovingKeys());
        for (int i = 0; i < 3; i++) if (Math.Abs(glow[i].R - glowTarget[i].R) + Math.Abs(glow[i].G - glowTarget[i].G) + Math.Abs(glow[i].B - glowTarget[i].B) > 0.004f) l.Add("glowcolor" + i);
        return string.Join(" ", l);
    }

    public bool NeedsContinuousFrames => page != null && page.WantsContinuousFrames(this) && Mode is not (Mode.Hidden or Mode.Off);
    /// <summary>Seconds between looks at the pointer that the resting face wants (the eyes follow it); infinity for none.</summary>
    public double CursorPollInterval => Mode == Mode.Rest && page is IdlePage idle ? idle.CursorPoll(this) : double.PositiveInfinity;
    public double AmbientInterval
    {
        get
        {
            if (page == null || Mode is Mode.Hidden or Mode.Off) return 0;
            double a = page.AmbientInterval(this);
            // the drifting glow is slow: 15 frames a second is plenty for it
            if (Cfg.Glow && Cfg.GlowDrift && !Cfg.ReduceMotion && glowAmt.Value > 0.01) a = a > 0 ? Math.Min(a, 1 / 15.0) : 1 / 15.0;
            // a spinner or skeleton on screen turns at 30 frames a second
            if (loaderShown) a = a > 0 ? Math.Min(a, 1 / 30.0) : 1 / 30.0;
            // a long title: every frame while it glides (smooth at the display's rate), a few a second while it rests
            if (marqueeShown) { double m = marqueeGliding ? 1 / 240.0 : 1 / 30.0; a = a > 0 ? Math.Min(a, m) : m; }
            return a;
        }
    }

    /// <summary>The island's current rectangle in window DIPs.</summary>
    public Box Shape
    {
        get
        {
            float notch = (float)styleT.Value;
            float sq = (float)squish.Value;           // press/bump: a quick breath of the whole shape
            float w = (float)W.Value + sq * 6, h = (float)H.Value + sq * 2;
            float top = Ease.Lerp(PillGap, 0, notch) is var t ? (float)t : 0;
            // dragging down stretches the island toward the pointer; dragging up lifts it
            float pl = (float)pull.Value;
            float y = top + (float)Y.Value;
            if (pl > 0) { h += pl; w += pl * 0.25f; }
            else y += pl;
            return new Box(CX - w / 2 + (float)lean.Value * 0.15f, y, Math.Max(1, w), Math.Max(1, h));
        }
    }
    public float Radius => (float)Math.Min(R.Value, H.Value / 2);
    public float Presence => (float)presence.Value;

    bool loaderShown;

    bool marqueeShown, marqueeGliding;

    public void Render(Canvas c)
    {
        try { RenderFrame(c); } finally { loaderShown = c.LoaderDrawn; marqueeShown = c.MarqueeMoved; marqueeGliding = c.MarqueeGliding; }
    }

    void RenderFrame(Canvas c)
    {
        Ux.BeginFrame(c);
        var box = Shape;
        float notch = (float)styleT.Value;
        float r = Math.Min(Radius, box.H / 2);
        float vis = (float)Math.Clamp(presence.Value, 0, 1);

        // The hidden-state indicator: a small neutral pill left behind at the top
        // edge. It used to sit on a slightly larger fill in the glow's color, meant
        // as a halo; unblurred, it read as a thin purple outline instead.
        float ind = (float)indicator.Value;
        if (ind > 0.01f)
            c.Fill(Box.Centered(CX, 3.5f, 44, 5), Col.Gray(0.42f, 0.9f * ind), 2.5f);

        if (box.Bottom > 0.5f && vis > 0.002f)
        {
            using var pill = PillGeometry(box, r);
            using var notchGeo = notch > 0.001f ? NotchGeometry(box, r) : null;

            float beat = Beat();
            // the music-reactive boost eases in and out (a hard switch on
            // pause or tab change was a visible flicker)
            float rx = (float)reactive.Value;
            float g = (float)glowAmt.Value * vis * (1 + rx * (0.75f * beat - 0.25f));
            var clip = notch > 0.5f && notchGeo != null ? notchGeo : pill;
            void Chrome()
            {
                // depth: a soft shadow once the island is large enough to float over content
                float shadowAmt = Ease.Clamp01((box.H - 60) / 80) is var sa ? (float)sa : 0;
                if (shadowAmt > 0.01f)
                    c.DropShadow(16, new Color4(0, 0, 0, 0.55f * shadowAmt * vis), 6, () => c.C.FillGeometry(pill, c.Brush(Col.Black)));
                // the body: pure black, so on the OLED it is simply pixels switched off
                if (notch < 0.999f) { c.Push(null, (1 - notch) * vis); c.C.FillGeometry(pill, c.Brush(Col.Black)); c.Pop(); }
                if (notchGeo != null) { c.Push(null, notch * vis); c.C.FillGeometry(notchGeo, c.Brush(Col.Black)); c.Pop(); }
            }
            // The shadow and body (the blurred, expensive part) are cached while
            // the shape is still; the glow is cheap gradients, drawn live so it
            // can drift.
            bool still = W.Settled && H.Settled && R.Settled && Y.Settled && styleT.Settled && presence.Settled && squish.Settled && lean.Settled && pull.Settled;
            string key = still ? $"{box.X:0.0},{box.Y:0.0},{box.W:0.0},{box.H:0.0},{r:0.0},{notch:0.00},{vis:0.00},{Gfx.Generation}" : null;
            if (still && key == chromeKey && chromeBmp != null) c.DrawBitmapAtOrigin(chromeBmp);
            else if (still) { chromeBmp = c.Snapshot(chromeBmp, Chrome); chromeKey = key; c.DrawBitmapAtOrigin(chromeBmp); }
            else { chromeKey = null; Chrome(); }
            if (g > 0.01f) DrawGlow(c, box, r, clip, g, beat);
            // a hairline highlight that catches the edge, like light on glass:
            // clipped to the shape, so only its inner half shows and nothing
            // spills past the island's outline
            if (notch < 0.5f) { c.Push(pill, 1); c.C.DrawGeometry(pill, c.Brush(Col.White.A(0.08f * vis * (1 - notch))), 1.5f); c.Pop(); }

            // content
            c.Push(clip, vis);
            // laid out at the size the island is heading for, and revealed by its shape as it grows: laid out
            // at the moving size, Home's tiles re-flowed (and re-measured their text) every frame of the
            // animation, which hitched, and a list too tall for the half-grown island showed a scroll bar
            var area = new Box(box.CX - (float)W.Target / 2, box.Y, (float)W.Target, (float)H.Target);
            float t = (float)xfade.Value;
            if (prevPage != null)
            {
                float pt = (float)prevFade.Value;
                // keep it centered on the island but at its own size and height
                var pa = new Box(area.CX - prevArea.W / 2, area.Y, prevArea.W, prevArea.H);
                DrawPage(c, prevPage, pa, pt * pt, false);
            }
            if (page != null) DrawPage(c, page, area, t, true);
            c.Pop();
        }
        DrawInline(c, box);
        Ux.EndFrame();
        UpdateHitArea(box, r);
        // what's shown wants another size than the island is heading for (Home Assistant came up while the
        // island was open on its tab, and it kept another tab's height): it re-sizes
        if (page != null && Mode is not (Mode.Hidden or Mode.Off) && !resizeQueued)
        {
            var (tw, th, _) = page.Size(this);
            if (Math.Abs(tw - W.Target) > 0.5 || Math.Abs(th - H.Target) > 0.5) { resizeQueued = true; Ui.Post(() => { resizeQueued = false; Retarget(); }); }
        }
    }

    bool resizeQueued;
    ID2D1Bitmap1 chromeBmp; string chromeKey;
    bool GlowColorsSettled()
    {
        for (int i = 0; i < 3; i++) if (Math.Abs(glow[i].R - glowTarget[i].R) + Math.Abs(glow[i].G - glowTarget[i].G) + Math.Abs(glow[i].B - glowTarget[i].B) > 0.004f) return false;
        return true;
    }

    void DrawPage(Canvas c, Page p, Box area, float t, bool live)
    {
        if (t <= 0.003f) return;
        // Apple's content transition: the incoming page scales up from slightly
        // small, fades and de-blurs as the shape settles; the outgoing one shrinks
        // a touch and blurs away, quickly.
        float s = Ux.ReduceMotion ? 1 : live ? 0.93f + 0.07f * t : 0.96f + 0.04f * t;
        float blur = Ux.ReduceMotion ? 0 : (1 - t) * (live ? 8 : 12);
        var old = c.ScaleAbout(s, area.CX, area.Y + area.H * 0.45f);
        // The outgoing page is never clickable; the incoming one only once it is
        // at least half there - nothing can be pressed by accident mid-morph.
        var ux = live && t >= 0.5f ? Ux : GhostUx(c);
        c.Blurred(blur, () => p.Draw(this, c, ux, area, t), t);
        c.Transform = old;
    }

    Ux shadowUx;
    Ux GhostUx(Canvas c) { shadowUx ??= new Ux { ReduceMotion = true }; shadowUx.BeginFrame(c); return shadowUx; }

    float beatSmooth;
    bool ReactiveShowing()
    {
        var m = Svc?.Media;
        return m?.Current != null && m.Current.Playing && Cfg.MediaWaveform && Cfg.Glow &&
            (Mode == Mode.Peek || (Mode == Mode.Rest && page == P.Compact) || (Mode == Mode.Expanded && P.Dashboard.CurrentTabId == "media"));
    }
    /// <summary>
    /// 0..1 loudness of what's playing, when the media is what the island is
    /// showing (compact, peek, Now Playing). Windows' meter gives loudness, not a
    /// spectrum, so this follows the beat and dynamics rather than frequencies.
    /// </summary>
    float Beat()
    {
        var m = Svc?.Media;
        // a small floor keeps it breathing between beats, but only while sound plays: silent, it lies still
        float lv = ReactiveShowing() ? m.Level : 0;
        float target = ReactiveShowing() && m.Sounding ? Math.Clamp(0.12f + lv * 1.6f, 0, 1) : 0;
        // fast attack, slower release, like a VU needle
        beatSmooth = target > beatSmooth ? beatSmooth + (target - beatSmooth) * 0.6f : beatSmooth + (target - beatSmooth) * 0.12f;
        if (beatSmooth < 0.002f) beatSmooth = 0;
        return beatSmooth;
    }

    /// <summary>Where each glow color sits along the edge (0..1), drifting slowly and independently.</summary>
    /// <summary>
    /// Where the edge gradient's three colors sit: at the pools, but never
    /// closer than <paramref name="gap"/> to each other or the ends. The pools
    /// drift in their own lanes (left, middle, right), so the order never
    /// changes, and clamping keeps every stop moving continuously.
    /// </summary>
    internal static float[] EdgeStops(float[] xs, float gap = 0.2f)
    {
        float lo = 0.06f, hi = 0.94f;
        float p0 = Math.Clamp(xs[0], lo, hi - 2 * gap);
        float p1 = Math.Clamp(xs[1], p0 + gap, hi - gap);
        float p2 = Math.Clamp(xs[2], p1 + gap, hi);
        return new[] { p0, p1, p2 };
    }

    float[] BlobX()
    {
        double now = Cfg.GlowDrift && !Cfg.ReduceMotion ? Clock.Now : 0;
        return new[]
        {
            0.20f + 0.13f * (float)Math.Sin(now * 0.23 + 0.4),
            0.50f + 0.17f * (float)Math.Sin(now * 0.17 + 2.1),
            0.80f + 0.13f * (float)Math.Sin(now * 0.29 + 4.0),
        };
    }

    /// <summary>
    /// The colorful rim: three soft color pools (radial gradients, no blur
    /// effects - cheap enough to redraw every frame) that drift left and right
    /// on their own slow rhythms. Inside the body they glow up from the lower
    /// edge; outside, a thin halo bleeds past it. With media showing, a small
    /// visualizer rises from the edge.
    /// </summary>
    void DrawGlow(Canvas c, Box box, float r, ID2D1Geometry clip, float g, float beat)
    {
        var xs = BlobX();
        float depth = Math.Min(box.H * 1.1f, (40 + box.H * 0.2f) * (1 + 0.45f * beat));
        // inner pools
        c.Push(clip, 1);
        for (int i = 0; i < 3; i++)
        {
            float cx = box.X + box.W * xs[i];
            using var br = c.Radial(new Vector2(cx, box.Bottom + depth * 0.15f), box.W * 0.30f, depth,
                (0, glow[i].A(0.95f * g)), (0.45f, glow[i].A(0.42f * g)), (1, glow[i].A(0)));
            c.Fill(new Box(cx - box.W * 0.30f, box.Bottom - depth, box.W * 0.60f, depth * 1.2f), br);
        }
        // a crisper bright line along the lower edge, colored by where the pools
        // are. Its stops are kept apart (EdgeStops): two pools passing close
        // put two colors a fraction of a pixel apart, a hard edge in the line
        // and in the visualizer bars drawn with it.
        var st = EdgeStops(xs);
        using var edge = c.Linear(new Vector2(box.X, 0), new Vector2(box.Right, 0),
            (0, glow[0].A(0)), (st[0], glow[0].A(0.9f * g)), (st[1], glow[1].A(g)), (st[2], glow[2].A(0.9f * g)), (1, glow[2].A(0)));
        c.Fill(new Box(box.X, box.Bottom - 2.2f, box.W, 2.2f), edge);
        if (beat > 0.01f)
        {
            // a little visualizer: thin bars rising from the lower edge in the
            // artwork's colors, each with its own lively rhythm around the level
            double now = Clock.Now;
            int n = Math.Max(8, (int)(box.W / 7));
            float bw = box.W / n;
            for (int i = 0; i < n; i++)
            {
                double ph = i * 2.399;   // golden-angle spread keeps neighbors out of step
                float wob = (float)(0.5 + 0.5 * Math.Sin(now * (4.2 + (i % 5) * 0.9) + ph)) * (float)(0.6 + 0.4 * Math.Sin(now * 1.7 + ph * 0.5));
                float center = 1 - Math.Abs(i - n / 2f) / (n / 2f) * 0.55f;
                float bh = (3 + 26 * beat * wob * center) * Math.Min(1, box.H / 60f);
                c.Fill(new Box(box.X + i * bw + bw * 0.3f, box.Bottom - bh, bw * 0.4f, bh), edge, bw * 0.2f);
            }
        }
        c.Pop();
        // Everything above is drawn inside the island's shape. There used to be
        // an "outer halo" here too, the pools again just past the lower edge,
        // which left a colored haze hanging under the island on any wallpaper.
    }

    void UpdateHitArea(Box box, float r)
    {
        if (Mode == Mode.Off) { Host.SetHitArea(default, 0); return; }
        if (Mode == Mode.Hidden)
        {
            if (box.Bottom > 10) { Host.SetHitArea(default, 0); return; }   // still sliding away: not a target
            // the indicator itself is clickable; reveal-on-approach is polled
            Host.SetHitArea(Box.Centered(CX, 4, 64, 8), 4);
            return;
        }
        // include the gap above the pill so throwing the cursor to the top edge lands on it
        var hit = new Box(box.X, 0, box.W, Math.Max(4, box.Bottom));
        Host.SetHitArea(hit, r);
    }

    // ---- shapes -------------------------------------------------------------

    public static ID2D1PathGeometry PillGeometry(Box b, float r)
    {
        var g = Gfx.D2DFactory.CreatePathGeometry();
        using var s = g.Open();
        r = Math.Min(r, Math.Min(b.W, b.H) / 2);
        float x0 = b.X, x1 = b.Right, y0 = b.Y, y1 = b.Bottom;
        s.BeginFigure(new Vector2(x0 + r, y0), FigureBegin.Filled);
        s.AddLine(new Vector2(x1 - r, y0));
        Corner(s, x1, y0 + r, r);
        s.AddLine(new Vector2(x1, y1 - r));
        Corner(s, x1 - r, y1, r);
        s.AddLine(new Vector2(x0 + r, y1));
        Corner(s, x0, y1 - r, r);
        s.AddLine(new Vector2(x0, y0 + r));
        Corner(s, x0 + r, y0, r);
        s.EndFigure(FigureEnd.Closed);
        s.Close();
        return g;
    }

    /// <summary>
    /// The classic notch: square top flush with the bezel, rounded bottom
    /// corners, and concave "ears" where it meets the screen edge so it reads as
    /// part of the display rather than a box stuck on it.
    /// </summary>
    public static ID2D1PathGeometry NotchGeometry(Box b, float r)
    {
        var g = Gfx.D2DFactory.CreatePathGeometry();
        using var s = g.Open();
        float ear = Math.Min(10, b.H * 0.35f);
        r = Math.Min(r, Math.Min(b.W / 2, b.H - ear));
        float x0 = b.X, x1 = b.Right, y1 = b.Bottom, top = Math.Min(b.Y, 0) - 1;
        float earTop = b.Y;   // ears hug the screen edge even while sliding
        s.BeginFigure(new Vector2(x0 - ear, top), FigureBegin.Filled);
        s.AddLine(new Vector2(x0 - ear, earTop));
        s.AddArc(new ArcSegment(new Vector2(x0, earTop + ear), new Size(ear, ear), 0, SweepDirection.Clockwise, ArcSize.Small));
        s.AddLine(new Vector2(x0, y1 - r));
        s.AddArc(new ArcSegment(new Vector2(x0 + r, y1), new Size(r, r), 0, SweepDirection.CounterClockwise, ArcSize.Small));
        s.AddLine(new Vector2(x1 - r, y1));
        s.AddArc(new ArcSegment(new Vector2(x1, y1 - r), new Size(r, r), 0, SweepDirection.CounterClockwise, ArcSize.Small));
        s.AddLine(new Vector2(x1, earTop + ear));
        s.AddArc(new ArcSegment(new Vector2(x1 + ear, earTop), new Size(ear, ear), 0, SweepDirection.Clockwise, ArcSize.Small));
        s.AddLine(new Vector2(x1 + ear, top));
        s.EndFigure(FigureEnd.Closed);
        s.Close();
        return g;
    }

    /// <summary>A quarter-circle corner ending at (ex, ey), clockwise.</summary>
    static void Corner(ID2D1GeometrySink s, float ex, float ey, float r)
    {
        s.AddArc(new ArcSegment(new Vector2(ex, ey), new Size(r, r), 0, SweepDirection.Clockwise, ArcSize.Small));
    }
}
