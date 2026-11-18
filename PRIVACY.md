# Privacy policy

Isola has no account, no telemetry, no analytics and no ads. Nobody, including
the people who make it, receives anything about you or how you use it.

## What stays on your computer

Isola reads these on your computer to show them in the island, and never sends them
anywhere:

- Notifications and Phone Link calls (the app, the sender and the message).
  A one-time code you copy from one is kept out of Windows' clipboard history,
  its cloud clipboard and Isola's own Clipboard tab
- What's playing in your media apps, and the cover art
- Your clipboard history (Win+V), if the Clipboard tab is on
- Bluetooth devices and battery levels, including AirPods
- The files you drop on the shelf, your notes and your timer
- Search results from your apps and folders, or from Everything if it's running
- MagicPods' log file, if MagicPods is installed, to read AirPods battery levels

Your settings, notes, shelf and log are stored in `%LOCALAPPDATA%\Isola` for
your Windows account only. The Home Assistant token and the Spotify sign-in are
encrypted with Windows' data protection for your account, so a copy of the file
can't be used anywhere else. The log records which app a notification came
from, never who sent it or what it said, and the detailed log is off unless you
turn it on in Settings -> About & Logs.

## What it sends, and where

Isola connects only to these, and only for the reason given:

| Service | When | What it sends | Turn it off |
|---|---|---|---|
| Your own Home Assistant | if you enter its address and token | your token, to the address you entered, to read and control the devices you picked | clear the address in Settings -> Home Assistant |
| [Open-Meteo](https://open-meteo.com/en/terms#privacy) | for weather, when Home Assistant isn't the source | the city or coordinates you entered | Settings -> Widgets -> Weather |
| [Spotify](https://www.spotify.com/legal/privacy-policy/) | if you connect it | your sign-in, to read and change Spotify's playback and volume, and to show your Spotify display name in Settings | disconnect in Settings -> Spotify |
| [GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement) | every six hours, and when you press Check now | a request for this project's latest release; nothing about you | Settings -> About & Logs -> Update automatically |
| Isola's feedback service, on [Cloudflare](https://www.cloudflare.com/privacypolicy/) | only when you press Send feedback | your subject and description, Feature request or Bug, your feedback ID (a one-way 16-digit number made from this computer, shown as @User and the digits, the same in every app by this developer; Windows' machine ID itself never leaves it, and a second one-way number lets the service tell computers apart), the Isola and Windows versions, your time zone's offset from UTC, whether it's the Store or GitHub version, and the log only if you tick Attach logs (with your account and computer names, folders, addresses, emails and tokens removed first). It's kept in a private database that only the developer, and anyone they give access to Isola's reports, can open (each with a password and a code from their phone); your IP address is kept only as a salted hash, to limit how often reports can be sent | don't send feedback |
| Isola's feedback service | when you open Settings -> Feedback (at most once a minute) | nothing about you: it only asks whether feedback is open, so the page can say when it's paused | don't open the Feedback page |
| [GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement) | with downloads on: the first time you download, then once a day | requests for the latest releases of yt-dlp, FFmpeg (Gyan's builds) and QuickJS, and their files; nothing about you | Settings -> Downloads |
| The site a link points to | only when you download from a link | the link, to that site (yt-dlp downloads it as your browser would, without your sign-ins or cookies) | don't download |

Each of these services sees your IP address when Isola connects, as with any
connection, and handles it under its own privacy policy linked above. Home
Assistant is your own server.

The setup program can also, only if you ask it to:

- install [Everything](https://www.voidtools.com/) through the Windows Package
  Manager, which downloads it from voidtools
- open [MagicPods](https://apps.microsoft.com/detail/9P6SKKFKSHKM) in the
  Microsoft Store
- download the Segoe Fluent Icons font from Microsoft, on Windows 10 if it's
  missing

## Changes Isola makes to Windows

While Isola shows notifications itself, it turns off Windows' own banner and
chime for each app, so each notification appears once. It saves the original
settings first and puts them back when it stops showing notifications, when it
quits, when you sign out, if it crashes, and when you uninstall it. You can turn
this off in Settings -> Notifications & Sounds -> Hide Windows' own popups and chime.

With "Start with Windows" on, it adds a sign-in task and a startup entry for
your account. The uninstaller removes them, and can remove your data too.

The Microsoft Store version (Isola Notch) changes none of these: it can't
change Windows' notification settings, so Settings -> Notifications & Sounds links to
Windows' own page where you can turn banners off yourself. It starts with
Windows through its package's startup task, which you can also turn off in
Windows' Startup apps, and keeps its data in its own package folder, which
Windows removes when you uninstall it.

## Questions

Open an issue at
[github.com/Scriptily/isola-notch/issues](https://github.com/Scriptily/isola-notch/issues),
or report a security problem privately as described in [SECURITY.md](SECURITY.md).

Changes to this policy are listed in the [changelog](CHANGELOG.md).
