using Isola.Services;

namespace Isola.App;

/// <summary>
/// What plays when an alert comes on screen and stops when it goes: its chosen sound at the chosen level,
/// quieter or not at all while media plays (by the setting), with the media dipped under it first. Its own
/// class so the tests drive the real thing with the island, a fake clock and fake playback.
/// </summary>
internal sealed class AlertSounds
{
    readonly Func<Config> cfg;
    readonly Func<bool> playing, heard;
    readonly Ducker ducker;
    internal Action<string, float, string> Play = Sounds.Play;
    internal Action Stop = Sounds.Stop;
    internal Func<string, string, double> Length = Sounds.Length;
    long pending; Alert pendingFor;

    /// <param name="playing">Media is playing.</param>
    /// <param name="heard">And can be heard (not paused, not at 0): only then is there anything to dip.</param>
    public AlertSounds(Func<Config> cfg, Func<bool> playing, Func<bool> heard, Ducker ducker)
    { this.cfg = cfg; this.playing = playing; this.heard = heard; this.ducker = ducker; }

    public void On(Alert a)
    {
        var c = cfg();
        if (!App.SoundAllowed(c, a)) return;
        // media that says it's playing but makes no sound (Spotify left "playing" at 0%, or paused without telling
        // Windows) isn't playing for this: the chime keeps its own volume, and there's nothing to dip
        bool media = playing() && heard();
        float v = App.SoundVolumeOf(c) * App.WhilePlayingScale(c, a.Sound, media);
        if (v <= 0) return;   // muted while media plays: nothing to make room for either
        string kind = a.Sound, tone = App.ToneFor(c, kind);
        // the media dips first, then the sound plays into the room it made
        double wait = ducker != null && App.DuckFor(c, media) ? ducker.Duck(Length(kind, tone), Sounds.Loops(kind) ? a : null) : 0;
        Ui.Cancel(ref pending); pendingFor = null;
        if (wait < 0.005) Play(kind, v, tone);
        else { pendingFor = a; pending = Ui.After(wait, () => { pending = 0; pendingFor = null; Play(kind, v, tone); }); }
    }

    public void Off(Alert a)
    {
        if (pendingFor == a) { Ui.Cancel(ref pending); pendingFor = null; }   // gone before its sound started
        if (a.Sound != null && Sounds.Loops(a.Sound)) Stop();
        ducker?.Release(a);
    }
}
