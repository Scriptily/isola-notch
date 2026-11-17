-- The feedback hub: one database for every app. Applied with:
--   npx wrangler d1 migrations apply feedback-hub --remote

-- One row per machine, for every app: the same machine has the same ID in all
-- of them. The ID (20 digits) is a hash of the machine's ID; the verifier is a
-- second, independent hash of it. Machines are known by their verifier, so if
-- two ever hashed to the same ID, the second is given another free 20-digit
-- number, and an ID once given belongs to that machine forever.
CREATE TABLE users (
  id         TEXT PRIMARY KEY CHECK (length(id) = 20),
  verifier   TEXT NOT NULL UNIQUE,
  first_seen INTEGER NOT NULL
);

-- Every report number each app has ever handed out, kept even if a report were
-- deleted, so no number is ever given out twice within an app.
CREATE TABLE issued (
  app    TEXT NOT NULL,
  number INTEGER NOT NULL,
  PRIMARY KEY (app, number)
);

CREATE TABLE reports (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,   -- internal: links tags, notes and history
  app         TEXT NOT NULL,
  number      INTEGER NOT NULL,                    -- what people see: #482913, random, per app
  user_id     TEXT NOT NULL REFERENCES users(id),
  kind        TEXT NOT NULL CHECK (kind IN ('bug', 'feature')),
  subject     TEXT NOT NULL,
  body        TEXT NOT NULL,
  version     TEXT,
  windows     TEXT,
  source      TEXT,
  tz          TEXT,                                -- the sender's UTC offset when sent, e.g. +03:00
  logs        TEXT,
  status      TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'closed')),
  fingerprint TEXT NOT NULL,
  ip_hash     TEXT,
  created_at  INTEGER NOT NULL,                    -- UTC milliseconds; shown in the viewer's time zone
  updated_at  INTEGER NOT NULL,
  closed_at   INTEGER,
  UNIQUE (app, number),
  UNIQUE (app, fingerprint)
);
CREATE INDEX reports_app    ON reports(app, status, created_at);
CREATE INDEX reports_user   ON reports(user_id, created_at);
CREATE INDEX reports_ip     ON reports(app, ip_hash, created_at);

CREATE TABLE tags (
  report_id INTEGER NOT NULL REFERENCES reports(id) ON DELETE CASCADE,
  tag       TEXT NOT NULL,
  PRIMARY KEY (report_id, tag)
);

-- private notes on a report, for the developer only
CREATE TABLE notes (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  report_id  INTEGER NOT NULL REFERENCES reports(id) ON DELETE CASCADE,
  author     TEXT NOT NULL,
  body       TEXT NOT NULL,
  created_at INTEGER NOT NULL
);

-- what happened to a report, newest last: opened, closed, reopened, tagged
CREATE TABLE events (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  report_id INTEGER NOT NULL REFERENCES reports(id) ON DELETE CASCADE,
  actor     TEXT NOT NULL,
  what      TEXT NOT NULL,
  at        INTEGER NOT NULL
);
CREATE INDEX events_report ON events(report_id, at);
