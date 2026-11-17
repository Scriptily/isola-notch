using Isola.App;
using Isola.Render;
using Isola.Services;
using Vortice.Mathematics;
using Box = Isola.Render.Box;
using FontWeight = Vortice.DirectWrite.FontWeight;

namespace Isola.Island;

/// <summary>
/// The Shelf tab's downloading: a link bar on top (paste, choose video or
/// audio, press Enter), the job in progress under it, a Downloads panel with
/// every job and its history, and Convert on any video or audio kept on the
/// shelf.
/// </summary>
public sealed partial class DashboardPage
{
    readonly TextBox linkBox = new() { Placeholder = "Paste a link to download a video or its audio", MaxLength = 2000 };
    bool linkFocused, linkAudio, dlPanel;
    ShelfItem convertFor;
    double linkBadAt = -10; string linkBad = "";

    static readonly Color4 Green = Col.Hex(0x34c759), Orange = Col.Hex(0xff9f0a), Blue = Col.Hex(0x0a84ff);

    /// <summary>Recordings: the link goes into the bar a few characters at a time, then it starts.</summary>
    internal void ShowcaseLink(string link, bool audio)
    {
        linkFocused = true; linkAudio = audio; linkBox.Set("");
        isl.Host.RequestFrame();
        int step = Math.Max(1, link.Length / 14);
        for (int i = step, k = 1; i < link.Length + step; i += step, k++)
        {
            string part = link[..Math.Min(i, link.Length)];
            Ui.After(k * 0.05, () => { linkBox.Set(part); isl.Host.RequestFrame(); });
        }
        Ui.After((link.Length / step + 2) * 0.05 + 0.6, StartLink);
    }

    /// <summary>The next time the Shelf tab draws, it shows the Downloads panel.</summary>
    public void OpenDownloads() { dlPanel = true; convertFor = null; }

    void InitDownloads()
    {
        linkBox.Submit = StartLink;
        linkBox.Escape = () => { linkFocused = false; isl.ReleaseKeyboard(); };
    }

    void StartLink()
    {
        var dl = isl.Svc?.Downloads;
        string s = linkBox.Text.Trim();
        if (dl == null || s.Length == 0) return;
        if (!Downloads.IsLink(s)) { Bad("That isn't a web link (it starts with https://)"); return; }
        try { dl.Download(s, linkAudio); linkBox.Set(""); linkFocused = false; isl.ReleaseKeyboard(); }
        catch (Exception ex) { Bad(ex.Message); }
    }

    void Bad(string why) { linkBadAt = isl.Clock.Now; linkBad = why; isl.Ux.Kick("link:shake", 14); isl.Host.RequestFrame(); }

    /// <summary>The job worth showing under the link bar: one running, or one that just ended.</summary>
    DownloadJob RowJob(Downloads dl)
    {
        var cur = dl.Current;
        if (cur != null) return cur;
        var last = dl.Jobs.FirstOrDefault();
        return last?.Finished is DateTime f && (DateTime.Now - f).TotalSeconds < 8 ? last : null;
    }

    void LinkBar(Canvas c, Ux ux, Box b, float t)
    {
        float shake = ux.Anim("link:shake", 0, SpringSpec.Wobbly);
        var field = b.Offset(shake, 0);
        float fh = ux.HoverT("link:field");
        bool bad = isl.Clock.Now - linkBadAt < 2.5;
        c.Fill(field, Col.White.A((linkFocused ? 0.13f : 0.08f + 0.04f * fh) * t), 12);
        if (linkFocused || bad) c.Stroke(field, (bad ? Col.Hex(0xff6b5a) : Paint.Accent(isl)).A(0.6f * t), 12, 1.2f);
        c.Icon(Glyph.Link, field.X + 18, field.CY, 13, Col.White.A(0.6f * t));
        // video or audio, and go: inside the field's right end
        float segW = 132, goW = 30;
        var seg = new Box(field.Right - segW - goW - 10, field.Y + 4, segW, field.H - 8);
        c.Fill(seg, Col.White.A(0.06f * t), seg.H / 2);
        float knob = ux.Anim("link:seg", linkAudio ? 1 : 0, SpringSpec.Snappy);
        var thumb = new Box(seg.X + 2 + knob * (seg.W / 2 - 2), seg.Y + 2, seg.W / 2 - 2, seg.H - 4);
        c.Fill(thumb, Col.White.A(0.16f * t), thumb.H / 2);
        for (int i = 0; i < 2; i++)
        {
            var half = new Box(seg.X + i * seg.W / 2, seg.Y, seg.W / 2, seg.H);
            bool on = (i == 1) == linkAudio;
            c.Icon(i == 0 ? Glyph.Video : Glyph.Music, half.X + 17, half.CY, 11, Col.White.A((on ? 0.95f : 0.5f) * t));
            c.Text(i == 0 ? "Video" : "Audio", new Box(half.X + 27, half.Y, half.W - 30, half.H), 11.5f, Col.White.A((on ? 0.95f : 0.5f) * t), FontWeight.SemiBold);
            bool audio = i == 1;
            ux.Add("link:seg" + i, half, () => { linkAudio = audio; isl.Host.RequestFrame(); }, half.H / 2);
        }
        bool ready = linkBox.Text.Trim().Length > 0;
        ux.IconButton("link:go", field.Right - goW / 2 - 6, field.CY, goW, Glyph.Download, StartLink, ready ? Blue : null, 13);
        var tb = new Box(field.X + 34, field.Y, seg.X - field.X - 40, field.H);
        if (bad && !linkFocused && linkBox.Text.Length == 0) c.Text(linkBad, tb, 12.5f, Col.Hex(0xff6b5a).A(t), FontWeight.Medium);
        else linkBox.Draw(c, tb, 13, linkFocused, isl.Clock.Now, Paint.Accent(isl));
        ux.Add("link:field", new Box(field.X, field.Y, seg.X - field.X - 4, field.H), () =>
        {
            linkFocused = true; noteFocused = false; timerFocused = false; isl.TakeKeyboard(); linkBox.Touch(isl.Clock.Now);
            // a link on the clipboard is what you came to paste: in it goes, ready for Enter
            if (linkBox.Text.Length == 0 && Native.Win32.GetClipboardText()?.Trim() is { } clip && Downloads.IsLink(clip)) linkBox.Set(clip, true);
        }, 12, CursorKind.IBeam);
    }

    /// <summary>A job as a row: its ring or result, its name, what it's doing, and the one thing to do with it.</summary>
    void JobRow(Canvas c, Ux ux, DownloadJob j, Box row, float t, bool panel)
    {
        string id = "job:" + j.Id;
        float h = ux.HoverT(id);
        c.Fill(row, Col.White.A((0.06f + 0.03f * h) * t), 12);
        float cx = row.X + 22, cy = row.CY;
        char kindGlyph = j.Kind == "audio" ? Glyph.Music : j.Kind == "convert" ? Glyph.Convert : Glyph.Video;
        if (j.Active)
        {
            if (j.State != "running" || j.Progress <= 0.001) Paint.Spinner(c, cx, cy, 16, Col.White.A(0.8f), isl.Clock.Now, t);
            else
            {
                float pr = ux.Anim(id + ":p", j.Progress, SpringSpec.Smooth);
                c.Ring(cx, cy, 10, Blue.A(0.25f * t), 2.4f);
                c.Arc(cx, cy, 10, 0, MathF.PI * 2 * Math.Max(0.001f, pr), Blue.A(t), 2.4f);
                c.Icon(kindGlyph, cx, cy, 8, Col.White.A(0.85f * t));
            }
        }
        else if (j.State == "done")
        {
            float d = ux.AnimFrom(id + ":done", 0, 1, SpringSpec.Content);
            c.Circle(cx, cy, 11 * (0.8f + 0.2f * d), Green.A(t));
            c.DrawnCheck(cx, cy, 15, (d - 0.15f) / 0.85f, Col.White.A(t), 2.2f);
        }
        else c.Icon(j.State == "cancelled" ? Glyph.Close : Glyph.Warning, cx, cy, 14, (j.State == "cancelled" ? Col.White.A(0.5f) : Orange).A(t));

        float right = row.Right - 10;
        // the action at the right end: cancel while it runs, then open, or try again
        if (j.Active) { ux.IconButton(id + ":cancel", right - 13, cy, 26, Glyph.Close, () => isl.Svc.Downloads.Cancel(j), null, 10); right -= 32; }
        else if (j.State == "done" && File.Exists(j.Output))
        {
            ux.IconButton(id + ":folder", right - 13, cy, 26, Glyph.Folder, () => Services.Shell.RevealInExplorer(j.Output), null, 11);
            ux.Pill(id + ":open", new Box(right - 30 - 64, cy - 13, 62, 26), "Open", () => Native.Win32.ShellExecute(j.Output), Col.White, '\0', false, 11.5f);
            right -= 100;
        }
        else if (j.State is "failed" or "cancelled")
        {
            ux.Pill(id + ":retry", new Box(right - 84, cy - 13, 84, 26), "Try again", () => { try { isl.Svc.Downloads.Retry(j); } catch (Exception ex) { Log.W("downloads", ex.Message); } }, Col.White, '\0', false, 11.5f);
            right -= 90;
        }
        if (panel && !j.Active && h > 0.3f) { ux.IconButton(id + ":x", right - 11, cy, 22, Glyph.Delete, () => isl.Svc.Downloads.Remove(j), null, 10); right -= 26; }
        var text = new Box(row.X + 42, row.Y, right - row.X - 46, row.H);
        c.Text(j.Label, new Box(text.X, row.Y + 5, text.W, 18), 12.5f, Col.White.A(0.92f * t), FontWeight.SemiBold);
        c.Text(Describe(j), new Box(text.X, row.Y + 22, text.W, 16), 11, (j.State == "failed" ? Orange : Col.White.A(0.55f)).A(t), FontWeight.Medium);
        if (j.Active && j.State == "running" && j.Progress > 0.001)
        {
            float pr = ux.Anim(id + ":bar", j.Progress, SpringSpec.Smooth);
            var bar = new Box(row.X + 12, row.Bottom - 3, row.W - 24, 2);
            c.Fill(bar, Col.White.A(0.08f * t), 1);
            c.Fill(new Box(bar.X, bar.Y, bar.W * pr, bar.H), Blue.A(t), 1);
        }
        ux.Add(id, row, j.State == "done" && File.Exists(j.Output) ? () => Native.Win32.ShellExecute(j.Output) : null, 12);
    }

    static string Describe(DownloadJob j)
    {
        if (j.State == "failed") return j.Error;
        if (j.State == "cancelled") return "Cancelled";
        if (j.State == "done")
        {
            string where = j.ToShelf ? "on the shelf" : Path.GetDirectoryName(j.Output) is { } d && string.Equals(d, KnownFolders.Downloads, StringComparison.OrdinalIgnoreCase) ? "in Downloads" : "in " + Path.GetFileName(Path.GetDirectoryName(j.Output));
            long size = File.Exists(j.Output) ? new FileInfo(j.Output).Length : 0;
            return $"{Path.GetExtension(j.Output).TrimStart('.').ToUpperInvariant()} · {Size(size)} · {where}";
        }
        if (j.State != "running" || j.Progress <= 0.001) return j.Stage;   // before there's anything to count, just what it's doing
        var parts = new List<string> { j.Stage, $"{j.Progress * 100:0}%" };
        if (j.Speed > 0) parts.Add(Size((long)j.Speed) + "/s");
        if (j.Eta > 0) parts.Add(j.Eta < 60 ? $"{j.Eta:0} s left" : $"{j.Eta / 60:0} min left");
        return string.Join(" · ", parts);
    }

    static string Size(long b) => b >= 1_000_000_000 ? $"{b / 1e9:0.0} GB" : b >= 1_000_000 ? $"{b / 1e6:0.0} MB" : $"{Math.Max(1, b / 1000)} KB";

    void DownloadsPanel(Canvas c, Ux ux, Box p, float t)
    {
        var dl = isl.Svc.Downloads;
        c.Text("Downloads", new Box(p.X + 4, p.Y - 2, 200, 18), 13, Col.White.A(0.9f * t), FontWeight.SemiBold);
        ux.Pill("dl:back", new Box(p.Right - 92, p.Y - 4, 92, 22), "Shelf", () => { dlPanel = false; isl.Host.RequestFrame(); }, Col.White, '\0', false, 11);
        if (dl.Jobs.Any(j => !j.Active)) ux.Pill("dl:clear", new Box(p.Right - 92 - 108, p.Y - 4, 100, 22), "Clear history", dl.ClearHistory, Col.White, '\0', false, 11);
        var list = new Box(p.X, p.Y + 26, p.W, p.H - 26);
        if (dl.Jobs.Count == 0)
        {
            c.Text("Nothing downloaded yet. Paste a link above, or type \"download\" and a link in search.", list.Inset(10, 30), 12, Col.White.A(0.45f * t), FontWeight.Normal, HAlign.Center, VAlign.Top, true);
            return;
        }
        const float rh = 50;
        float sc = ux.Anim("scroll:dl", scroll.GetValueOrDefault("dl"), ScrollSpring, 0.05);
        ux.Add("dl:scroll", list, null, 0, CursorKind.Arrow).Wheel = (d, h) => { scroll["dl"] = Math.Clamp(scroll.GetValueOrDefault("dl") - d / 120f * 50, 0, Math.Max(0, dl.Jobs.Count * rh - list.H)); isl.Host.RequestFrame(); };
        c.FadeClip(list, sc > 0.5f, sc < dl.Jobs.Count * rh - list.H - 0.5f);
        for (int i = 0; i < dl.Jobs.Count; i++)
        {
            var row = new Box(list.X, list.Y + i * rh - sc, list.W, rh - 6);
            if (row.Bottom < list.Y || row.Y > list.Bottom) continue;
            JobRow(c, ux, dl.Jobs[i], row, t, true);
        }
        c.Unfade();
    }

    /// <summary>Converting a file on the shelf: the formats it can become, in place of the shelf's header.</summary>
    void ConvertBar(Canvas c, Ux ux, Box b, float t)
    {
        var item = convertFor;
        c.Text("Convert to", new Box(b.X + 4, b.Y - 2, 80, 18), 11, Col.White.A(0.55f * t), FontWeight.SemiBold);
        float x = b.X + 76;
        foreach (var f in Downloads.FormatsFor(item.Path))
        {
            string label = f == "small" ? "Smaller" : Downloads.FormatName(f);
            float w = c.Measure(label, 11).w + 22;
            if (x + w > b.Right - 80) break;
            string fmt = f;
            ux.Pill("conv:" + f, new Box(x, b.Y - 4, w, 22), label, () =>
            {
                try { isl.Svc.Downloads.Convert(item.Path, fmt); } catch (Exception ex) { Log.W("downloads", ex.Message); }
                convertFor = null; isl.Host.RequestFrame();
            }, Col.White, '\0', false, 11);
            x += w + 6;
        }
        ux.Pill("conv:cancel", new Box(b.Right - 70, b.Y - 4, 70, 22), "Cancel", () => { convertFor = null; isl.Host.RequestFrame(); }, Col.White, '\0', false, 11);
    }
}
