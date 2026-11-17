# Feedback hub

One Cloudflare Worker for every app's feedback. An app's Feedback page sends
reports here, the Worker keeps them in a D1 database, and a private dashboard
is where you read, sort, tag and close them, for all apps at once or one at a
time. Isola is the first app; a new app is one more name in `APPS`.

```
apps ──POST /feedback──> Worker (workers.dev) ──> D1
you ──Cloudflare Access──> Worker (dashboard host) ──> dashboard + /api/*
```

## Why it's built this way

- **No credential in any app.** The apps are open source, so anything they held
  would be public. An app only knows the public intake address. Reading
  feedback needs a Cloudflare Access sign-in, which the Worker checks on every
  request (`src/access.js`), not only at the edge.
- **Limits enforced here.** An app checks them first so it can explain, but a
  modified copy could skip that. Per app, the Worker allows one report per PC
  per day, never the same subject and description twice (the app computes the
  same fingerprint), 3 reports per IP address per day, and 100 per day overall.
- **Report numbers** (`#482913`) are random, per app, and never handed out
  twice in an app: every number an app has issued is kept in `issued`, even if
  its report were deleted. They start at 6 digits and grow so an app always
  has 100 times more free numbers than used, so a random pick almost never
  needs a second try, and a number says nothing about how many reports exist.
- **People** (`@72073953124684870734`) have one ID across every app: always 20
  digits, a hash of the machine with a key all apps share. The app also sends
  a second, independent hash (the verifier), and the hub knows machines by it.
  If two machines ever derived the same ID (about 1% likely only after 1.3
  billion machines), the second is given another free 20-digit ID. An ID once
  given is never given to another machine, and 9 x 10^19 IDs won't run out.
- **Times** are stored in UTC and shown in the reader's time zone. Each report
  also carries the sender's UTC offset, so the dashboard shows their local
  time too.
- **Little kept about people.** IP addresses are stored only as salted hashes,
  to count reports per address. Logs arrive already scrubbed by the app.

## API

| | |
|---|---|
| `POST /feedback` | public: a report (`app`, `user`, `verifier`, `kind`, `subject`, `text`, `version`, `windows`, `source`, `tz`, `logs?`), answering `{report, user}` |
| `POST /feedback/mine` | public: `{app, verifier}`, answering that machine's reports in the app with their status (titles only) |
| `GET /api/summary` | every app: counts and the newest reports (the home page) |
| `GET /api/reports` | `app`, `status` (open, closed, all), `kind`, `q` (`#482913` is a report, `@` and 20 digits a person), `tag`, `user`, `logs=1`, `sort` (new, old, updated, user), `limit`, `offset` |
| `GET /api/reports/:app/:number` | one report with tags, notes and history |
| `PATCH /api/reports/:app/:number` | `{status?, addTags?, removeTags?}` |
| `POST /api/reports/:app/:number/notes` | `{body}`: a private note; `#number` and `@person` become links |
| `POST /api/bulk` | `{items: [{app, number}], status}` |
| `GET /api/stats` | counts, tags and apps (`?app=` for one app) |
| `GET /api/export?format=csv` | everything (or `&app=`), as CSV or JSON |

## Pages

| | |
|---|---|
| `/` | every app at a glance, with links into each |
| `/<app>/feedback` | an app's reports, with filters in the query string |
| `/<app>/feedback/<number>` | one report |
| `/agents` | the guide for AI agents (`/agents.md` raw) |

An app's own hostname opens that app: there, `/feedback/<number>` is the same
page as `/<app>/feedback/<number>` on the home hostname.

## Set up (once, for all apps)

1. Sign in and create the database:
   ```
   cd feedback
   npx wrangler login
   npx wrangler d1 create feedback-hub          # put the ID in wrangler.toml
   npx wrangler d1 migrations apply feedback-hub --remote
   ```
2. Check that the account's workers.dev subdomain (Workers & Pages) has
   nothing personal in it, because every app's intake address includes it.
3. `npx wrangler deploy`, then attach the dashboard's hostnames in Cloudflare:
   Workers & Pages -> feedback-hub -> Settings -> Domains & Routes -> Add ->
   Custom domain, once for the home hostname and once for each app's. It's
   done there rather than in `wrangler.toml` so this public repository never
   names the domain. (A local DNS rewrite can't do this: DNS maps names to
   addresses, not to paths. If a local resolver rewrites the whole domain,
   exempt these hostnames so they resolve to Cloudflare.)
4. Zero Trust -> Access -> Applications -> Add a self-hosted application
   covering those hostnames, with a policy allowing only your email. Copy its
   Application Audience (AUD) tag. Choose a team name with nothing personal in
   it: it shows on the sign-in page.
5. Set the secrets:
   ```
   npx wrangler secret put HOSTS                # {"home.example.com": "", "app.example.com": "app"}
   npx wrangler secret put ACCESS_TEAM_DOMAIN   # yourteam.cloudflareaccess.com
   npx wrangler secret put ACCESS_AUD           # the tag from step 4
   npx wrangler secret put IP_SALT              # any long random string
   ```
6. Put `https://feedback-hub.<subdomain>.workers.dev/feedback` in the app
   (Isola: `Feedback.Endpoint` in `src/Services/Feedback.cs`).

For scripts (or Claude) to use the API, create an Access service token, allow
it in the application's policy, and send its `CF-Access-Client-Id` and
`CF-Access-Client-Secret` headers.

## Adding an app

Add its id to `APPS` in `wrangler.toml` and deploy. In the app, send `app` with
that id, derive the user ID and verifier with the app's own HMAC keys (so IDs
from different apps never line up), and use the same fingerprint as
`src/util.js`.

## Develop and test

```
node --test feedback/test/service.test.mjs       # the Worker against real SQLite
node feedback/dev/serve.mjs                      # the dashboard with sample reports, at localhost:8787
```
