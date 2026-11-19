using System.Numerics;
using Isola.App;
using Isola.Render;
using Isola.Services;
using Vortice.Mathematics;
using Box = Isola.Render.Box;
using FontWeight = Vortice.DirectWrite.FontWeight;

namespace Isola.Island;

/// <summary>
/// The expanded island: a tab strip and one panel per feature. Tabs slide in
/// the direction you moved; the island's height springs to fit each panel.
/// </summary>
public sealed partial class DashboardPage : Page
{
    sealed record TabDef(string Id, string Label, char Glyph, Func<Config, bool> Enabled, float Height);
    static readonly TabDef[] AllTabs =
    {
        new("media", "Now Playing", Glyph.Music, c => c.Media, 292),
        new("sound", "Sound", Glyph.Volume3, c => true, 330),
        new("home", "Home", Glyph.Home, c => c.HomeAssistant, 330),
        new("weather", "Weather", Glyph.Sun, c => c.Weather, 280),
        new("notes", "Notes", Glyph.Note, c => c.Notes, 320),
        new("shelf", "Shelf", Glyph.Folder, c => c.Shelf, 336),
        new("clipboard", "Clipboard", Glyph.Clipboard, c => c.Clipboard, 320),
        new("timer", "Timer", Glyph.Timer, c => c.Timer, 270),
        new("devices", "Devices", Glyph.Bluetooth, c => c.BluetoothAlerts || c.AirPods, 400),
    };
    readonly Island isl;
    List<TabDef> tabs = new();
    int prevTab = -1; int dir = 1;
    readonly TextBox noteBox = new() { Placeholder = "Add a note or to-do…", MaxLength = 400 };
    bool noteFocused;
    readonly TextBox timerBox = new() { Placeholder = "Any length: 7m, 1:30, 1h 20m", MaxLength = 24 };
    bool timerFocused;
    double timerBadAt = -10;
    readonly Dictionary<string, float> scroll = new();   // scroll targets; what's drawn springs toward them
    static readonly SpringSpec ScrollSpring = new(0.28, 1.0);
    double lastMixer = -1, lastHa = -1;
    string copiedFlash; double copiedAt;

    public DashboardPage(Island isl)
    {
        this.isl = isl;
        requestFrame = () => isl.Host.RequestFrame();
        noteBox.Submit = () => { isl.Svc?.Notes?.Add(noteBox.Text); noteBox.Set(""); isl.Ux.Kick("notes:added", 1); };
        noteBox.Escape = () => { noteFocused = false; isl.ReleaseKeyboard(); };
        timerBox.Submit = () => StartTyped();
        timerBox.Escape = () => { timerFocused = false; isl.ReleaseKeyboard(); };
        InitDownloads();
    }

    public override string Key => "dashboard";
    // The Home tab only shows when Home Assistant is set up and reachable: an
    // empty Home tab with "not set up" was most people's first sight of it.
    // Tabs are chosen by position, so when the set changes the island stays on
    // the same tab by name instead of sliding onto its neighbor.
    List<TabDef> Tabs()
    {
        var now = AllTabs.Where(t => t.Enabled(isl.Cfg) && (t.Id != "home" || isl.Svc?.Ha?.Usable == true)).ToList();
        if (tabs != null && !now.Select(t => t.Id).SequenceEqual(tabs.Select(t => t.Id)))
        {
            string was = tabs.ElementAtOrDefault(Math.Clamp(isl.Tab, 0, Math.Max(0, tabs.Count - 1)))?.Id;
            int i = now.FindIndex(t => t.Id == was);
            isl.Tab = i >= 0 ? i : Math.Clamp(isl.Tab, 0, Math.Max(0, now.Count - 1));
        }
        tabs = now;
        return tabs;
    }
    public int TabIndex(string id) => Math.Max(0, Tabs().FindIndex(t => t.Id == id));
    internal IReadOnlyList<string> TabIds => Tabs().Select(t => t.Id).ToList();
    public string CurrentTabId => CurrentId;
    string CurrentId => Tabs().ElementAtOrDefault(Math.Clamp(isl.Tab, 0, Math.Max(0, tabs.Count - 1)))?.Id ?? "media";

    public override (float, float, float) Size(Island i)
    {
        var t = Tabs(); if (t.Count == 0) return (420, 120, 36);
        isl.Tab = Math.Clamp(isl.Tab, 0, t.Count - 1);
        float h = t[isl.Tab].Height;
        if (t[isl.Tab].Id == "home")
        {
            // four rows fit before the grid scrolls (440 clipped the fourth by 8 DIP)
            h = Math.Clamp(104 + HaRows() * 86, 220, 450);
        }
        if (t[isl.Tab].Id == "media" && (isl.Svc?.Media?.Sessions.Count ?? 0) > 1) h += 34;
        if (t[isl.Tab].Id == "media" && SpotifyRow()) h += SpotifyRowH;
        return (620, h, 40);
    }

    /// <summary>Rows the Home grid needs, counting climate tiles as two cells.</summary>
    int HaRows()
    {
        int k = 0;
        foreach (var id in isl.Cfg.HaEntities)
        {
            int span = id.StartsWith("climate.") ? 2 : 1;
            if (k % 4 + span > 4) k += 4 - k % 4;
            k += span;
        }
        return Math.Max(1, (int)Math.Ceiling(k / 4.0));
    }

    public override void Shown(Island i)
    {
        if (CurrentId == "sound") { isl.Svc?.Audio?.RefreshMixerAsync(() => isl.Host.RequestFrame()); lastMixer = isl.Clock.Now; }
        if (CurrentId == "home") RefreshHa(true);
    }
    public override void Hidden(Island i) { noteFocused = false; timerFocused = false; linkFocused = false; convertFor = null; }

    public override double AmbientInterval(Island i) => CurrentId switch
    {
        "media" => isl.Svc?.Media?.Current?.Playing == true ? 1 / 30.0 : 0.5,
        "timer" => isl.Svc?.Timer is { Running: true, Paused: false } ? 0.25 : timerFocused ? 0.5 : 0,
        "sound" => 1.0,
        "home" => 1.0,
        "notes" => noteFocused ? 0.5 : 0,
        // the spinner and the bar of a download in progress (its progress also asks for frames)
        "shelf" => isl.Svc?.Downloads?.ActiveCount > 0 ? 1 / 30.0 : linkFocused ? 0.5 : 0,
        _ => 0,
    };
    public override bool WantsKeyboard => false;

    public void Swipe(Island i, int d)
    {
        var t = Tabs(); if (t.Count == 0) return;
        Select((isl.Tab + d + t.Count) % t.Count);
    }

    void Select(int idx)
    {
        if (idx == isl.Tab) return;
        dir = idx > isl.Tab ? 1 : -1;
        prevTab = isl.Tab;
        isl.Tab = idx;
        isl.Ux.Forget("tabx");
        isl.Ux.ClearZones();
        noteFocused = false; timerFocused = false; linkFocused = false; convertFor = null;
        Shown(isl);
        isl.Retarget();
        isl.MarkActive();
    }

    void RefreshHa(bool force)
    {
        var ha = isl.Svc?.Ha;
        if (ha == null) return;
        if (force || ha.Stale(isl.Cfg.HaRefreshSeconds)) { lastHa = isl.Clock.Now; _ = ha.RefreshAsync(); }
        if (force) haSpinUntil = isl.Clock.Now + MinSpin;
    }

    // A refresh you asked for shows a spinner in place of its button, for at
    // least a beat: a 40 ms refresh would otherwise flash, or show nothing at
    // all and look like the press missed. The automatic ten-second refresh
    // stays silent.
    const double MinSpin = 0.45;
    double haSpinUntil, wxSpinUntil;

    void RefreshButton(Ux ux, Canvas c, string id, float cx, float cy, bool busy, double spinUntil, Action refresh, float t)
    {
        if (busy || isl.Clock.Now < spinUntil) Paint.Spinner(c, cx, cy, 13, Col.White.A(0.7f), isl.Clock.Now, t);
        else ux.IconButton(id, cx, cy, 24, Glyph.Refresh, refresh, null, 11);
    }

    // A service call that takes longer than a moment gets a spinner beside the
    // tile's icon: the tile already flipped (optimistic), this says Home
    // Assistant hasn't confirmed it yet. Quick calls never show one.
    void TileSpinner(Canvas c, HomeAssistant ha, HaEntity e, Box b, float t, bool offline)
    {
        double since = ha.CallingSince(e.Id);
        if (double.IsNaN(since) || isl.Clock.Now - since < 0.25) return;
        bool stateless = e.Domain is "scene" or "script" or "button" or "input_button";
        bool lit = e.On && !e.Unavailable && !offline && !stateless;
        Paint.Spinner(c, b.X + 56, b.Y + 26, 14, lit ? Col.Hex(0x1c1c1e) : Col.White, isl.Clock.Now, t * 0.9f);
    }

    public override bool OnKey(Island i, int vk, bool ctrl, bool shift)
    {
        if (noteFocused) return noteBox.Key(vk, ctrl, shift, isl.Clock.Now);
        if (timerFocused) return timerBox.Key(vk, ctrl, shift, isl.Clock.Now);
        if (linkFocused) return linkBox.Key(vk, ctrl, shift, isl.Clock.Now);
        if (vk == 0x25) { Swipe(i, -1); return true; }
        if (vk == 0x27) { Swipe(i, 1); return true; }
        return false;
    }
    public override bool OnChar(Island i, char ch)
    {
        if (noteFocused) { noteBox.Char(ch, isl.Clock.Now); return true; }
        if (timerFocused) { timerBox.Char(ch, isl.Clock.Now); return true; }
        if (linkFocused) { linkBox.Char(ch, isl.Clock.Now); return true; }
        return false;
    }

    public override void Draw(Island i, Canvas c, Ux ux, Box a, float t)
    {
        var tl = Tabs();
        if (tl.Count == 0) return;
        isl.Tab = Math.Clamp(isl.Tab, 0, tl.Count - 1);
        // ---- tab strip --------------------------------------------------
        // Every tab's width is a spring: the chosen tab grows to show its label
        // while the others glide aside, instead of being shoved in one frame.
        float stripY = a.Y + 14, stripH = 32;
        const float iconW = 38, gap = 4;
        var targetW = tl.Select((tb, idx) => idx == isl.Tab ? iconW + c.Measure(tb.Label, 12.5f, FontWeight.SemiBold).w + 14 : iconW).ToArray();
        var animW = tl.Select((tb, idx) => ux.Anim("tabw:" + tb.Id, targetW[idx], SpringSpec.Snappy, 0.05)).ToArray();
        float tTotal = targetW.Sum() + (tl.Count - 1) * gap, aTotal = animW.Sum() + (tl.Count - 1) * gap;
        float tx0 = a.CX - tTotal / 2 + 18, ax0 = a.CX - aTotal / 2 + 18;
        // search on the left, settings on the right
        ux.IconButton("dash:search", a.X + 34, stripY + stripH / 2, 32, Glyph.Search, () => isl.OpenSearch(), null, 14, isl.Cfg.Search);
        ux.IconButton("dash:settings", a.Right - 34, stripY + stripH / 2, 32, Glyph.Settings, () => isl.Host.OpenSettings(), null, 14);
        // the selection pill chases the chosen tab's live position, so pill and
        // label travel together instead of the pill arriving first
        float selX = ax0 + animW.Take(isl.Tab).Sum() + isl.Tab * gap;
        float sx = ux.Anim("tab:selx", selX, SpringSpec.Snappy), sw = ux.Anim("tab:selw", animW[isl.Tab], SpringSpec.Snappy);
        c.Fill(new Box(sx, stripY, sw, stripH), Col.White.A(0.13f * t), stripH / 2);
        float x = ax0;
        for (int k = 0; k < tl.Count; k++)
        {
            var tb = tl[k]; int idx = k;
            var b = new Box(x, stripY, animW[k], stripH);
            x += animW[k] + gap;
            ux.Add("tab:" + tb.Id, b, () => Select(idx), stripH / 2);
            float h = ux.HoverT("tab:" + tb.Id), p = ux.PressT("tab:" + tb.Id);
            bool sel = k == isl.Tab;
            if (!sel && h > 0.01f) c.Fill(b, Col.White.A(0.07f * h * t), stripH / 2);
            float s = 1 - 0.03f * p;
            c.Icon(tb.Glyph, b.X + iconW / 2, b.CY, 15 * s, Col.White.A((sel ? 1f : 0.55f + 0.35f * h) * t));
            // the label fades in as its tab opens up (and out as it closes)
            float open = Math.Clamp((animW[k] - iconW) / Math.Max(1, c.Measure(tb.Label, 12.5f, FontWeight.SemiBold).w + 14), 0, 1);
            if (open > 0.02f)
            {
                c.ClipRect(b);
                c.Text(tb.Label, new Box(b.X + iconW - 4, b.Y, 200, b.H), 12.5f, Col.White.A(t * open * open), FontWeight.SemiBold);
                c.Unclip();
            }
        }
        var tw = ux.Add("tab:wheel", new Box(a.X, stripY - 6, a.W, stripH + 12), null); tw.Horizontal = true; tw.Wheel = (d, hz) => Swipe(i, (d > 0) != hz ? -1 : 1);

        // ---- panel ------------------------------------------------------
        var panel = new Box(a.X + 22, stripY + stripH + 14, a.W - 44, a.Bottom - (stripY + stripH + 14) - 18);
        // Tab change: each panel is faded as ONE layer, so every button, pill and
        // slider in it fades together (elements that ignore the alpha argument
        // would otherwise stay solid and pop out at the end). The old panel is
        // gone by ~40% of the slide; the new one comes in behind it.
        float tt = ux.AnimFrom("tabx", 0, 1, SpringSpec.Smooth);
        if (prevTab >= 0 && prevTab < tl.Count && tt < 0.999f)
        {
            float outA = (float)Math.Pow(1 - Ease.Range(tt, 0, 0.4), 2);
            if (outA > 0.004f)
            {
                float off = -dir * 46 * tt;
                var old = c.Transform; c.Transform = Matrix3x2.CreateTranslation(off, 0) * old;
                c.ClipRect(panel.Inset(-22, -4));
                var g = Ghost(c);
                int pt = prevTab;
                c.Blurred(6 * tt, () => DrawPanel(tl[pt].Id, c, g, panel, t), outA);
                c.Unclip();
                c.Transform = old;
            }
        }
        else prevTab = -1;
        {
            float inA = prevTab >= 0 ? (float)Ease.Smooth(Ease.Range(tt, 0.12, 1)) : 1;
            float off = prevTab >= 0 ? dir * 46 * (1 - tt) : 0;
            var old = c.Transform; c.Transform = Matrix3x2.CreateTranslation(off, 0) * old;
            c.ClipRect(panel.Inset(-22, -4));
            // the new tab's controls only take clicks once it is mostly in view
            var pux = inA >= 0.5f ? ux : Ghost(c);
            if (inA >= 0.999f) DrawPanel(tl[isl.Tab].Id, c, ux, panel, t);
            else if (inA > 0.004f) { c.Push(null, inA); DrawPanel(tl[isl.Tab].Id, c, pux, panel, t); c.Pop(); }
            c.Unclip();
            c.Transform = old;
        }
    }

    Ux ghost;
    Ux Ghost(Canvas c) { ghost ??= new Ux { ReduceMotion = true }; ghost.BeginFrame(c); return ghost; }

    void DrawPanel(string id, Canvas c, Ux ux, Box p, float t)
    {
        if (t <= 0.004f) return;
        switch (id)
        {
            case "media": Media(c, ux, p, t); break;
            case "sound": Sound(c, ux, p, t); break;
            case "home": Home(c, ux, p, t); break;
            case "weather": Weather(c, ux, p, t); break;
            case "notes": Notes(c, ux, p, t); break;
            case "shelf": Shelf(c, ux, p, t); break;
            case "clipboard": Clipboard(c, ux, p, t); break;
            case "timer": Timer(c, ux, p, t); break;
            case "devices": Devices(c, ux, p, t); break;
        }
    }

    static void Empty(Canvas c, Box p, char glyph, string title, string sub, float t)
    {
        c.Icon(glyph, p.CX, p.CY - 26, 30, Col.White.A(0.35f * t));
        c.Text(title, new Box(p.X, p.CY - 2, p.W, 22), 14.5f, Col.White.A(0.8f * t), FontWeight.SemiBold, HAlign.Center);
        c.Text(sub, new Box(p.X + 20, p.CY + 20, p.W - 40, 36), 12, Col.White.A(0.5f * t), FontWeight.Normal, HAlign.Center, VAlign.Top, true);
    }

    // ==== Now Playing ======================================================
    void Media(Canvas c, Ux ux, Box p, float t)
    {
        var media = isl.Svc?.Media;
        if (media?.Current == null || string.IsNullOrEmpty(media.Current.Title)) { Empty(c, p, Glyph.Music, "Nothing playing", "Play something in Spotify, YouTube, Plex or any app that shows in the Windows media controls.", t); return; }
        var sessions = media.Sessions.Where(s => !string.IsNullOrEmpty(s.Title)).ToList();
        float rowH = SpotifyRow() ? SpotifyRowH : 0, pillsH = sessions.Count > 1 ? 34 : 0;
        var card = new Box(p.X - 22, p.Y - 14, p.W + 44, p.H + 14 - rowH - pillsH);
        PeekPage.MediaCard(isl, c, ux, card, t, true);
        if (rowH > 0) SpotifyVolumeRow(c, ux, new Box(p.X, card.Bottom - 10, p.W, rowH), t);
        if (sessions.Count > 1)
        {
            float x = p.X; float y = p.Bottom - 26;
            foreach (var s in sessions.Take(5))
            {
                float w = Math.Min(170, c.Measure(s.AppName + "  " + s.Title, 11.5f, FontWeight.SemiBold).w + 40);
                bool cur = s == media.Current;
                ux.Pill("sess:" + s.SessionId, new Box(x, y, w, 26), s.AppName + " · " + s.Title, () => media.Select(s.SessionId), cur ? Col.White : Col.White, s.Playing ? Glyph.Play : Glyph.Pause, cur, 11.5f);
                x += w + 8;
            }
        }
    }

    // Spotify's own volume (the slider inside the app; see SpotifyService), on its
    // own row under the transport like the volume row in Apple's Now Playing.
    const float SpotifyRowH = 40;
    bool SpotifyRow() => isl.Cfg.SpotifyVolume && isl.Svc?.Spotify != null && isl.Svc.Media?.Current is { } m && m.IsSpotify && !string.IsNullOrEmpty(m.Title);

    void SpotifyVolumeRow(Canvas c, Ux ux, Box row, float t)
    {
        var sp = isl.Svc.Spotify;
        if (!sp.Connected)
        {
            // signed in before and ran out: one click signs in again (Spotify remembers
            // the approval); never set up: the Spotify tab, which walks through it
            if (sp.Connecting) { Paint.Spinner(c, row.CX, row.CY, 14, Col.White.A(0.6f), isl.Clock.Now, t); return; }
            bool canRenew = sp.Expired && !string.IsNullOrEmpty(isl.Cfg.SpotifyClientId);
            ux.Pill("sp:connect", Box.Centered(row.CX, row.CY, 300, 28), canRenew ? "Spotify signed out - sign in again" : "Connect Spotify to control its volume",
                () => { if (canRenew) _ = sp.ConnectAsync(); else isl.Host.OpenSettings("Spotify"); }, Col.White, Glyph.Settings, false, 11.5f);
            return;
        }
        sp.Poll();
        if (sp.Volume == null)
        {
            // the first read is out: a spinner; otherwise say why there's nothing to set.
            // A line of text needs more room under the play button than the thin
            // slider does, so it sits in the row's lower part, clear of the transport.
            var low = new Box(row.X, row.Y + 12, row.W, row.H - 12);
            if (sp.Loading) Paint.Spinner(c, low.CX, low.CY, 14, Col.White.A(0.6f), isl.Clock.Now, t);
            else c.Text(sp.Status, low, 11.5f, Col.White.A(0.45f * t), FontWeight.Medium, HAlign.Center);
            return;
        }
        int v = sp.Volume.Value;
        float x0 = row.X + 6, x1 = row.Right - 6;
        c.Icon(v == 0 ? Glyph.Mute : Glyph.Volume1, x0 + 8, row.CY, 13, Col.White.A(0.6f * t));
        c.Icon(Glyph.Volume3, x1 - 48, row.CY, 13, Col.White.A(0.6f * t));
        c.Text($"{v}%", new Box(x1 - 34, row.Y, 34, row.H), 11, Col.White.A(0.55f * t), FontWeight.Medium, HAlign.Right);
        var track = new Box(x0 + 26, row.CY - 6, x1 - 68 - (x0 + 26), 12);
        ux.Slider("spvol", track, v / 100f, (f, fin) => sp.SetVolume((int)Math.Round(f * 100), fin), Col.White.A((sp.SupportsVolume ? 0.9f : 0.4f) * t), 4.5f);
        // under the slider: whose volume this is when it's another device, or that
        // Spotify's six-month sign-in is nearly up (it can't be renewed silently)
        var ub = new Box(track.X, row.Bottom - 12, track.W, 12);
        if (sp.ReauthDaysLeft is int d && d <= 14)
        {
            string txt = sp.Connecting ? "Renewing in your browser…" : $"Spotify sign-in ends in {Math.Max(0, d)} day{(d == 1 ? "" : "s")} · Renew";
            ux.Add("sp:renew", Box.Centered(ub.CX, ub.CY, 260, 16), () => { if (!sp.Connecting) _ = sp.ConnectAsync(); }, 6);
            c.Text(txt, ub, 10, Col.White.A((0.45f + 0.35f * ux.HoverT("sp:renew")) * t), FontWeight.SemiBold, HAlign.Center);
        }
        else if (sp.DeviceName.Length > 0 && !sp.DeviceName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            c.Text("on " + sp.DeviceName, ub, 10, Col.White.A(0.4f * t), FontWeight.Medium, HAlign.Center);
    }

    // ==== Sound ============================================================
    void Sound(Canvas c, Ux ux, Box p, float t)
    {
        var au = isl.Svc?.Audio;
        if (au == null) { Empty(c, p, Glyph.Volume, "Sound unavailable", "Windows' audio service didn't answer.", t); return; }
        if (isl.Clock.Now - lastMixer > 1.0) { au.RefreshMixerAsync(() => isl.Host.RequestFrame()); lastMixer = isl.Clock.Now; }
        float sc = ux.Anim("scroll:sound", scroll.GetValueOrDefault("sound"), ScrollSpring, 0.05);
        var view = p.Inset(0, -4);
        var soundScroll = ux.Add("sound:scroll", view, null, 0, CursorKind.Arrow); soundScroll.ScrollPx = 40; soundScroll.Wheel = (d, h) => { scroll["sound"] = Math.Clamp(scroll.GetValueOrDefault("sound") - d / 120f * 40, 0, Math.Max(0, contentH - view.H)); isl.Host.RequestFrame(); };
        c.FadeClip(view, sc > 0.5f, sc < contentH - view.H - 0.5f);
        float y = p.Y - sc;
        c.Text("OUTPUT", new Box(p.X, y, 200, 16), 10.5f, Col.White.A(0.45f * t), FontWeight.Bold);
        y += 20;
        if (au.Outputs.Count == 0)
            for (int k = 0; k < 3; k++) Paint.SkeletonRow(c, new Box(p.X, y + k * 42, p.W, 40), isl.Clock.Now, t * (1 - 0.2f * k), k);
        foreach (var d in au.Outputs)
        {
            var row = new Box(p.X, y, p.W, 40);
            var dd = d;
            ux.Add("out:" + d.Id, new Box(row.X, row.Y, 210, row.H), () => { if (!dd.Default && au.SetDefault(dd)) { au.RefreshMixerAsync(() => isl.Host.RequestFrame()); ux.Kick("out:bump", 1); } }, 10);
            float h = ux.HoverT("out:" + d.Id);
            if (h > 0.01f) c.Fill(new Box(row.X - 6, row.Y, 216, row.H), Col.White.A(0.06f * h * t), 10);
            c.Circle(row.X + 9, row.CY, 6.5f, (d.Default ? Paint.Accent(isl) : Col.White.A(0.15f)).A(t));
            if (d.Default) c.Circle(row.X + 9, row.CY, 2.6f, Col.White.A(t));
            c.Text(d.Name, new Box(row.X + 24, row.Y, 180, row.H), 12.5f, Col.White.A((d.Default ? 0.95f : 0.7f) * t), d.Default ? FontWeight.SemiBold : FontWeight.Medium);
            ux.Slider("outvol:" + d.Id, new Box(row.X + 220, row.CY - 6, row.W - 220 - 80, 12), d.Muted ? 0 : d.Volume, (v, fin) => au.SetDeviceVolume(dd, v), Col.White.A(0.9f * t), 4);
            c.Text($"{Math.Round(d.Volume * 100)}", new Box(row.Right - 74, row.Y, 30, row.H), 11.5f, Col.White.A(0.55f * t), FontWeight.Medium, HAlign.Right);
            ux.IconButton("outmute:" + d.Id, row.Right - 20, row.CY, 30, d.Muted ? Glyph.Mute : Glyph.Volume3, () => au.ToggleDeviceMute(dd), d.Muted ? Col.Hex(0xff453a) : null, 13);
            y += 42;
        }
        y += 8;
        c.Text("APPS", new Box(p.X, y, 200, 16), 10.5f, Col.White.A(0.45f * t), FontWeight.Bold);
        y += 20;
        if (au.Apps.Count == 0) { c.Text("No apps are playing sound", new Box(p.X, y, p.W, 30), 12, Col.White.A(0.45f * t)); y += 30; }
        foreach (var app in au.Apps)
        {
            var row = new Box(p.X, y, p.W, 38);
            var aa = app;
            var ic = AppIcon(c, app.ExePath);
            if (ic != null) c.Image(ic, Box.Centered(row.X + 12, row.CY, 22, 22), (app.Muted ? 0.4f : 1) * t);
            else c.Icon(Glyph.App, row.X + 12, row.CY, 16, Col.White.A(0.6f * t));
            c.Text(app.Name, new Box(row.X + 32, row.Y, 175, row.H), 12.5f, Col.White.A((app.Active ? 0.92f : 0.6f) * t), FontWeight.Medium);
            if (app.Active) c.Circle(row.X + 210, row.CY, 2.5f, Col.Hex(0x34c759).A(t));
            ux.Slider("appvol:" + app.Key, new Box(row.X + 220, row.CY - 6, row.W - 220 - 80, 12), app.Muted ? 0 : app.Volume, (v, fin) => au.SetAppVolume(aa, v), (app.Muted ? Col.Gray(0.5f) : Col.White).A(0.9f * t), 4);
            c.Text($"{Math.Round(app.Volume * 100)}", new Box(row.Right - 74, row.Y, 30, row.H), 11.5f, Col.White.A(0.55f * t), FontWeight.Medium, HAlign.Right);
            ux.IconButton("appmute:" + app.Key, row.Right - 20, row.CY, 30, app.Muted ? Glyph.Mute : Glyph.Volume2, () => au.ToggleAppMute(aa), app.Muted ? Col.Hex(0xff453a) : null, 13);
            y += 40;
        }
        float measured = y + sc - p.Y;
        if (Math.Abs(measured - contentH) > 0.5f) isl.Host.RequestFrame();   // the fade used last frame's height
        contentH = measured;
        c.Unfade();
    }
    float contentH;

    static readonly Dictionary<string, (Vortice.Direct2D1.ID2D1Bitmap1 b, int gen)> appIcons = new();
    static readonly Dictionary<string, (byte[] px, int w, int h)?> iconPixels = new();
    static Action requestFrame;

    /// <summary>
    /// A file's shell icon as a GPU bitmap. Never blocks: the first ask queues
    /// the fetch on the icon thread and returns null (the caller draws a
    /// placeholder); the next frame after it arrives has the icon.
    /// </summary>
    public static Vortice.Direct2D1.ID2D1Bitmap1 AppIcon(Canvas c, string path, int size = 32)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string k = path + "|" + size;
        if (appIcons.TryGetValue(k, out var e) && e.gen == Gfx.Generation) return e.b;
        if (!iconPixels.TryGetValue(k, out var px))
        {
            if (Services.IconLoader.Delivered == null)
                Services.IconLoader.Delivered = (pth, sz, r) => { iconPixels[pth + "|" + sz] = r; requestFrame?.Invoke(); };
            Services.IconLoader.Request(path, size);
            return null;
        }
        var bmp = px == null ? null : Canvas.FromBgra(c.C, px.Value.px, px.Value.w, px.Value.h);
        if (appIcons.Count > 200) { foreach (var kv in appIcons.Take(50).ToList()) { kv.Value.b?.Dispose(); appIcons.Remove(kv.Key); } }
        if (iconPixels.Count > 400) iconPixels.Clear();
        appIcons[k] = (bmp, Gfx.Generation);
        return bmp;
    }

    // ==== Home Assistant ===================================================
    void Home(Canvas c, Ux ux, Box p, float t)
    {
        var ha = isl.Svc?.Ha;
        if (ha == null) return;
        if (isl.Clock.Now - lastHa > isl.Cfg.HaRefreshSeconds) RefreshHa(false);
        if (!ha.Configured) { Empty(c, p, Glyph.Home, "Connect Home Assistant", "Add your Home Assistant URL and a long-lived access token in Settings -> Home Assistant.", t); SettingsButton(c, ux, p, t, "Open Settings"); return; }
        if (isl.Cfg.HaEntities.Count == 0) { Empty(c, p, Glyph.Home, "Pick your devices", "Choose which lights, scenes, scripts and climate controls appear here in Settings -> Home Assistant.", t); SettingsButton(c, ux, p, t, "Choose entities"); return; }
        bool offline = ha.Status is HaStatus.Unreachable or HaStatus.Unauthorized or HaStatus.Error;
        // status line
        // connecting is a wait with nothing to preview: a spinner where the status dot goes
        if (ha.Status == HaStatus.Connecting) Paint.Spinner(c, p.X + 4, p.Y + 6, 11, Col.White.A(0.75f), isl.Clock.Now, t);
        else c.Circle(p.X + 4, p.Y + 6, 3.5f, (ha.Status == HaStatus.Online ? Col.Hex(0x34c759) : Col.Hex(0xff453a)).A(t));
        c.Text(ha.StatusText, new Box(p.X + 14, p.Y - 2, p.W - 60, 16), 11, Col.White.A(0.5f * t), FontWeight.Medium);
        RefreshButton(ux, c, "ha:refresh", p.Right - 10, p.Y + 6, false, haSpinUntil, () => RefreshHa(true), t);
        var grid = new Box(p.X, p.Y + 20, p.W, p.H - 20);
        int cols = 4; float gap = 10, tw = (grid.W - gap * (cols - 1)) / cols, th = 76;
        int k = 0;
        float sc = ux.Anim("scroll:home", scroll.GetValueOrDefault("home"), ScrollSpring, 0.05);
        ux.Add("ha:scroll", grid, null, 0, CursorKind.Arrow).Wheel = (d, h) => { scroll["home"] = Math.Clamp(scroll.GetValueOrDefault("home") - d / 120f * 50, 0, Math.Max(0, homeH - grid.H)); isl.Host.RequestFrame(); };
        c.FadeClip(grid.Inset(-4, -2), sc > 0.5f, sc < homeH - grid.H - 0.5f);
        foreach (var id in isl.Cfg.HaEntities)
        {
            ha.Entities.TryGetValue(id, out var e);
            int span = id.StartsWith("climate.") ? 2 : 1;
            if (k % cols + span > cols) k += cols - k % cols;
            float bx = grid.X + (k % cols) * (tw + gap), by = grid.Y + (k / cols) * (th + gap) - sc;
            var b = new Box(bx, by, tw * span + gap * (span - 1), th);
            if (e == null && !offline)
            {
                // not fetched yet: a tile-shaped placeholder instead of a "…"
                Paint.Skeleton(c, b, 18, isl.Clock.Now, t);
                Paint.Skeleton(c, new Box(b.X + 12, b.Y + 12, 28, 28), 14, isl.Clock.Now, t);
                Paint.Skeleton(c, new Box(b.X + 12, b.Y + 46, b.W * 0.55f, 10), 5, isl.Clock.Now, t);
                Paint.Skeleton(c, new Box(b.X + 12, b.Y + 61, b.W * 0.35f, 8), 4, isl.Clock.Now, t * 0.8f);
            }
            else Tile(c, ux, ha, e ?? new HaEntity { Id = id, Name = id, State = "unavailable" }, b, t, offline);
            if (e != null) TileSpinner(c, ha, e, b, t, offline);
            k += span;
        }
        float measured = (float)Math.Ceiling(k / (double)cols) * (th + gap);
        if (Math.Abs(measured - homeH) > 0.5f) isl.Host.RequestFrame();   // the fade used last frame's height
        homeH = measured;
        c.Unfade();
    }
    float homeH;

    void SettingsButton(Canvas c, Ux ux, Box p, float t, string label) =>
        ux.Pill("ha:settings", Box.Centered(p.CX, p.Bottom - 18, 150, 30), label, () => isl.Host.OpenSettings("Home Assistant"), Col.White, Glyph.Settings);

    /// <summary>
    /// One Home Assistant tile, in Apple Home's visual language: off (and
    /// stateless scenes/scripts) is dark glass with white text; on is a light
    /// tile with dark text and the device's color in its icon disc. A light's
    /// brightness is a soft fill rising from the left that you drag to set.
    /// </summary>
    void Tile(Canvas c, Ux ux, HomeAssistant ha, HaEntity e, Box b, float t, bool offline)
    {
        string id = "ha:" + e.Id;
        bool dead = e.Unavailable || offline;
        bool stateless = e.Domain is "scene" or "script" or "button" or "input_button";
        bool on = e.On && !dead && !stateless;
        var tint = TileTint(e);
        var disc = Col.Legible(tint, 0.18f);
        float h = ux.HoverT(id), p = ux.PressT(id);
        float onT = ux.Anim(id + ":on", on ? 1 : 0, SpringSpec.Snappy);
        var bb = b.Scale(1 - 0.03f * p);
        float alpha = t * (dead ? 0.45f : 1);
        const float R = 18, P = 12;

        // background: glass when off, near-white when on (cross-fades)
        c.Fill(bb, Col.White.A((0.085f + 0.045f * h) * alpha), R);
        if (onT > 0.003f) c.Fill(bb, Col.Mix(Col.Gray(0.93f), Col.White, 0.4f * h).A(onT * alpha), R);

        // brightness: a tinted fill from the left, inside the tile's rounded shape
        if (e.Domain == "light" && e.Dimmable && onT > 0.01f)
        {
            float bw = ux.Anim(id + ":bri", (float)e.Brightness, SpringSpec.Snappy);
            using var g = Gfx.D2DFactory.CreateRoundedRectangleGeometry(new Vortice.Direct2D1.RoundedRectangle(bb.Raw, R, R));
            c.Push(g, onT * alpha);
            c.Fill(new Box(bb.X, bb.Y, bb.W * bw, bb.H), tint.A(0.30f));
            c.Pop();
        }

        float ripple = ux.Anim(id + ":ripple", 0, SpringSpec.Slow);
        if (ripple > 0.01f)
        {
            using var g = Gfx.D2DFactory.CreateRoundedRectangleGeometry(new Vortice.Direct2D1.RoundedRectangle(bb.Raw, R, R));
            c.Push(g, 1);
            c.Circle(bb.X + P + 14, bb.Y + P + 14, bb.W * 1.3f * (1 - ripple / 6f), disc.A(ripple / 6f * 0.45f * t));
            c.Pop();
        }

        var z = ux.Add(id, bb, dead ? null : () => { ha.Tap(e); if (stateless) ux.Kick(id + ":ripple", 6); }, R);
        if (e.Domain == "light" && e.Dimmable && !dead)
            z.Drag = (x, y, fin) =>
            {
                double v = Math.Clamp((x - bb.X) / bb.W, 0.01, 1);
                if (fin) ha.SetBrightness(e, v); else { e.State = "on"; e.Attr["brightness"] = (int)(v * 255); }
            };

        // icon disc
        var ic = new Box(bb.X + P, bb.Y + P, 28, 28);
        var title = Col.Mix(Col.White, Col.Gray(0.08f), onT);
        var sub = Col.Mix(Col.White.A(0.55f), Col.Gray(0.08f, 0.55f), onT);
        bool coloredDisc = on || stateless;
        c.Circle(ic.CX, ic.CY, 14, (coloredDisc ? disc : Col.White.A(0.14f)).A(alpha));
        c.Icon(TileGlyph(e), ic.CX, ic.CY, 14, (coloredDisc ? Col.White : Col.White.A(0.8f)).A(alpha));

        c.Text(e.Name, new Box(bb.X + P, bb.Y + 42, bb.W - 2 * P, 17), 12, title.A(alpha), FontWeight.SemiBold);
        c.Text(StateText(e, offline), new Box(bb.X + P, bb.Y + 58, bb.W - 2 * P, 15), 10.5f, sub.A(sub.A * alpha), FontWeight.Medium);

        if (e.Domain == "climate" && !dead)
        {
            double target = e.Num("temperature") ?? double.NaN;
            double step = e.Num("target_temp_step") ?? 1;
            float cy = ic.CY;
            var btnFg = Col.Mix(Col.White, Col.Gray(0.1f), onT);
            ClimateButton(c, ux, id + ":minus", bb.Right - 92, cy, '\uE738', () => { if (!double.IsNaN(target)) ha.SetTemperature(e, target - step); }, onT, alpha);
            c.Text(double.IsNaN(target) ? "--°" : $"{target:0.#}°", Box.Centered(bb.Right - 60, cy, 40, 20), 14, btnFg.A(alpha), FontWeight.SemiBold, HAlign.Center);
            ClimateButton(c, ux, id + ":plus", bb.Right - 28, cy, Glyph.Add, () => { if (!double.IsNaN(target)) ha.SetTemperature(e, target + step); }, onT, alpha);
            var modes = e.List("hvac_modes");
            if (modes.Count > 1)
            {
                var mb = new Box(bb.Right - P - 92, bb.Bottom - P - 24, 92, 24);
                string mid = id + ":mode";
                ux.Add(mid, mb, () => { int i = modes.IndexOf(e.State); ha.SetHvacMode(e, modes[(i + 1) % modes.Count]); }, 12);
                float mh = ux.HoverT(mid), mp = ux.PressT(mid);
                var mbb = mb.Scale(1 - 0.03f * mp);
                // a dark pill on the light tile, a light one on the dark tile
                c.Fill(mbb, Col.Mix(Col.White.A(0.16f + 0.08f * mh), Col.Gray(0.12f, 0.88f + 0.1f * mh), onT).A(alpha), 12);
                c.Text(Pretty(e.State), mbb, 11, Col.White.A(alpha), FontWeight.SemiBold, HAlign.Center);
            }
        }
        else if ((e.Domain is "select" or "input_select") && !dead)
        {
            var opts = e.List("options");
            z.Click = () => { if (opts.Count == 0) return; int i = opts.IndexOf(e.State); ha.SelectOption(e, opts[(i + 1) % opts.Count]); };
        }
        else if (e.Domain is "number" or "input_number" && !dead)
        {
            double mn = e.Num("min") ?? 0, mx = e.Num("max") ?? 100;
            double.TryParse(e.State, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v);
            z.Click = null;
            z.Drag = (x, y, fin) => { double nv = mn + (mx - mn) * Math.Clamp((x - bb.X) / bb.W, 0, 1); double st = e.Num("step") ?? 1; nv = Math.Round(nv / st) * st; e.State = nv.ToString(System.Globalization.CultureInfo.InvariantCulture); if (fin) ha.SetNumber(e, nv); };
            c.Fill(new Box(bb.X + P, bb.Bottom - 6, (bb.W - 2 * P) * (float)Math.Clamp((v - mn) / Math.Max(1e-9, mx - mn), 0, 1), 2.5f), Col.White.A(0.6f * alpha), 1.25f);
        }
        else if (e.Domain == "fan" && on && e.Num("percentage") is double pct)
        {
            z.Drag = (x, y, fin) => { double nv = Math.Clamp((x - bb.X) / bb.W, 0, 1); e.Attr["percentage"] = (int)(nv * 100); if (fin) ha.SetFanPercent(e, nv); };
            using var g = Gfx.D2DFactory.CreateRoundedRectangleGeometry(new Vortice.Direct2D1.RoundedRectangle(bb.Raw, R, R));
            c.Push(g, onT * alpha);
            c.Fill(new Box(bb.X, bb.Y, bb.W * (float)(pct / 100), bb.H), tint.A(0.25f));
            c.Pop();
        }
    }

    static void ClimateButton(Canvas c, Ux ux, string id, float cx, float cy, char glyph, Action click, float onT, float alpha)
    {
        var b = Box.Centered(cx, cy, 28, 28);
        ux.Add(id, b, click, 14);
        float h = ux.HoverT(id), p = ux.PressT(id);
        float s = 1 - 0.03f * p;
        c.Circle(cx, cy, 14 * s, Col.Mix(Col.White.A(0.10f + 0.10f * h), Col.Gray(0.1f, 0.07f + 0.08f * h), onT).A(alpha));
        c.Icon(glyph, cx, cy, 12 * s, Col.Mix(Col.White, Col.Gray(0.1f), onT).A(alpha));
    }

    static Color4 TileTint(HaEntity e)
    {
        if (e.Domain == "light" && e.Rgb is var (r, g, b)) return Col.Vivid(new Color4(r / 255f, g / 255f, b / 255f, 1), 0.6f, 1.05f);
        return e.Domain switch
        {
            "light" => Col.Hex(0xffb340), "climate" => e.State switch { "cool" => Col.Hex(0x32ade6), "heat" => Col.Hex(0xff9500), "dry" => Col.Hex(0xaf52de), _ => Col.Hex(0x32ade6) },
            "fan" => Col.Hex(0x32ade6), "switch" or "input_boolean" => Col.Hex(0x34c759), "scene" => Col.Hex(0xaf52de), "script" => Col.Hex(0x007aff),
            "automation" => Col.Hex(0xff9500), "lock" => Col.Hex(0xff3b30), "media_player" => Col.Hex(0xff2d55), "humidifier" => Col.Hex(0x30b0c7), _ => Col.Hex(0x007aff),
        };
    }

    static char TileGlyph(HaEntity e) => e.Domain switch
    {
        "light" => Glyph.Lightbulb, "switch" => Glyph.Power, "input_boolean" => Glyph.ToggleOn, "climate" => Glyph.Thermometer,
        "fan" => Glyph.Wind, "scene" => '\uE790', "script" => Glyph.Play, "automation" => '\uE945', "button" or "input_button" => Glyph.Button,
        "media_player" => Glyph.Tv, "lock" => Glyph.Lock, "cover" => '\uE80F', "sensor" => Glyph.Thermometer, "binary_sensor" => Glyph.Eye,
        "humidifier" => Glyph.Drop, "select" or "input_select" => '\uE8FD', "number" or "input_number" => '\uE9E9', _ => Glyph.Home,
    };

    static string Pretty(string s) => string.IsNullOrEmpty(s) ? "" : (char.ToUpper(s[0]) + s[1..]).Replace('_', ' ');

    static string StateText(HaEntity e, bool offline)
    {
        if (offline) return "Offline";
        if (e.State == "missing") return "Not found in HA";
        if (e.Unavailable) return "Unavailable";
        switch (e.Domain)
        {
            case "light": return e.On ? (e.Attr.ContainsKey("brightness") ? $"On · {Math.Round(e.Brightness * 100)}%" : "On") : "Off";
            case "climate":
                var cur = e.Num("current_temperature");
                return Pretty(e.State) + (cur.HasValue ? $" · {cur:0.#}° now" : "");
            case "fan": return e.On ? (e.Num("percentage") is double p ? $"On · {p:0}%" : "On") : "Off";
            case "scene": case "script": case "button": case "input_button": return e.Domain == "script" && e.On ? "Running…" : "Tap to run";
            case "sensor": return e.State + (e.Str("unit_of_measurement") is string u ? " " + u : "");
            case "automation": return e.On ? "Enabled" : "Disabled";
            default: return Pretty(e.State);
        }
    }

    // ==== Weather ==========================================================
    void Weather(Canvas c, Ux ux, Box p, float t)
    {
        var ws = isl.Svc?.Weather;
        var w = ws?.Now;
        if (w == null)
        {
            if (ws != null && ws.Status is "Loading…" or "")
            {
                // the weather panel's shape, shimmering, until the first reading lands
                double n = isl.Clock.Now;
                Paint.Skeleton(c, new Box(p.X + 12, p.Y + 10, 46, 46), 23, n, t);
                Paint.Skeleton(c, new Box(p.X + 78, p.Y + 10, 110, 46), 10, n, t);
                Paint.Skeleton(c, new Box(p.X + 230, p.Y + 12, 120, 14), 7, n, t);
                Paint.Skeleton(c, new Box(p.X + 230, p.Y + 34, 90, 10), 5, n, t * 0.8f);
                Paint.Skeleton(c, new Box(p.X, p.Y + 84, p.W, 94), 16, n, t * 0.9f);
                return;
            }
            Empty(c, p, Glyph.Cloud, "No weather yet", ws?.Status ?? "", t); return;
        }
        Paint.Weather(c, w.Icon, p.X + 34, p.Y + 34, 44, t);
        c.Text($"{w.Temp:0}°", new Box(p.X + 78, p.Y, 160, 66), 52, Col.White.A(t), FontWeight.SemiBold, HAlign.Left);
        c.Text(w.Condition, new Box(p.X + 230, p.Y + 8, p.W - 230, 22), 16, Col.White.A(0.92f * t), FontWeight.SemiBold);
        string hl = double.IsNaN(w.High) ? "" : $"H {w.High:0}°  L {w.Low:0}°";
        c.Text(hl, new Box(p.X + 230, p.Y + 32, 200, 18), 12.5f, Col.White.A(0.6f * t), FontWeight.Medium);
        var extra = new List<string>();
        if (!double.IsNaN(w.Humidity)) extra.Add($"{w.Humidity:0}% humidity");
        if (!double.IsNaN(w.Wind)) extra.Add($"wind {w.Wind:0}");
        c.Text(string.Join("  ·  ", extra), new Box(p.X + 230, p.Y + 50, 300, 18), 11.5f, Col.White.A(0.45f * t), FontWeight.Medium);
        // hourly
        if (w.Hours.Count > 0)
        {
            float cw = p.W / w.Hours.Count, y0 = p.Y + 92;
            c.Fill(new Box(p.X, y0 - 8, p.W, 94), Col.White.A(0.05f * t), 16);
            for (int i = 0; i < w.Hours.Count; i++)
            {
                var (lbl, temp, icon) = w.Hours[i];
                float cx = p.X + cw * (i + 0.5f);
                c.Text(i == 0 ? "Now" : lbl, Box.Centered(cx, y0 + 8, cw, 16), 11, Col.White.A(0.55f * t), FontWeight.SemiBold, HAlign.Center);
                Paint.Weather(c, icon, cx, y0 + 38, 20, t);
                c.Text($"{temp:0}°", Box.Centered(cx, y0 + 66, cw, 18), 13, Col.White.A(0.9f * t), FontWeight.SemiBold, HAlign.Center);
            }
        }
        c.Text($"{w.Location} · {w.Source} · updated {w.Updated:t}", new Box(p.X, p.Bottom - 16, p.W - 40, 16), 10.5f, Col.White.A(0.35f * t), FontWeight.Medium);
        RefreshButton(ux, c, "wx:refresh", p.Right - 10, p.Bottom - 8, ws.Busy, wxSpinUntil, () => { wxSpinUntil = isl.Clock.Now + MinSpin; _ = ws.RefreshAsync(); }, t);
    }

    // ==== Notes ============================================================
    void Notes(Canvas c, Ux ux, Box p, float t)
    {
        var notes = isl.Svc?.Notes;
        if (notes == null) return;
        var field = new Box(p.X, p.Y, p.W, 36);
        float fh = ux.HoverT("notes:field");
        c.Fill(field, Col.White.A((noteFocused ? 0.13f : 0.08f + 0.04f * fh) * t), 12);
        if (noteFocused) c.Stroke(field, Paint.Accent(isl).A(0.6f * t), 12, 1.2f);
        c.Icon(Glyph.Add, field.X + 18, field.CY, 13, Col.White.A(0.6f * t));
        var tb = new Box(field.X + 34, field.Y, field.W - 44, field.H);
        noteBox.Draw(c, tb, 13, noteFocused, isl.Clock.Now, Paint.Accent(isl));
        ux.Add("notes:field", field, () => { noteFocused = true; isl.TakeKeyboard(); noteBox.Touch(isl.Clock.Now); }, 12, CursorKind.IBeam);
        int done = notes.Items.Count(n => n.Done);
        c.Text(notes.Items.Count == 0 ? "" : $"{done}/{notes.Items.Count} done", new Box(p.X + 4, p.Y + 44, 200, 16), 11, Col.White.A(0.45f * t), FontWeight.SemiBold);
        if (done > 0) ux.Pill("notes:clear", new Box(p.Right - 92, p.Y + 42, 92, 22), "Clear done", notes.ClearDone, Col.White, '\0', false, 11);
        var list = new Box(p.X, p.Y + 70, p.W, p.H - 70);
        float sc = ux.Anim("scroll:notes", scroll.GetValueOrDefault("notes"), ScrollSpring, 0.05);
        var notesScroll = ux.Add("notes:scroll", list, null, 0, CursorKind.Arrow); notesScroll.ScrollPx = 40; notesScroll.Wheel = (d, h) => { scroll["notes"] = Math.Clamp(scroll.GetValueOrDefault("notes") - d / 120f * 40, 0, Math.Max(0, notes.Items.Count * 38 - list.H)); isl.Host.RequestFrame(); };
        if (notes.Items.Count == 0) { c.Text("Nothing here yet. Type above and press Enter.", list.Inset(0, 30), 12, Col.White.A(0.4f * t), FontWeight.Normal, HAlign.Center, VAlign.Top); return; }
        c.FadeClip(list, sc > 0.5f, sc < notes.Items.Count * 38 - list.H - 0.5f);
        float y = list.Y - sc;
        float added = ux.Anim("notes:added", 0, SpringSpec.Wobbly);
        for (int i = 0; i < notes.Items.Count; i++)
        {
            var n = notes.Items[i];
            var row = new Box(list.X, y, list.W, 34);
            if (row.Bottom >= list.Y && row.Y <= list.Bottom)
            {
                string rid = "note:" + n.Created.Ticks;
                float h = ux.HoverT(rid);
                if (h > 0.01f) c.Fill(row, Col.White.A(0.06f * h * t), 10);
                var check = Box.Centered(row.X + 16, row.CY, 18, 18);
                float dt = ux.Anim(rid + ":done", n.Done ? 1 : 0, SpringSpec.Press);
                c.Stroke(check, Col.White.A((0.5f - 0.5f * dt) * t), 9, 1.5f);
                c.Fill(check.Scale(dt), Paint.Accent(isl).A(t), 9);
                // the mark draws itself a beat after the box fills (and unwinds when cleared)
                float tick = ux.Anim(rid + ":tick", n.Done ? 1 : 0, SpringSpec.Content);
                c.DrawnCheck(check.CX, check.CY, 14, (tick - 0.2f) / 0.8f, Col.White.A(t), 2.2f);
                var nn = n;
                ux.Add(rid, new Box(row.X, row.Y, row.W - 36, row.H), () => notes.Toggle(nn), 10);
                float slide = i == 0 ? added * 14 : 0;
                c.Text(n.Text, new Box(row.X + 34 - slide, row.Y, row.W - 80, row.H), 13, Col.White.A((n.Done ? 0.4f : 0.92f) * t), FontWeight.Normal);
                if (n.Done) c.Line(row.X + 34, row.CY + 1, row.X + 34 + Math.Min(row.W - 80, c.Measure(n.Text, 13).w) * dt, row.CY + 1, Col.White.A(0.4f * t), 1.2f);
                if (h > 0.05f || ux.TouchMode) ux.IconButton(rid + ":del", row.Right - 18, row.CY, 26, Glyph.Delete, () => notes.Remove(nn), null, 11);
            }
            y += 38;
        }
        c.Unfade();
    }

    // ==== Shelf: your own storage =========================================
    string shelfFlash; double shelfFlashAt; string shelfFlashId;
    void Flash(string id, string word) { if (word == null) return; shelfFlash = word; shelfFlashId = id; shelfFlashAt = isl.Clock.Now; }

    void Shelf(Canvas c, Ux ux, Box p, float t)
    {
        var shelf = isl.Svc?.Shelf;
        if (shelf == null) return;
        float drop = ux.Anim("shelf:drop", isl.DragHover ? 1 : 0, SpringSpec.Snappy);
        if (drop > 0.01f)
        {
            c.Fill(p.Inset(-6), Paint.Accent(isl).A(0.12f * drop * t), 18);
            c.Stroke(p.Inset(-6), Paint.Accent(isl).A(0.7f * drop * t), 18, 1.6f);
        }
        void Paste() { var tx = Native.Win32.GetClipboardText(); if (!string.IsNullOrWhiteSpace(tx)) shelf.AddText(tx); }
        var dl = isl.Svc?.Downloads;
        if (isl.Cfg.Downloads && dl != null)
        {
            LinkBar(c, ux, new Box(p.X, p.Y, p.W, 36), t);
            float top = p.Y + 46;
            if (dlPanel) { DownloadsPanel(c, ux, new Box(p.X, top, p.W, p.Bottom - top), t); return; }
            if (RowJob(dl) is { } job) { JobRow(c, ux, job, new Box(p.X, top, p.W, 44), t, false); top += 52; }
            p = new Box(p.X, top, p.W, p.Bottom - top);
        }
        if (shelf.Items.Count == 0)
        {
            Empty(c, p, '\uE896', isl.DragHover ? "Drop to keep it here" : "Your shelf is empty",
                "Drag files, highlighted text, links or images onto the island to keep them here while you work - separate from the clipboard, and kept until you remove them.", t);
            ux.Pill("shelf:paste", Box.Centered(p.CX, p.Bottom - 20, 210, 30), "Keep what's on the clipboard", Paste, Col.White, Glyph.Clipboard);
            return;
        }
        if (convertFor != null && shelf.Items.Contains(convertFor)) ConvertBar(c, ux, p, t);
        else
        {
            convertFor = null;
            bool dlOn = isl.Cfg.Downloads && dl != null;
            ux.Pill("shelf:paste", new Box(p.Right - 186, p.Y - 4, 108, 22), "Add clipboard", Paste, Col.White, '\0', false, 11);
            if (dlOn) ux.Pill("shelf:downloads", new Box(p.Right - 290, p.Y - 4, 96, 22), dl.ActiveCount > 0 ? $"Downloads ({dl.ActiveCount})" : "Downloads", () => { dlPanel = true; isl.Host.RequestFrame(); }, Col.White, '\0', false, 11);
            c.Text($"{shelf.Items.Count} kept · click to open or copy · drag out to use", new Box(p.X, p.Y - 2, p.W - (dlOn ? 296 : 190), 16), 11, Col.White.A(0.45f * t), FontWeight.SemiBold);
            ux.Pill("shelf:clear", new Box(p.Right - 70, p.Y - 4, 70, 22), "Clear", shelf.Clear, Col.White, '\0', false, 11);
        }
        var grid = new Box(p.X, p.Y + 26, p.W, p.H - 26);
        int cols = 5; float cw = grid.W / cols, ch = 96;
        float sc = ux.Anim("scroll:shelf", scroll.GetValueOrDefault("shelf"), ScrollSpring, 0.05);
        ux.Add("shelf:scroll", grid, null, 0, CursorKind.Arrow).Wheel = (d, h) => { scroll["shelf"] = Math.Clamp(scroll.GetValueOrDefault("shelf") - d / 120f * 50, 0, Math.Max(0, (float)Math.Ceiling(shelf.Items.Count / (double)cols) * ch - grid.H)); isl.Host.RequestFrame(); };
        c.FadeClip(grid, sc > 0.5f, sc < (float)Math.Ceiling(shelf.Items.Count / (double)cols) * ch - grid.H - 0.5f);
        for (int i = 0; i < shelf.Items.Count; i++)
        {
            var it = shelf.Items[i];
            var cell = new Box(grid.X + (i % cols) * cw + 4, grid.Y + (i / cols) * ch - sc, cw - 8, ch - 8);
            if (cell.Bottom < grid.Y || cell.Y > grid.Bottom) continue;
            string id = "shelf:" + it.Kind + it.Path + it.Text.GetHashCode() + it.Added.Ticks;
            float h = ux.HoverT(id), pr = ux.PressT(id);
            // a new item fades in from 97%: grown from far smaller, it looked cartoonish
            float appear = ux.AnimFrom(id + ":in", 0, 1, SpringSpec.Content);
            var cb = cell.Scale((0.97f + 0.03f * appear) * (1 - 0.03f * pr));
            if (appear < 0.999f) c.Push(null, appear);
            c.Fill(cb, Col.White.A((0.06f + 0.06f * h) * t), 14);
            switch (it.Kind)
            {
                case "text":
                    c.Icon(Glyph.Note, cb.X + 16, cb.Y + 16, 12, Col.Hex(0xffd60a).A(t));
                    c.Text(it.Text.Trim(), new Box(cb.X + 10, cb.Y + 28, cb.W - 20, cb.H - 34), 10.5f, Col.White.A(0.85f * t), FontWeight.Normal, HAlign.Left, VAlign.Top, true);
                    break;
                case "link":
                    c.Icon(Glyph.Globe, cb.X + 16, cb.Y + 16, 12, Col.Hex(0x64d2ff).A(t));
                    c.Text(it.Name, new Box(cb.X + 10, cb.Y + 28, cb.W - 20, 30), 10.5f, Col.White.A(0.9f * t), FontWeight.SemiBold, HAlign.Left, VAlign.Top, true);
                    c.Text(Host(it.Text), new Box(cb.X + 10, cb.Bottom - 20, cb.W - 20, 14), 9.5f, Col.White.A(0.45f * t), FontWeight.Medium);
                    break;
                case "image":
                {
                    var img = Paint.Image(c, "shelfimg:" + it.Path, ImageBytes(it.Path));
                    if (img != null) c.Image(img, cb.Inset(4), t, 11);
                    else c.Icon('\uEB9F', cb.CX, cb.CY, 24, Col.White.A(0.6f * t));
                    break;
                }
                default:
                {
                    var ic = AppIcon(c, it.Path, 48);
                    var ib = Box.Centered(cb.CX, cb.Y + 32, 38, 38).Scale(1 + 0.06f * h);
                    if (ic != null) c.Image(ic, ib, (it.Exists ? 1 : 0.35f) * t);
                    else c.Icon(it.IsFolder ? Glyph.Folder : Glyph.File, ib.CX, ib.CY, 26, Col.White.A(0.7f * t));
                    c.Text(it.Name, new Box(cb.X + 6, cb.Y + 56, cb.W - 12, 30), 10.5f, Col.White.A((it.Exists ? 0.85f : 0.4f) * t), FontWeight.Medium, HAlign.Center, VAlign.Top, true);
                    break;
                }
            }
            if (shelfFlashId == id && isl.Clock.Now - shelfFlashAt < 1.2)
            {
                c.Fill(cb, Col.Black.A(0.6f * t), 14);
                c.Text(shelfFlash, cb, 12, Col.Hex(0x34c759).A(t), FontWeight.SemiBold, HAlign.Center);
                isl.Host.RequestFrame();
            }
            if (appear < 0.999f) c.Pop();
            var ii = it; string iid = id;
            var z = ux.Add(id, cb, () => Flash(iid, shelf.Activate(ii)), 14);
            z.RightClick = () => Flash(iid, shelf.Secondary(ii));
            z.Drag = (x, y, fin) =>
            {
                if (fin || !ii.Exists) return;
                ux.CancelPress();
                if (ii.Kind is "text" or "link") isl.Host.StartTextDrag(ii.Text); else isl.Host.StartFileDrag(new[] { ii.Path });
            };
            if (h > 0.4f || ux.TouchMode) ux.IconButton(id + ":x", cb.Right - 12, cb.Y + 12, 20, Glyph.Close, () => shelf.Remove(ii), null, 9);
            // a video or a song can be converted, right from the shelf
            if ((h > 0.4f || ux.TouchMode) && isl.Cfg.Downloads && isl.Svc?.Downloads != null && ii.Kind == "file" && Downloads.IsMedia(ii.Path) && ii.Exists)
                ux.IconButton(id + ":conv", cb.X + 12, cb.Y + 12, 20, Glyph.Convert, () => { convertFor = ii; isl.Host.RequestFrame(); }, null, 9);
        }
        c.Unfade();
    }

    static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
    static readonly Dictionary<string, byte[]> imageCache = new();
    static byte[] ImageBytes(string path)
    {
        if (imageCache.TryGetValue(path, out var b)) return b;
        try { b = File.Exists(path) && new FileInfo(path).Length < 30_000_000 ? File.ReadAllBytes(path) : null; } catch { b = null; }
        if (imageCache.Count > 40) imageCache.Clear();
        return imageCache[path] = b;
    }

    // ==== Clipboard: Windows' history (Win+V) ===============================
    void Clipboard(Canvas c, Ux ux, Box p, float t)
    {
        var cb = isl.Svc?.Clipboard;
        if (cb == null) return;
        if (cb.Items.Count == 0 && !cb.Loaded)
        {
            // the first read of the history is still out: rows in its shape, not "empty"
            for (int k = 0; k < 4; k++) Paint.SkeletonRow(c, new Box(p.X, p.Y + 26 + k * 48, p.W, 44), isl.Clock.Now, t * (1 - 0.18f * k), k);
            return;
        }
        if (cb.Items.Count == 0)
        {
            Empty(c, p, Glyph.Clipboard, "Clipboard history is empty", cb.WindowsHistory ? "Things you copy show up here - the same list as Win+V." : "Windows clipboard history is off. Turn it on to see the Win+V list here.", t);
            if (!cb.WindowsHistory) ux.Pill("clip:enable", Box.Centered(p.CX, p.Bottom - 18, 190, 30), "Clipboard settings", () => Native.Win32.ShellExecute("ms-settings:clipboard"), Col.White, Glyph.Settings);
            return;
        }
        c.Text(cb.WindowsHistory ? "Windows clipboard history · click to copy" : "Copied text (memory only) · click to copy", new Box(p.X, p.Y - 2, 360, 16), 11, Col.White.A(0.45f * t), FontWeight.SemiBold);
        ux.Pill("clip:clear", new Box(p.Right - 80, p.Y - 4, 80, 22), "Clear all", cb.Clear, Col.Hex(0xff453a), '\0', false, 11);
        var list = new Box(p.X, p.Y + 26, p.W, p.H - 26);
        float sc = ux.Anim("scroll:clip", scroll.GetValueOrDefault("clip"), ScrollSpring, 0.05);
        float rowH = 44;
        float total = cb.Items.Sum(i => i.Kind == "image" ? 84f : rowH);
        var clipScroll = ux.Add("clip:scroll", list, null, 0, CursorKind.Arrow); clipScroll.ScrollPx = 44; clipScroll.Wheel = (d, h) => { scroll["clip"] = Math.Clamp(scroll.GetValueOrDefault("clip") - d / 120f * 44, 0, Math.Max(0, total - list.H)); isl.Host.RequestFrame(); };
        c.FadeClip(list, sc > 0.5f, sc < total - list.H - 0.5f);
        float y = list.Y - sc;
        foreach (var item in cb.Items.ToList())
        {
            float hgt = item.Kind == "image" ? 80 : 40;
            var row = new Box(list.X, y, list.W, hgt);
            if (row.Bottom >= list.Y && row.Y <= list.Bottom)
            {
                string id = "clip:" + item.Id;
                float h = ux.HoverT(id);
                c.Fill(row, Col.White.A((0.05f + 0.06f * h) * t), 10);
                char g = item.Kind switch { "image" => '\uEB9F', "files" => Glyph.Folder, "text" => Glyph.Note, _ => Glyph.Clipboard };
                c.Icon(g, row.X + 18, row.Y + 20, 13, Col.White.A(0.5f * t));
                if (item.Kind == "image")
                {
                    var img = Paint.Image(c, "clipimg:" + item.Id, item.Image);
                    if (img != null) c.Image(img, new Box(row.X + 36, row.Y + 6, 120, hgt - 12), t, 8);
                }
                else
                {
                    string one = item.Text.Replace("\r", " ").Replace("\n", " ⏎ ").Replace("\t", " ");
                    c.Text(one, new Box(row.X + 36, row.Y, row.W - 150, hgt), 12.5f, Col.White.A(0.88f * t), FontWeight.Normal);
                }
                bool flash = copiedFlash == item.Id && isl.Clock.Now - copiedAt < 1.2;
                c.Text(flash ? "Copied" : item.At.ToString("t"), new Box(row.Right - 112, row.Y, 64, 40), 11, (flash ? Col.Hex(0x34c759) : Col.White).A(0.5f * t), FontWeight.SemiBold, HAlign.Right);
                if (flash) isl.Host.RequestFrame();
                var ci = item;
                ux.Add(id, new Box(row.X, row.Y, row.W - 40, row.H), () => { cb.Copy(ci); copiedFlash = ci.Id; copiedAt = isl.Clock.Now; }, 10);
                ux.IconButton(id + ":del", row.Right - 20, row.Y + 20, 26, Glyph.Delete, () => cb.Delete(ci), null, 11);
            }
            y += hgt + 4;
        }
        c.Unfade();
    }

    // ==== Timer ============================================================
    static readonly (string label, double secs)[] TimerPresets =
        { ("1 min", 60), ("3 min", 180), ("5 min", 300), ("10 min", 600), ("15 min", 900), ("25 min", 1500), ("30 min", 1800), ("1 hour", 3600) };

    void StartTyped()
    {
        if (CountdownTimer.Parse(timerBox.Text) is double secs)
        {
            isl.Svc?.Timer?.Start(secs);
            timerBox.Set(""); timerFocused = false; isl.ReleaseKeyboard();
        }
        else { timerBadAt = isl.Clock.Now; isl.Ux.Kick("timer:shake", 1); }
    }

    void Timer(Canvas c, Ux ux, Box p, float t)
    {
        var tm = isl.Svc?.Timer;
        if (tm == null) return;
        double now = isl.Clock.Now;
        var orange = Col.Hex(0xff9f0a);
        var col = tm.Paused ? Col.White.A(0.55f) : orange;
        float cx = p.X + 92, cy = p.CY;
        // the ring empties as the time runs down
        float left = tm.Running ? 1 - (float)tm.Fraction(now) : 0;
        c.Ring(cx, cy, 70, (tm.Running ? col : Col.White).A((tm.Running ? 0.18f : 0.1f) * t), 8);
        if (tm.Running) c.Arc(cx, cy, 70, 0, MathF.PI * 2 * Math.Max(0.001f, left), col.A(t), 8);
        double shown = tm.Running ? tm.Remaining(now) : (tm.Last > 0 ? tm.Last : 600);
        c.Text(Paint.Time(shown), Box.Centered(cx, cy - 4, 140, 40), shown >= 3600 ? 26 : 30, Col.White.A((tm.Running ? 1 : 0.55f) * t), FontWeight.SemiBold, HAlign.Center);
        // idle, the ring offers the last length again: click it to start
        if (!tm.Running)
        {
            var ring = Box.Centered(cx, cy, 150, 150);
            ux.Add("timer:ring", ring, () => tm.Start(shown), 75);
            float rh = ux.HoverT("timer:ring");
            if (rh > 0.01f) c.Ring(cx, cy, 70, orange.A(0.35f * rh * t), 8);
        }
        string caption = !tm.Running ? (ux.HoverT("timer:ring") > 0.5f ? "Start" : "Ready") : tm.Paused ? "Paused" : "ends " + tm.EndsAt(now).ToString(isl.Cfg.Use24Hour ? "HH:mm" : "h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
        c.Text(caption, Box.Centered(cx, cy + 24, 130, 18), 12, col.A(0.9f * t), FontWeight.SemiBold, HAlign.Center);

        float x = p.X + 210, w = p.Right - x;
        if (tm.Running)
        {
            c.Text(CountdownTimer.Describe(tm.Length) + " timer", new Box(x, p.Y + 22, w, 24), 18, Col.White.A(t), FontWeight.SemiBold);
            c.Text(tm.Paused ? "Paused - resume when you're ready" : "Counts down in the island; rings when it's done", new Box(x, p.Y + 50, w, 18), 12, Col.White.A(0.55f * t), FontWeight.Medium);
            float by = p.Bottom - 70;
            ux.Pill("timer:go", new Box(x, by, 130, 36), tm.Paused ? "Resume" : "Pause", tm.PauseResume, orange, tm.Paused ? Glyph.Play : Glyph.Pause, true, 13);
            ux.Pill("timer:more", new Box(x + 140, by, 100, 36), "+1 min", tm.AddMinute, Col.White, Glyph.Add, false, 13);
            ux.Pill("timer:stop", new Box(x + 250, by, 100, 36), "Cancel", tm.Cancel, Col.White, Glyph.Close, false, 13);
            return;
        }
        c.Text("Timer", new Box(x, p.Y + 14, w, 24), 18, Col.White.A(t), FontWeight.SemiBold);
        // a length it can't read: say so where the hint is (below the field it fell off the panel)
        bool bad = now - timerBadAt < 2.5;
        c.Text(bad ? "Try 7m, 90s, 1:30 or 1h 20m" : "Tap a length, or type one", new Box(x, p.Y + 40, w, 18), 12, bad ? Col.Hex(0xff6b5a).A(t) : Col.White.A(0.55f * t), FontWeight.Medium);
        float gap = 8, cw = (w - 3 * gap) / 4;
        for (int i = 0; i < TimerPresets.Length; i++)
        {
            var (label, secs) = TimerPresets[i];
            var chip = new Box(x + (i % 4) * (cw + gap), p.Y + 66 + (i / 4) * 38, cw, 30);
            ux.Pill("timer:p" + i, chip, label, () => tm.Start(secs), Col.White, '\0', false, 12);
        }
        // any length: a field and a Start button
        float shake = ux.Anim("timer:shake", 0, SpringSpec.Wobbly) * 6;
        var field = new Box(x + shake, p.Y + 148, w - 98, 36);
        float fh = ux.HoverT("timer:field");
        c.Fill(field, Col.White.A((timerFocused ? 0.13f : 0.08f + 0.04f * fh) * t), 12);
        if (timerFocused) c.Stroke(field, orange.A(0.6f * t), 12, 1.2f);
        timerBox.Draw(c, field.Inset(14, 0), 13, timerFocused, now, orange);
        ux.Add("timer:field", field, () => { timerFocused = true; noteFocused = false; isl.TakeKeyboard(); timerBox.Touch(now); }, 12, CursorKind.IBeam);
        ux.Pill("timer:start", new Box(field.Right + 8 - shake, field.Y, 90, 36), "Start", StartTyped, orange, Glyph.Play, true, 13);
    }

    // ==== Devices ==========================================================

    /// <summary>
    /// Whether the AirPods card shows: only while they're actually connected
    /// (radio on, a connected AirPods device). MagicPods keeps logging the last
    /// reading, so a reading from earlier in the day used to keep the card up
    /// with stale percentages long after the buds went back in the case.
    /// Without the Bluetooth service, a reading under two minutes old will do.
    /// </summary>
    internal static bool AirPodsLive(AirPodsState s, BluetoothService bt, double now) =>
        s != null && (s.Left >= 0 || s.Right >= 0) &&
        (bt != null ? bt.RadioOn == true && bt.Devices.Any(d => d.IsAirPods && d.Connected) : now - s.UpdatedAt < 120);
    void Devices(Canvas c, Ux ux, Box p, float t)
    {
        var bt = isl.Svc?.Bluetooth; var pods = isl.Svc?.AirPods;
        float y = p.Y;
        var row = new Box(p.X, y, p.W, 36);
        c.Icon(Glyph.Bluetooth, row.X + 12, row.CY, 16, Col.White.A(t));
        c.Text("Bluetooth", new Box(row.X + 32, row.Y, 200, row.H), 14, Col.White.A(t), FontWeight.SemiBold);
        if (bt?.Available == true)
        {
            ux.Switch("bt:radio", row.Right - 120, row.CY, bt.RadioOn == true, () => { if (!bt.Switching) _ = bt.SetRadio(bt.RadioOn != true); }, Col.Hex(0x0a84ff));
            // switching the radio takes a second or two; say it's happening. (This
            // once sat between the if above and its else, which then drew "No
            // radio" under the switch whenever it wasn't switching.)
            if (bt.Switching) Paint.Spinner(c, row.Right - 168, row.CY, 14, Col.White.A(0.75f), isl.Clock.Now, t);
        }
        else c.Text("No radio", new Box(row.Right - 200, row.Y, 120, row.H), 12, Col.White.A(0.5f * t), FontWeight.Medium, HAlign.Right);
        ux.IconButton("bt:settings", row.Right - 18, row.CY, 30, Glyph.Settings, () => Native.Win32.ShellExecute("ms-settings:bluetooth"), null, 13);
        y += 44;
        var s = pods?.State;
        if (AirPodsLive(s, bt, isl.Clock.Now))
        {
            var card = new Box(p.X, y, p.W, 96);
            c.Fill(card, Col.White.A(0.07f * t), 18);
            c.Icon(Glyph.Headphones, card.X + 30, card.CY - 10, 26, Col.White.A(t));
            c.Text(s.Name, new Box(card.X + 56, card.Y + 16, 220, 22), 15, Col.White.A(t), FontWeight.SemiBold);
            string ear = s.LeftInEar == true || s.RightInEar == true ? $"In ear: {(s.LeftInEar == true ? "L " : "")}{(s.RightInEar == true ? "R" : "")}" : s.LidOpen == true ? "Case open" : "In case";
            c.Text($"{ear} · via {s.Source}", new Box(card.X + 56, card.Y + 38, 260, 18), 11.5f, Col.White.A(0.5f * t), FontWeight.Medium);
            float rx = card.Right - 34;
            foreach (var (lbl, pct, chg) in new[] { ("Case", s.Case, s.CaseCharging), ("R", s.Right, s.RightCharging), ("L", s.Left, s.LeftCharging) })
            {
                Paint.BatteryRing(c, rx, card.CY - 6, 16, pct, chg, t);
                c.Text(lbl, Box.Centered(rx, card.CY + 22, 40, 14), 10.5f, Col.White.A(0.55f * t), FontWeight.SemiBold, HAlign.Center);
                rx -= 54;
            }
            y += 106;
        }
        if (bt == null) return;
        var list = bt.Devices.GroupBy(d => d.Name).Select(g => g.OrderByDescending(d => d.Connected).First())
            .OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
        c.Text("PAIRED", new Box(p.X, y, 200, 16), 10.5f, Col.White.A(0.45f * t), FontWeight.Bold);
        y += 20;
        var area = new Box(p.X, y, p.W, p.Bottom - y);
        c.ClipRect(area);
        float sc = ux.Anim("scroll:dev", scroll.GetValueOrDefault("dev"), ScrollSpring, 0.05);
        var devScroll = ux.Add("dev:scroll", area, null, 0, CursorKind.Arrow); devScroll.ScrollPx = 40; devScroll.Wheel = (d, h) => { scroll["dev"] = Math.Clamp(scroll.GetValueOrDefault("dev") - d / 120f * 40, 0, Math.Max(0, list.Count * 34 - area.H)); isl.Host.RequestFrame(); };
        y -= sc;
        // the watcher is still listing paired devices: rows in their shape
        if (list.Count == 0 && !bt.Enumerated)
            for (int k = 0; k < 3; k++) Paint.SkeletonRow(c, new Box(p.X, y + k * 34, p.W, 30), isl.Clock.Now, t * (1 - 0.2f * k), k);
        foreach (var d in list)
        {
            var r = new Box(p.X, y, p.W, 30);
            c.Icon(d.Glyph, r.X + 12, r.CY, 14, Col.White.A((d.Connected ? 0.95f : 0.45f) * t));
            c.Text(d.Name, new Box(r.X + 32, r.Y, r.W - 160, r.H), 12.5f, Col.White.A((d.Connected ? 0.95f : 0.5f) * t), FontWeight.Medium);
            string st = d.Connected ? (d.Battery >= 0 ? $"Connected · {d.Battery}%" : "Connected") : "Not connected";
            c.Text(st, new Box(r.Right - 160, r.Y, 150, r.H), 11.5f, (d.Connected ? Col.Hex(0x34c759) : Col.White).A(0.6f * t), FontWeight.Medium, HAlign.Right);
            y += 34;
        }
        c.Unclip();
    }
}
