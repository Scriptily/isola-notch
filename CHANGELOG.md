# Changelog

Version numbers are MAJOR.MINOR.PATCH. The major number changes only for big
overhauls. The minor number goes up for new features or substantial changes
(by more than one when a release brings many), and the patch number goes up by
the number of changes in a release that only fixes and adjusts things, so
1.4.0 with two fixes is 1.4.2. Each release's section below is also its GitHub
release notes.

## [Unreleased]

### Added

- A short notice names any app that starts or stops using the camera or
  microphone (Settings -> Notifications & Sounds -> Privacy, on by default).
  It's silent, and an app that uses them on and off all day, like a game
  recorder, only shows once in a while. Isola reads Windows' own record of
  this and never uses the camera or microphone itself.
- "Come back when media starts" (Settings -> Behavior, on by default): an
  island that tucked itself away after inactivity comes back when music or a
  video starts. One you hid yourself stays hidden.

### Changed

- A notice that changes in place, like the sound output as you cycle through
  them, fades from the old text to the new instead of swapping it.

### Fixed

- A text from your phone showed Phone Link's icon. It now shows as Messages
  with the sender's initials, or a message icon when the sender is a number,
  and an app relayed from your phone without its icon shows its first letter.
  Phone Link's own icon is only for Phone Link's own notices.
- A long notification was cut off after two lines. It shows up to three, as
  many as it really wraps to, and anything longer scrolls up slowly so you can
  read it all in the time it's on screen. Recent shows each message on up to
  three lines too, and longer ones scroll while you point at them.
- A code after a colon, like "رمز التسليم: 3036", gets a Copy button, also in
  Arabic and a few other languages. A message that only mentions a code, like
  "could you provide an access code", no longer offers to copy a number from
  somewhere else in it.
- A Home Assistant tile you tapped went back to how it was a second later,
  then changed again once Home Assistant caught up (a cloud device like an
  air conditioner takes seconds to report). The tile now keeps showing what
  you asked for until Home Assistant reports it, for up to 15 seconds, and
  goes straight back if the change couldn't be sent.
- Stepping through an air conditioner's modes went back and forth, and could
  turn it off. Modes are now a list to pick from, without Off, since tapping
  the tile turns it on and off. Home Assistant select tiles use the same list.
- Home Assistant tiles have even space above and below, and the air
  conditioner's temperature buttons line up with its mode list.
- The Bluetooth switch in Devices sits beside its settings button.
- A video playing by itself never counted as heard, so a Spotify paused for
  hours, the one on the island, kept its place: the island didn't switch to
  the video, and notification sounds ignored Quieter and didn't lower it.

## [1.7.0] - 2026-11-19

### Added

- Search suggests as you type and answers some questions itself, with no
  browser: definitions (`subsidized meaning`), translations
  (`translate subsidized to arabic`) and Wikipedia summaries
  (`who is alan turing`). Enter copies the answer. Off until you turn on
  Settings -> Search & Keys -> Web suggestions and answers, since it sends
  what you type to Google, the dictionary and Wikipedia. File and folder
  paths are never sent. See PRIVACY.md.
- While you're focused (Settings -> Notifications & Sounds): notification,
  call and notice sounds can play at full volume, quieter or not at all while
  media plays. The playing app dips while a chime or ringtone plays and comes
  back up gradually, the way a phone does (it always returns to its exact
  volume, even after a crash, and a volume you change meanwhile stays yours).
  Another setting holds notifications, calls and notices while
  media plays or a game or video is fullscreen on any screen. They come back
  as one summary when it stops, when fullscreen ends, or when you open the
  island. Timers and low battery always come through.
- "Stay while media plays" (Settings -> Behavior, on by default): music or a
  video keeps the island on screen, and the inactivity timer starts when it
  stops. Turned off, the island hides after inactivity as before.
- The island shows Isola's own updates: when one is found, how far it has
  downloaded, and when it starts installing.
- The download tools (yt-dlp, FFmpeg, QuickJS) show their progress on the
  island, with one notice when they're ready, updated or removed.
- A title too long for the island slides slowly back and forth so you can
  read it, its edges fading where there's more.
- A Recent tab lists every notification, call and notice the island showed
  since Isola started.
- The Weather tab switches between the next hours and the next days. Days
  show their high and low, rain chances show in blue, and clicking an hour
  or a day opens its details: feels like, rain, humidity, wind, UV and, for
  today, sunrise and sunset.
- A download, timer, update or tool download in progress also shows when you
  hover over the island or open it, not only in the pill.
- A soft sound for notices: fully charged, on battery, a display or sound
  output change, updates and the download tools, with its own switch and
  choice of four tones in Settings -> Notifications & Sounds.

### Changed

- Notifications and notices that arrive while you're typing in search wait
  until it closes, with a note in the search bar saying how many. Before, a
  banner dropped over the box you were typing in. A call or a timer still
  comes through, and when it's over search comes back with what you'd typed.
  If you moved to another app meanwhile, the text is there the next time you
  open search.
- The pill shows the song's artist after the title, in grey: "The Break Up -
  mgk". A new song pops up with every artist for a moment, then tucks back
  into one line, and the pill crossfades from one song to the next.
- A notification that arrives while the island is open drops in as a banner
  at its top instead of waiting until you close it. Clicking a notification
  or a call dismisses it, and hovering keeps it on screen.
- Every list, and Settings, shows a scroll bar when it has more to show. It
  stays dim until the pointer is over the list, the list makes room for it, and
  you can drag it or click along it.
- Weather icons follow the real sunrise and sunset instead of a fixed 6 am
  and 7 pm.
- The tabs scroll sideways when they don't all fit, and the open island never
  grows wider than its screen.
- Choosing which player to show works for any number of them. The one you
  can hear comes first, and of two you can hear, the one started last.
  Pausing the one on the island keeps it there, and a player picked by hand
  stays until another one starts.
- Switching between apps crossfades, in the pill and in Now Playing, and
  notices crossfade into each other.
- The waveform eases between moving and still, and lies still while the app
  makes no sound.
- The glow takes the song's colors only while the island shows the song. On
  the eyes, or the plain island, it goes back to your theme's colors. A
  notification or call shows its app's colors, or your theme's if it has none,
  and the song's come back when it goes. A new song's popup keeps the song's.
- New default sounds: Bloom for notifications, Warm for calls, Triple for the
  timer, Bubble for devices, Energy for the charger and Sink for low battery.
  Sounds you picked yourself stay as they are.
- Tooltips in Settings appear sooner, and they and the "?" popovers fade away
  instead of vanishing.
- The bell that opens Recent sits beside settings, and the tabs are centered
  between the buttons, with even space around each label.

### Fixed

- A phone call shows the caller's initials, as the iPhone does, instead of
  Phone Link's app icon (which only showed when looking it up worked, and
  said nothing about who was calling). A call relayed from a phone app, like
  WhatsApp, keeps that app's icon.
- A player turned to 0% in its own app (Spotify, for one) still moved the
  waveform and could take the island from what you were listening to.
  Isola now measures each app's own sound, for any player: Store apps like
  Apple Music and Media Player, browsers, Plex and the rest.
- Play and pause could show the wrong state when the app was slow to
  report, so the next press went the wrong way.
- Clicking along the progress bar briefly jumped back to the old position.
- Switching to Spotify with the island open squeezed its volume row against
  the controls until the island was closed and opened again.
- Tooltips in Settings waited for the mouse to move before they appeared.
- The Shelf's "Keep what's on the clipboard" did nothing for copied files and
  pictures; it keeps those now, and says so when the clipboard is empty.
- Pressing Tab in search typed a tab instead of moving to the result's actions.
- Sliding titles and scrolling lists moved in visible steps; they glide now.
- The glow faded to the theme's colors while a song popup slid away into the
  hidden island.
- Opening onto a tall tab, like Weather, made the island swell past its
  height and then shrink back.
- Opening Home with many tiles hitched, and showed a scroll bar for a moment.
  What the island shows is now laid out once, at the size it's growing to.
- Opening the island on Home before Home Assistant had connected kept another
  tab's height.
- Grabbing a scroll bar's thumb made the list jump to the pointer; it now
  moves only as you drag.
- The Weather tab's footer sat against the forecast, and the Shelf's button
  had more space after its label than before its icon.

### VirusTotal

1 of 71 security vendors flagged `IsolaSetup.exe` on 2026-11-19. [Full report](https://www.virustotal.com/gui/file/3c2cf2a4de4b9ea25f2ae1ba82abc27082a80c3572759ddd4eda018068dd7ac9)

- Bkav: W32.Malware.EDBC4E18

The setup isn't code-signed yet, and a new unsigned program often trips a few engines' heuristics. The source and the build are public, and the SHA-256 in the release notes is the file this report covers.

## [1.6.0] - 2026-11-19

### Added

- Touchscreen support. Tap the island to open it, drag sliders and the
  progress bar, scroll lists by dragging, swipe to change tabs or songs, and
  pull the island down to search. A tap just beside a button still presses
  it, and buttons that only showed on hover stay visible after a touch.
  Settings scrolls with a finger, stretching a little at either end.
- Right-click the island to hide it at once, instead of dragging it up.
  Right-click the top edge to bring it back.

### Changed

- Pressed buttons shrink a little less (to 97%), the same amount everywhere.
- New items on the Shelf and the help popovers in Settings fade in from 97%
  instead of growing from much smaller.
- The setup wizard slides each new page in from the right, and from the left
  when you go back.
- When you let go of the island after pulling it, it springs back at the
  speed it was actually moving.
- The island gets in the way less. It opens on hover only once the pointer
  has rested on it for 450 ms (it was 200), never while a mouse button is held,
  and not for a moment after you click near the top of the screen. Once
  hidden, it comes back only for a pointer resting at the top edge right above
  it, not anywhere across half the screen, and not when the pointer just
  passes over it.
- Dragging the island with the mouse does what you meant. A small nudge up no
  longer hides it, a drag that stops before you let go no longer flings it,
  and dragging it sideways skips the song or changes the tab instead of
  opening it.

### Fixed

- An island opened with a tap closed again a second later.
- Sliders couldn't be dragged with a finger.
- Tooltips in Settings never appeared. Now the first one shows after a short
  pause, and the next ones show right away.
- With two apps playing, a song changing in one you couldn't hear (Spotify at
  0% under a YouTube video) took over the island. The island now shows the
  app you can hear, and its waveform follows that app's sound instead of
  everything the speakers play.

### VirusTotal

1 of 65 security vendors flagged `IsolaSetup.exe` on 2026-11-19. [Full report](https://www.virustotal.com/gui/file/d85234310ac78bc3a27f522679572269ef0d58861e38df3b70438c48871f26bb)

- Bkav: W32.Malware.141CA1D4

The setup isn't code-signed yet, and a new unsigned program often trips a few engines' heuristics. The source and the build are public, and the SHA-256 in the release notes is the file this report covers.

## [1.5.2] - 2026-11-18

### Changed

- Settings: the Notifications tab is now Notifications & Sounds, since the
  island's sounds and their volume are set there too.

### VirusTotal

No security vendor flagged `IsolaSetup.exe`: 0 of 70 on 2026-11-18. [Full report](https://www.virustotal.com/gui/file/8fbfc0f3ae3bf0bd3812107b3947260b9ea4b04e07c6f0c9a6dda0bd565e889f)

## [1.5.1] - 2026-11-18

### Fixed

- Real phone calls showed as an ordinary notification and never rang. Phone
  Link puts the caller first and "Incoming call" under it, and 1.4.0 (while
  stopping messages that mention "calling" from ringing) only looked at the
  first line. A call is now recognized when "Incoming call" is a whole line of
  Phone Link's own notification, wherever it sits, and the caller shown is the
  name or number beside it.

### VirusTotal

1 of 66 security vendors flagged `IsolaSetup.exe` on 2026-11-18. [Full report](https://www.virustotal.com/gui/file/867c1d2f6c6dc6bfaba2bdaa55c64cc89dace594c913a4de7ff4baf93d40828c)

- Bkav: W32.Malware.220C3002

The setup isn't code-signed yet, and a new unsigned program often trips a few engines' heuristics. The source and the build are public, and the SHA-256 in the release notes is the file this report covers.

## [1.5.0] - 2026-11-18

### Added

- Downloads and conversion. Paste a link into the new bar at the top of the
  Shelf tab, choose Video or Audio, and press Enter: it downloads from YouTube
  and most other video and music sites, as an MP4 up to 1080p (or the best
  there is) or as MP3 or M4A. The progress shows in the pill while you do
  something else, and a popup says when it's done, with Open and Show in
  folder. Files go to your Downloads folder, a folder you choose, or the Shelf
  (Settings -> Downloads).
- Any video or song on the Shelf converts to MP4, MP3, GIF, M4A, WAV or WebM,
  or to a smaller MP4, from the button on its tile. Search results that are
  videos or songs offer it too.
- In search: `download` or `audio` and a link, `convert mp3` (or gif, mp4 and
  the rest), and `downloads` for the history. Pasting a link offers to
  download it.
- A Downloads panel on the Shelf lists every download and conversion, newest
  first, with its progress, a way to cancel, and Open, Show in folder or Try
  again when it's over.
- The work is done by yt-dlp, FFmpeg (the essentials build) and QuickJS. They
  aren't in the installer: Isola fetches them from each project's GitHub
  releases the first time you download (about 130 MB), checks each file
  against the checksum GitHub publishes, and updates them once a day. They're
  kept across Isola's updates and removed when you uninstall it. If Node.js is
  installed, YouTube downloads start about twice as fast.
- A sound volume setting, from 50% to 150%, with 100% in the middle. Letting go
  of the slider plays a chime at the new level. Your earlier volume carries
  over.

### Changed

- Sounds play the moment something happens. Each one used to start with a
  short silence (0.15 s, or 0.4 s on Bluetooth headphones) so a dozing output
  couldn't swallow its start. Now that silence is added only when the output
  really is asleep, not when music is playing or another sound just played.
  Each sound is also built once instead of for every volume, and Settings
  builds them all ahead, so a preview never waits.
- Checking a to-do in Notes draws its check mark instead of popping an icon in.
- The glow takes the colors of the cover of whatever is playing from the
  moment Isola starts, not only after the first track change.
- The README's animations are full-color WebP instead of GIF, so the glow no
  longer bands, and they show real songs, downloads and the Shelf.

### Fixed

- Three command icons in search were emoji (YouTube, UUID and Shut down). They
  use Isola's own icons now, like everything else.

### VirusTotal

No security vendor flagged `IsolaSetup.exe`: 0 of 71 on 2026-11-18. [Full report](https://www.virustotal.com/gui/file/e99fcd41007564a82cbdf56b80c23ac99bd89b5cb6f56f02ff30c9bf1fa8a1d8)

## [1.4.0] - 2026-11-17

### Added

- Settings -> Feedback: send a feature request or a bug report with a subject
  and a description, and optionally the log, with your account and computer names,
  folders, addresses, emails and tokens removed first. Each report carries a
  feedback ID, a one-way 16-digit number made from your computer, so reports from
  the same person can be told apart, and gets a report number of its own. It goes to
  the developer privately (a private database behind Isola's feedback
  service), one a day, and never the same report twice. A bug report's
  description shows how to lay out the steps to reproduce it.
- Settings -> About & Logs says who made Isola; the name opens the project's
  GitHub page. Support Isola opens its GitHub Sponsors page, and the repository
  has a Sponsor button.
- A notification with a sign-in or verification code (an SMS through Phone
  Link, an email, WhatsApp) gets a Copy button and stays 20 seconds, like
  Windows' own banners. The copied code stays out of clipboard history.
- If feedback is paused for a while, Settings -> Feedback says so (the form
  greyed out behind a notice, with Check again) instead of letting you write
  something that can't be sent.
- Quit Isola at the bottom of Settings stops it for now. It starts again the
  next time you open it, or with Windows if it's set to.

### Changed

- Scrolling lists (Sound, Home, Notes, Shelf, Clipboard and every Settings
  page) fade out at an edge with more to scroll toward, instead of cutting a
  row or tile in half.
- Icons that change (play and pause, mute) blur from one to the other instead
  of swapping in a single frame.
- Small round buttons can be clicked a few pixels outside the circle, and
  switches a little above and below the track. The Home Assistant entity rows
  in Settings space their move and remove buttons further apart.
- Navigation steps in text read "Settings -> Notifications" instead of using
  an arrow character.
- The repository's New issue page now points to Settings -> Feedback (and to
  private vulnerability reporting for security problems).
- New sounds, in higher quality: five notification chimes, six ringtones, six
  timer alarms, and their own sets for devices connecting, the charger and low
  battery. Each fades out instead of stopping short, they're matched in
  loudness (a held ringtone no longer sounds far louder than a click), and
  they play in stereo with a little room around them. Earlier choices move to
  the new defaults.
- The release pipeline runs each GitHub action at a fixed commit rather than a
  version tag that could be moved, and Dependabot proposes updates and
  security fixes for the actions and the NuGet packages. Secret scanning and
  push protection are on for the repository.

### Fixed

- A message relayed from the phone that happened to contain "calling" (a
  shopping app's "Your cart is calling") rang as an incoming call. Only Phone
  Link's own call notification counts as a call now.
- A ringtone could keep playing forever once its call left the screen
  another way than timing out (opening the island over it, a fullscreen app
  hiding it). It now stops whenever its call does, and any looping sound
  stops by itself after two minutes, whatever happens.

### VirusTotal

1 of 70 security vendors flagged `IsolaSetup.exe` on 2026-11-17. [Full report](https://www.virustotal.com/gui/file/b3142922d6854f73764fb174a82a0be4b6cb39b3f8e12ffe252b49e07e87c425)

- Bkav: W32.Malware.2E399881

The setup isn't code-signed yet, and a new unsigned program often trips a few engines' heuristics. The source and the build are public, and the SHA-256 in the release notes is the file this report covers.

## [1.3.0] - 2026-11-16

### Added

- The GitHub setup and the Microsoft Store version each offer to replace the
  other. The setup asks first when Isola Notch is installed; Isola Notch asks
  the first time it opens next to a GitHub install. Settings, notes and shelf
  carry over unless you choose to start fresh. Only one copy can run at a
  time, so having both meant the second one never started.

### VirusTotal

2 of 71 security vendors flagged `IsolaSetup.exe` on 2026-11-16. [Full report](https://www.virustotal.com/gui/file/ae71382c5e95896c7b050076a5bc5178c91234a329c14457ba897b07ad97a6f6)

- Bkav: W32.Malware.1693FFCB
- Rising: Trojan.Kryptik@AI.82 (RDML:5gcqf2nd3uhiqR2yRNE2UQ)

The setup isn't code-signed yet, and a new unsigned program often trips a few engines' heuristics. The source and the build are public, and the SHA-256 in the release notes is the file this report covers.

## [1.2.1] - 2026-11-16

### Changed

- The Microsoft Store version doesn't change Windows' notification settings and
  keeps its data in its own package folder: Microsoft didn't allow the
  capability both need. Its Settings link to Windows' notification settings
  instead, so you can turn off banners for the apps the island shows. The
  GitHub version is unchanged.

### VirusTotal

1 of 71 security vendors flagged `IsolaSetup.exe` on 2026-11-16. [Full report](https://www.virustotal.com/gui/file/9007fbb642dcccec4d2f852087f1b434b51a09c71e15c9b6e1f9299c373c9d3e)

- Rising: Trojan.Kryptik@AI.82 (RDML:9wZR37Vi+fkAkkVRblteLQ)

The setup isn't code-signed yet, and a new unsigned program often trips a few engines' heuristics. The source and the build are public, and the SHA-256 in the release notes is the file this report covers.

## [1.2.0] - 2026-11-15

### Added

- A Microsoft Store version, built from every release tag with the same
  version (store/README.md). As a Store app, Isola is updated by the Store
  instead of updating itself, starts with Windows through its package's
  startup task, and keeps its settings in the same folder as the GitHub
  version.

### VirusTotal

1 of 71 security vendors flagged `IsolaSetup.exe` on 2026-11-15. [Full report](https://www.virustotal.com/gui/file/e2ddf940e42f6390ee91c9e997f0fc2e8d29e9cd183b1fa83ec91320cd38b9f1)

- Rising: Trojan.Kryptik@AI.82 (RDML:etbeo/0+1VFCCcFGZOYM2g)

The setup isn't code-signed yet, and a new unsigned program often trips a few engines' heuristics. The source and the build are public, and the SHA-256 in the release notes is the file this report covers.

## [1.1.0] - 2026-11-15

### Added

- The setup's Options page links to the new privacy policy (PRIVACY.md).

### Changed

- The "Hide Windows' own popups and chime" switch says plainly that it changes
  each app's Windows notification setting and puts it back.
- Isola.exe and IsolaSetup.exe show Isola as product and company in their file
  properties.
- The volume popup shows the muted speaker and 0 when muted, instead of a
  "Muted" label that didn't fit.
- Settings uses a stronger tint when maximized, so a busy background doesn't
  wash color across the whole window.

### Fixed

- Hovering video previews on a web page (a TikTok profile, for example) made
  the island switch between the page and the song playing two or three times a
  second, re-coloring its glow each time. A newly playing session now has to
  keep playing for a moment (longer if it's only "a site is playing media")
  before it takes over. Sessions you pick, and falling back when one closes,
  are still instant.
- Each of those short-lived sessions left three event subscriptions behind.
- Sound previews in Settings: a ringtone picked within three seconds of
  another was cut short by the first one's stop. Short sounds now start with a
  moment of silence, longer on Bluetooth headphones, so an output waking from
  idle no longer clips them or swallows them until a second click.
- The "Spotify isn't playing on any device" line sat right under the play
  button; it now has room of its own.

### VirusTotal

2 of 71 security vendors flagged `IsolaSetup.exe` on 2026-11-15. [Full report](https://www.virustotal.com/gui/file/8d0c9ead8c4765ef6de81e74c6b428f680a74efc4b4b7c7ff2ca3b4f635729de)

- Bkav: W32.Malware.786E714D
- Rising: Trojan.Kryptik@AI.82 (RDML:h7SfisYideFMnCoejses7Q)

The setup isn't code-signed yet, and a new unsigned program often trips a few engines' heuristics. The source and the build are public, and the SHA-256 in the release notes is the file this report covers.

## [1.0.1] - 2026-11-15

### Fixed

- A Home Assistant token saved before 1.0.0, or typed into the config file by
  hand, stayed in plain text on disk until a setting next changed. Isola now
  encrypts it the first time it starts.
- The install carried the project's logo images, which the app never uses.
  Only its icon is installed now.

### VirusTotal

2 of 71 security vendors flagged `IsolaSetup.exe` on 2026-11-15. [Full report](https://www.virustotal.com/gui/file/48f1b08c526a3c90f8bbad5ea21dbea1c030303315d994e1cffa0cbdf1a1fa46)

- Bkav: W32.Malware.FBFF05A7
- Rising: Trojan.Kryptik@AI.82 (RDML:1Jdy4OrnpWnt0ZrElcCnyA)

The setup isn't code-signed yet, and a new unsigned program often trips a few engines' heuristics. The source and the build are public, and the SHA-256 in the release notes is the file this report covers.

## [1.0.0] - 2026-11-15

The first public release.

### Added

- A dynamic island at the top of the screen, as a floating pill or a notch,
  that peeks on hover and opens into a dashboard on click.
- Popups for notifications (with the app's icon), Phone Link calls with answer
  and decline, track changes, Bluetooth devices and AirPods battery, screens,
  the charger and battery, volume, Caps Lock and the timer. Bursts queue and
  fold into a summary, and calls always go first.
- Isola's own notification chime, ringtone, timer bell and device sounds, each
  with several choices and its own switch.
- Dashboard tabs: Now Playing, Sound (output device and per-app volume), Home
  Assistant tiles, Weather, Notes, Shelf, Clipboard history, Timer and Devices.
- A launcher on `Alt+Space` with math, units, colors, world time, more than 60
  commands, quick notes and timers, web searches, and app and file search
  (through Everything when it's running, otherwise Isola's own index).
- Spotify's in-app volume under Now Playing, through a Spotify app of your own.
- A setup wizard that installs per user without administrator rights, and can
  update, repair or uninstall an existing install.
- Automatic updates from this repository's releases, checked against the
  release notes' SHA-256 and GitHub's own digest before they run.
- A help button next to settings that need explaining, such as the Home
  Assistant token and the Spotify Client ID.

### Security

- The Home Assistant token and the Spotify sign-in are stored encrypted with
  Windows' data protection for your account.
- The log records which app a notification or call came from, never who sent
  it or what it says, and the detailed log is off by default.
- Search replies are accepted only from Everything's own window and are read
  with every length checked against the message.
- Settings warns when Home Assistant is reached over plain `http://` outside
  your local network.

### VirusTotal

2 of 71 security vendors flagged `IsolaSetup.exe` on 2026-11-15. [Full report](https://www.virustotal.com/gui/file/eadf6631841398d7deba1fe98403dee29580c8ec2a562419526aa5684940e92a)

- Bkav: W32.Malware.DC1A85E5
- Rising: Trojan.Kryptik@AI.82 (RDML:ZYZ9i+F12CoNAy15plJ/Gg)

The setup isn't code-signed yet, and a new unsigned program often trips a few engines' heuristics. The source and the build are public, and the SHA-256 in the release notes is the file this report covers.
