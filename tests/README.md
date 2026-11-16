# Isola tests

The island depends on things a test can't unplug or take offline on demand:
displays, a Bluetooth radio, media sessions, Windows notifications, Home
Assistant, Spotify and GitHub. So these scenarios run the island's real code
against stand-ins for all of them.

Nothing is re-implemented. `Isola.Tests.csproj` compiles every file in
`..\src` except `Program.cs` (the island's `Main`) alongside the scenarios, so
what passes here is the shipping code.

```powershell
dotnet run -c Release                 # every scenario, about 6 s; exit code = failures
dotnet run -c Release -- Fullscreen   # only scenarios whose method name contains it
dotnet run -c Release -- --example-config ../isola.config.example.json
```

The last line regenerates the shipped example config from the real defaults
(a scenario fails if the two drift apart). `build.ps1` runs the suite before
every build and stops if anything fails.

During a run the services log to `scenarios.log` beside the test build (its
path is printed when something fails), never to a real log.

## What stands in for what

| Real thing | Stand-in (`Fakes.cs`) |
|---|---|
| `Stopwatch` time and the UI thread's timers | `FakeClock`: time moves only when a scenario says so, in 1/60 s frames, firing `Ui` timers and stepping the island as the real loop does |
| The two island windows | `FakeHost`: records hit areas, keyboard capture, frames requested, settings and menu requests, and drags started |
| `EnumWindows`, `GetForegroundWindow`, DWM cloaking | `FakeWindows`: a Z-ordered list of windows with rectangles, caption, maximized, minimized, cloaked and click-through flags |
| `QueryDisplayConfig` and the monitor list | `FakeDisplays`: a built-in panel (embedded DisplayPort) and a TV (HDMI), plugged, unplugged and made primary at will |
| Home Assistant | `FakeHa`, an `HttpMessageHandler` answering `/api/states` from a dictionary, recording service calls (which a test can hold in flight or make throw), or failing with any status. A loopback `TcpListener` stands in for the server's port, because the client probes TCP before it sends anything |
| Windows media sessions | `FakeMedia`: a now-playing record and counters for play, pause, next and previous |
| The Bluetooth association-endpoint watcher | `BluetoothService.TestAdd`, which delivers exactly what the watcher's `Added` event does |
| MagicPods' log and Apple's BLE broadcast | Lines in MagicPods' log format, and a hand-built proximity-pairing advert |
| Everything's IPC reply | Hand-built `EVERYTHING_IPC_LIST2` buffers, well-formed and crafted to lie about their lengths |
| GitHub's releases API | `FakeGitHub`, an `HttpMessageHandler` serving a release list (some for another project, without the asset, or pre-release) and the asset download |
| Spotify's accounts service and Web API | `FakeSpotify` (in `Scenarios5.cs`): checks the PKCE verifier against the challenge, issues and rotates tokens, answers 401 to a stale access token and `invalid_grant` to a revoked refresh token, and serves the player, its volume and the profile. The browser is a function that makes the real loopback request to the island's listener |
| Windows' per-app notification settings | `FakeBanners` (in `Scenarios7.cs`), an in-memory `IBannerStore`; the registry walk itself (`RegistryBanners.LeafApps`) runs over a dictionary shaped like the real keys, nested app ids included |
| Starting the downloaded setup | `Updater.StartInstaller`, replaced by a function that records the call and succeeds, fails or throws |

## Layout

| | |
|---|---|
| `Program.cs` | Finds every `[Scenario]`, runs it, prints pass or FAIL with the reason |
| `Fakes.cs` | The stand-ins above |
| `Scenarios.cs` | Springs (overshoot and timing, interruption, frame-rate independence) and the state machine: hello, auto-hide, push up to hide, a notice while hidden, fullscreen, alert queueing, glow colors while paused, swipes |
| `Scenarios2.cs` | Fullscreen per screen by Z order, finding the built-in panel, display changes settling, MagicPods and BLE parsing, Everything's reply, launcher answers, choosing a release, Home Assistant reachability, config robustness, hotkeys, the shelf, stale Bluetooth records |
| `Scenarios3.cs` | Search noise from Home Assistant switches, the waveform with no signal, a stale AirPods card, the example config staying in sync, a Home Assistant call in flight |
| `Scenarios4.cs` | The updater end to end against a fake GitHub: a setup that won't start leaves the island running, a good one starts before the island quits, a download that doesn't match is never started. Media timeline reports, and the resting eyes drawing only while the pointer moves |
| `Scenarios5.cs` | Spotify: PKCE and the loopback reply, signing in end to end, the volume slider's throttling, token refresh and revocation, and the six-month sign-in |
| `Scenarios6.cs` | The timer, Preview popups, app icons as PNG, a late second leave notice, Home Assistant scene times, Find My names, and Phone Link's phone notifications |
| `Scenarios7.cs` | Bursts of notifications and calls, battery states, desktops, Windows' banners (journal first, restore exactly, after a crash, nested app ids, chimes), hover and reveal intent, the sign-in task, the built-in search index, AirPods without MagicPods, the glow taking a notification's colors, the setup wizard, crash restarts, track popups staying calm, the Home tab only while usable, tokens encrypted at rest, logs without message text, crafted search replies, media sessions settling before they take over, sound previews and their lead-in, and recognizing Bluetooth outputs |
| `Scenarios8.cs` | Replacing the GitHub install with the Store version and back: the package family name against the Partner Center identity, whose settings win, nothing cleared before the other copy is really gone, the uninstaller never run with `/wipe`, and the Store version's question leaving no quit message for the island that starts after it |

## A regression test must fail without its fix

Each scenario written for a bug was checked by putting the bug back,
rebuilding, and watching that scenario fail, then restoring the fix. A few
examples of what they report with the bug back:

| Scenario | Fails with the bug back as |
|---|---|
| `EverythingReplyBounds` | the test process crashes reading past the reply buffer |
| `TokensEncryptedAtRest` | the token isn't on disk in plain text |
| `LogKeepsNoMessageText` | no names or messages: `notification: Jordan Smith \| WhatsApp` |
| `HomeTabOnlyWhenUsable` | set up and reachable, with the island closed: Home tab |
| `NotificationBurstQueues` | at most 4 waiting individually, got 29 |
| `BannersJournalAfterCrash` | Discord back: expected nothing, got 0 |
| `UpdaterHandsOverSafely` | the setup didn't start, so the island must not quit |
| `ConfigKeepsUnknownSettings` | an unknown string setting survives a save: expected abc123, got nothing |

One exception, stated plainly: `ImageDecodeStorm` covers the move to native
image decoding, but it doesn't reproduce the original crash with the old code,
which only happened under real media session churn.

## Checked by hand on a real PC

Drawing and some Windows APIs can't run in the suite, so each change is also
checked on the real island. Set `ISOLA_ZONES` to a folder and each window
writes the screen rectangle of every click target there (`island.txt`,
`settings.txt`), so a script can find a control by its id (`tab:home`,
`t:glowdrift`) instead of guessing where a spring-animated layout put it.
`ISOLA_HOME` points the island at a scratch data folder, and a stand-in Home
Assistant serves sample entities and logs the calls it gets.

For screenshots without any input, `ISOLA_SHOWCASE` opens a state at start
(`expand:home`, `search:12*4.5`, `popups`) and `ISOLA_BACKDROP=0d1117` draws a
solid color behind the island. The images in the main README are made this way
by the scripts in [`tools/readme-media`](../tools/readme-media/README.md).

## Not covered here

Drawing itself (checked by eye from screenshots), the real WinRT media,
Bluetooth and notification APIs, Phone Link's UI Automation, and the installer
(checked by running it: a fresh install, re-running it over a running island,
updating, and uninstalling with and without keeping data).
