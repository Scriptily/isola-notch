# Changelog

Isola follows [semantic versioning](https://semver.org/): the major number
changes for breaking or very large changes, the minor number for new features,
and the patch number for fixes only. Each release's section below is also its
GitHub release notes.

## [Unreleased]

### Added

- Settings -> Feedback: send a feature request or a bug report with a subject
  and a description, and optionally the log, with your account and PC names,
  folders, addresses, emails and tokens removed first. Each report carries a
  feedback ID, a one-way 20-digit number made from your PC, so reports from
  the same person can be told apart, and gets a report number of its own. It goes to
  the developer privately (a private database behind Isola's feedback
  service), one a day, and never the same report twice.

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
