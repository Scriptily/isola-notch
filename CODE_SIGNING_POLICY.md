# Code signing policy

Isola's Windows releases are not code-signed yet. The project has applied to
the [SignPath Foundation](https://signpath.org/) for free code signing for
open-source projects; once approved, this page will say so and every release
from then on will be signed.

## How releases are made

- Every release is built by [GitHub Actions](.github/workflows/release.yml)
  from the public source at its version tag, never on a personal computer. The
  build log is public and each setup carries a signed build attestation, which
  anyone can check with `gh attestation verify IsolaSetup.exe --repo Scriptily/isola-notch`.
- Each release's notes give the setup's SHA-256 and a VirusTotal scan of it.
- Only artifacts built from this repository's own source are signed. Nothing
  from other projects is signed under Isola's name.
- Every signing request is approved by hand by an approver below.

## Team roles

| Role | Members |
|---|---|
| Committers and reviewers | [Scriptily](https://github.com/Scriptily) |
| Approvers | [Scriptily](https://github.com/Scriptily) |

All members use multi-factor authentication for GitHub and for SignPath.

## Privacy

Isola sends no telemetry. What it connects to, and why, is in the
[privacy policy](PRIVACY.md).
