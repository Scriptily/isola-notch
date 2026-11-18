# Store trailer

A 30-second trailer for the Store listing: a 3D laptop (Three.js) with the
README's real recordings on its screen, captions, and a soundtrack cut to it.
Rendered frame by frame in a headless Edge, so the timing is exact on any
machine.

The music is "Ramp It Up" from Mixkit (https://mixkit.co/free-stock-music/,
track 69), under the Mixkit Stock Music Free License: free for commercial use,
including online ads, with no attribution. That license doesn't allow sharing
the track on its own, so it isn't in the repository: download it as
`tracks/69.mp3`. It runs at 120 BPM (119.84, nudged 0.13%); started 11.617 s
in, its drop lands on the first cut at 4 s and every scene change after is
on a downbeat.

```powershell
python tools\readme-media\record_all.py           # the recordings it uses (dark versions)
cd store\trailer
python frames.py                                   # the recordings as frames, backdrop keyed out
# Three.js 0.169.0 into lib\ (build\three.module.js and the examples\jsm files scene.js imports)
# and the Store badge: https://get.microsoft.com/images/en-us%20dark.svg as store-badge.svg
ffmpeg -i tracks\69.mp3 -af "atrim=start=11.617,asetpts=PTS-STARTPTS,atempo=1.00134,atrim=end=30,afade=t=in:d=0.4,afade=t=out:st=27:d=3:curve=qsin,highshelf=f=7000:g=-3,loudnorm=I=-16:TP=-1.5:LRA=11,volume=-2dB" -ar 48000 music.wav
node capture.mjs 60 0 30                           # out\00000.png ...
ffmpeg -framerate 60 -i out\%05d.png -i music.wav -c:v libx264 -preset slow -crf 15 -pix_fmt yuv420p -movflags +faststart -c:a aac -b:a 320k -shortest isola-trailer.mp4
```

`node capture.mjs still 5 12.5 ...` renders single frames to `stills\` for checking.
The output (frames, video, lib) stays out of git.

The Store screenshots come from the same scene: one composed frame each, the
island on the laptop's screen with the trailer's captions (`shots.json` lists
them):

```powershell
node capture.mjs shots shots.json ..\screenshots
```

The 16:9 Super hero art (artwork only, no text: the Store needs it for the
trailer to play at the top of the listing, and shows it after the trailer):

```powershell
node capture.mjs shots art.json ..\screenshots\art
```
