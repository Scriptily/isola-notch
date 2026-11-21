# Code signing policy

The setup on GitHub (`IsolaSetup.exe`) isn't code-signed, and signing isn't
planned for now. Windows may warn that it's from an unknown publisher; choose
**More info**, then **Run anyway**.

The Microsoft Store version (Isola Notch) is signed by Microsoft when it
passes certification, so it installs without that warning.

## Checking a download yourself

Without a signature, this is how to tell a setup is the real one:

- Every release is built by [GitHub Actions](.github/workflows/release.yml)
  from the public source at its version tag, never on a personal computer. The
  build log is public.
- Each setup carries a signed build attestation, which anyone can check with
  `gh attestation verify IsolaSetup.exe --repo Scriptily/isola-notch`.
- Each release's notes give the setup's SHA-256 and a VirusTotal scan of it,
  with anything it found.
- Isola updates itself only with a setup from this repository's own releases,
  and only if its SHA-256 matches both the release notes and GitHub's record of
  the file.

## Programs Isola fetches

Isola's own files are the only ones built here. For downloading
and converting, Isola fetches yt-dlp, FFmpeg (Gyan's essentials build) and
QuickJS from those projects' own GitHub releases, signed (or not) by them.
Isola installs one only if it matches the SHA-256 checksum GitHub publishes
for that release file, and never runs anything else.

## Privacy

Isola sends no telemetry. What it connects to, and why, is in the
[privacy policy](PRIVACY.md).
