# Store trailer

A 30-second trailer for the Store listing: a 3D laptop (Three.js) with the
README's real recordings on its screen, captions, and an original soundtrack
made with the sound lab's synthesizer (`music.mjs`, 120 BPM, every scene change
on a downbeat). Rendered frame by frame in a headless Edge, so the timing is
exact on any machine.

```powershell
python tools\readme-media\record_all.py           # the recordings it uses (dark versions)
cd store\trailer
python frames.py                                   # the recordings as frames, backdrop keyed out
# Three.js 0.169.0 into lib\ (build\three.module.js and the examples\jsm files scene.js imports)
# and the Store badge: https://get.microsoft.com/images/en-us%20dark.svg as store-badge.svg
node music.mjs                                     # music.wav
node capture.mjs 60 0 30                           # out\00000.png ...
ffmpeg -framerate 60 -i out\%05d.png -i music.wav -af "equalizer=f=4000:t=q:w=1.2:g=2.5,alimiter=limit=0.9" -c:v libx264 -preset slow -crf 15 -pix_fmt yuv420p -movflags +faststart -c:a aac -b:a 320k -shortest isola-trailer.mp4
```

`node capture.mjs still 5 12.5 ...` renders single frames to `stills\` for checking.
The output (frames, video, lib) stays out of git.
