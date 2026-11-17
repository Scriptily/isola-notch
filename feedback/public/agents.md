# Guide for AI agents

You're picking up work on Scriptily's Windows apps. Everything below is shared
by every app, so read this once and you can work on any of them. The newest
copy is always the one on the feedback hub, at `/agents` (or raw at
`/agents.md`), and in the repository at `isola-notch/feedback/public/agents.md`.

## Start here

1. Read the app's own `CLAUDE.md` (in the app's folder, never committed). It
   has the rules; they're the same for every app because they're generated from
   Isola's.
2. Read `~/.claude/CLAUDE.md` and `~/.claude/publishing-setup.md`: the accounts,
   keys and pipelines that already exist. Never set any of it up again.
3. Check `git status`, the current branch, and `## [Unreleased]` in
   `CHANGELOG.md`: that's what's in progress.
4. Look at the open feedback for the app on the hub (below).

## The apps and where they live

| | |
|---|---|
| `C:\Users\Scriptily\Documents\dirs\isola-notch` | **Isola**, "Dynamic Island for Windows": the reference app. Shared pieces are built and tested here first. Public at github.com/Scriptily/isola-notch; on the Microsoft Store as Isola Notch. |
| `C:\Users\Scriptily\Documents\dirs\app-template` | The template every new app starts from (`new-app.ps1`). `sync.py` regenerates it from Isola. |
| `isola-notch\feedback` | The feedback hub: one Cloudflare Worker, D1 database and dashboard for every app. |

New apps go next to these, created with `app-template\new-app.ps1`.

## Stack

- **Apps:** C# on .NET 10, published NativeAOT. UI drawn with Direct2D and
  DirectComposition (Vortice), Win32 interop throughout, no XAML. Spring
  animations (`src/Render/Spring.cs`).
- **Tests:** `tests/` compiles the real source against fakes and simulated
  hardware: `dotnet run -c Release` in `tests/`. A regression test must fail
  with the bug put back.
- **Installer:** a NativeAOT setup with the app embedded (`installer/`),
  per-user, no admin. **Store:** an MSIX from `store/`.
- **Scripts:** PowerShell for building (`build.ps1`), Python for media and
  tooling.
- **Feedback hub:** a Cloudflare Worker in plain JavaScript (no build step), D1
  (SQLite), a dashboard of plain HTML, CSS and JS. Tests:
  `node --test feedback/test/service.test.mjs`.

## How work ships

- Work on a branch. Every change is tested. Update the README (the landing
  page), CHANGELOG `## [Unreleased]`, and PRIVACY.md when anything talks to a
  new service.
- **Greenlight:** show the user what changed (with screenshots) and wait for an
  explicit go before merging, pushing or tagging.
- A release is a tag, `vX.Y.Z`, pushed after `VERSION` and the changelog are
  updated. GitHub Actions tests, builds, attests, scans on VirusTotal,
  publishes, and submits the Store package. Never release from the PC.
- After changing a shared piece in Isola (workflows, `store/`, `build.ps1`,
  `Packaging.cs`, `Secret.cs`, `Feedback.cs`, CLAUDE.md rules), run
  `python ../app-template/sync.py` and commit the template too.

## Language and design

- American English everywhere. Navigation steps are written with `->`:
  Settings -> Feedback.
- Public text goes through the humanizer skill; replies to the user don't.
- No emojis in any UI: consistent hollow and filled icons.
- Dark, near-black surfaces that get lighter as they rise; translucent
  borders; colors from one OKLCH lightness; the teal-blue-violet rim; nested
  corners concentric (inner radius = outer minus padding); scrolled edges fade;
  icon swaps blur; motion under 300 ms; click targets at least 36 px.
- The full rules are in every app's CLAUDE.md under "Visual details" and
  "Motion and interaction".

## The feedback hub

- **Reports** have a number per app, shown as `#482913`: random, never reused
  in that app, at least 6 digits, growing as an app fills up. Each is open or
  closed, with tags, private notes and a history.
- **People** have one 20-digit ID across every app, shown as
  `@48151623424815162342`, derived from the machine. No ID is ever shared
  between machines.
- **Pages:** `/` is every app at a glance; `/<app>/feedback` an app's reports;
  `/<app>/feedback/<number>` one report; `/agents` this guide. An app's own
  hostname opens that app directly (`/feedback/<number>`).
- **API** (the dashboard uses it; so can you): `GET /api/summary`,
  `GET /api/reports?app=&status=&kind=&q=&tag=&user=&sort=`,
  `GET|PATCH /api/reports/<app>/<number>` (`{status, addTags, removeTags}`),
  `POST /api/reports/<app>/<number>/notes` (`{body}`), `GET /api/export`.
  Everything sits behind Cloudflare Access: send the service token's
  `CF-Access-Client-Id` and `CF-Access-Client-Secret` headers if the user has
  given you one. The hub's address is in the user's local notes, never in a
  repository.

## Never

- Put anything personal in a repository, app, commit or release note: names,
  emails, tokens, domains, IP addresses, device names, locations.
- Add Claude attribution (no Co-Authored-By, no session links).
- Inject keyboard or mouse input on the user's PC unless they say they're
  away. Verify with tests, logs, window messages and screenshots instead.
