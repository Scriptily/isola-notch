# Changelog

Isola follows [semantic versioning](https://semver.org/): the major number
changes for breaking or very large changes, the minor number for new features,
and the patch number for fixes only. Each release's section below is also its
GitHub release notes.

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
- The log no longer records who a notification or call is from or what it
  says, and the detailed log is off by default.
- Search replies are accepted only from Everything's own window and are read
  with every length checked against the message.
- Settings warns when Home Assistant is reached over plain `http://` outside
  your local network.
