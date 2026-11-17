-- The feedback hub: one database for every app. Applied with:
--   npx wrangler d1 migrations apply feedback-hub --remote

-- One row per PC that has sent feedback. The app derives its ID from the
-- machine; the verifier is a second, independent hash of the same machine, so
-- a different PC that happened to derive the same ID is told apart and given
-- its own. An ID, once given, belongs to that PC forever.
CREATE TABLE users (
  app        TEXT NOT NULL,
  id         TEXT NOT NULL,
  verifier   TEXT NOT NULL,
  first_seen INTEGER NOT NULL,
  PRIMARY KEY (app, id),
  UNIQUE (app, verifier)
);

-- AUTOINCREMENT (not just INTEGER PRIMARY KEY): SQLite then never hands out a
-- number again, even after the highest report is deleted.
CREATE TABLE reports (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  app         TEXT NOT NULL,
  user_id     TEXT NOT NULL,
  kind        TEXT NOT NULL CHECK (kind IN ('bug', 'feature')),
  subject     TEXT NOT NULL,
  body        TEXT NOT NULL,
  version     TEXT,
  windows     TEXT,
  source      TEXT,
  logs        TEXT,
  status      TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'closed')),
  fingerprint TEXT NOT NULL,
  ip_hash     TEXT,
  created_at  INTEGER NOT NULL,
  updated_at  INTEGER NOT NULL,
  closed_at   INTEGER,
  FOREIGN KEY (app, user_id) REFERENCES users(app, id),
  UNIQUE (app, fingerprint)
);
CREATE INDEX reports_app     ON reports(app, status, created_at);
CREATE INDEX reports_user    ON reports(app, user_id, created_at);
CREATE INDEX reports_status  ON reports(status, created_at);
CREATE INDEX reports_ip      ON reports(ip_hash, created_at);
CREATE INDEX reports_created ON reports(created_at);

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
