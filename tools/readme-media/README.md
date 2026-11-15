# README media

The scripts that make the images and GIFs in the main README and the logos in
`assets/`. Re-record whenever a feature on the page changes, so the page
always shows the app as it is.

They run the development build on a made-up data folder (sample Home Assistant
devices, notes, weather), so nothing from your own PC appears, and they send no
keyboard or mouse input: each state is opened with the `ISOLA_SHOWCASE`
switch, over a solid backdrop drawn with `ISOLA_BACKDROP`.

Windows only. Needs Python 3 with Pillow (`pip install pillow`).

```powershell
dotnet build Isola.csproj                         # the Debug build the scripts run
python tools\readme-media\record_all.py           # every README image, dark and light
python tools\readme-media\record_all.py hello     # just one
python tools\readme-media\logos.py                # the logos and wordmarks
```

| | |
|---|---|
| `record_all.py` | Every README image, each in a dark (`#0d1117`, GitHub's dark page) and a white version, in the notch style |
| `record.py` | One take of one state. Closes the installed island for the take and starts it again afterwards |
| `sample_data.py` | Writes the sample data folder into `.work/` (ignored by git) |
| `stub_ha.py` | A stand-in Home Assistant serving the sample devices on `127.0.0.1:8125` |
| `logos.py` | The icon with the name, with and without the tagline, and the name alone |

A take keeps a frame only if the window's edges show the backdrop. Until the
island draws its first frame, its window shows whatever is behind it on the
desktop, so a take that never gets clean is thrown away instead of saved.

The README switches each image by theme with
`<picture><source media="(prefers-color-scheme: dark)" ...>`, so neither theme
shows a box around them.
