# Security

## Reporting a problem

Please report security problems privately, through GitHub's
[private vulnerability reporting](https://github.com/Scriptily/isola-notch/security/advisories/new)
for this repository, not in a public issue. Include what you found, how to
reproduce it, and which version you tested (Settings › About & Logs).

Only the latest release gets fixes.

## What Isola does that matters for security

- It runs as your user, never as administrator, and installs into your own
  profile.
- Updates come only from this repository's releases. A download runs only if
  its SHA-256 matches both the release notes and GitHub's digest for the file.
- Tokens (Home Assistant, Spotify) are encrypted with Windows' data protection
  for your account.
- It listens on the network only while you connect Spotify: a one-time
  listener on `127.0.0.1:4381` for the sign-in reply, checked against a random
  state value, closed as soon as the reply arrives or after three minutes.
- It changes Windows' per-app notification banner and sound settings while it
  shows notifications itself, and puts them back. See "What it changes in
  Windows" in the [README](README.md#what-it-changes-in-windows).
