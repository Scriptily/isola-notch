# Feedback relay

Settings -> Feedback sends here. The relay is a Cloudflare Worker that files
each report as an issue in a **private** GitHub repository.

Why a relay: Isola is open source, so any credential inside it is public.
With a GitHub token in the app, anyone could read every report or flood the
repository. The token lives only in the Worker, as a secret, and the Worker
enforces the limits no matter what a modified copy of the app sends:

| Limit | How |
|---|---|
| One report per person per day | KV key per user number, expiring after 24 h |
| The same report never twice | SHA-256 of the normalized subject and text (the app computes the same) |
| Made-up user numbers | 3 reports per IP address per day, 100 per day in total |
| Size | subject 120, description 4,000, logs 60,000 characters |

Logs arrive already scrubbed by the app (`Feedback.Redact`) and go in a
comment on the issue.

## Set up

1. Create the private repository (here `Scriptily/isola-feedback`), with the
   labels `bug`, `enhancement` and `feedback`.
2. Create a fine-grained GitHub token: Settings -> Developer settings ->
   Fine-grained tokens. Repository access: only that repository. Permissions:
   Issues, read and write. Nothing else.
3. Sign in to Cloudflare (a free account is enough) and create the store:
   ```
   npx wrangler login
   npx wrangler kv namespace create FEEDBACK
   ```
   Put the printed ID in `wrangler.toml`.
4. Add the token as a secret and deploy:
   ```
   npx wrangler secret put GITHUB_TOKEN
   npx wrangler deploy
   ```
5. Put the printed address plus `/feedback` in `Feedback.Endpoint`
   (`src/Services/Feedback.cs`).

## Test

```
node --test tools/feedback-relay/worker.test.mjs
```
