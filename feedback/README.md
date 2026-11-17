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
- **Numbers never reused.** Reports use `AUTOINCREMENT`, so a number is never
  handed out twice, even after deleting the newest report. Numbers are shared
  across apps, so `#12` always means one report.
- **An ID belongs to one PC.** An app derives its user ID from the machine and
  sends a second, independent hash (the verifier). A PC seen before keeps its
  ID. If a different PC happens to derive an ID that's already taken, it gets
  `-2`, `-3` and so on. The app shows whatever ID the Worker answers with. IDs
  are per app.
- **Little kept about people.** IP addresses are stored only as salted hashes,
  to count reports per address. Logs arrive already scrubbed by the app.

## API

| | |
|---|---|
| `POST /feedback` | public: a report (`app`, `user`, `verifier`, `kind`, `subject`, `text`, `version`, `windows`, `source`, `logs?`), answering `{report, user}` |
| `POST /feedback/mine` | public: `{app, verifier}`, answering that PC's reports with their status (titles only) |
| `GET /api/reports` | `app`, `status` (open, closed, all), `kind`, `q` (`#12` is a report number), `tag`, `user`, `logs=1`, `sort` (new, old, updated, user), `limit`, `offset` |
| `GET /api/reports/:id` | one report with tags, notes and history |
| `PATCH /api/reports/:id` | `{status?, addTags?, removeTags?}` |
| `POST /api/reports/:id/notes` | `{body}`: a private note |
| `POST /api/bulk` | `{ids, status}` |
| `GET /api/stats` | counts, tags and apps (`?app=` for one app) |
| `GET /api/export?format=csv` | everything, as CSV or JSON |

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
3. `npx wrangler deploy`, then attach the dashboard's hostname in Cloudflare:
   Workers & Pages -> feedback-hub -> Settings -> Domains & Routes -> Add ->
   Custom domain. It's done there rather than in `wrangler.toml` so this
   public repository never names the domain.
4. Zero Trust -> Access -> Applications -> Add a self-hosted application for
   that hostname, with a policy allowing only your email. Copy its
   Application Audience (AUD) tag. Choose a team name with nothing personal in
   it: it shows on the sign-in page.
5. Set the secrets:
   ```
   npx wrangler secret put DASHBOARD_HOST       # the hostname from step 3
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
