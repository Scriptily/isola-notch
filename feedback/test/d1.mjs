// A stand-in for Cloudflare D1 on Node's built-in SQLite: the same SQL engine
// D1 runs, the same migration, the parts of D1's API the Worker uses.
import { DatabaseSync } from 'node:sqlite';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

class Stmt {
  constructor(db, sql, args = []) { this.db = db; this.sql = sql; this.args = args; }
  bind(...args) { return new Stmt(this.db, this.sql, args); }
  #norm() { return this.args.map(a => (typeof a === 'boolean' ? +a : a)); }
  async first() { return this.db.prepare(this.sql).get(...this.#norm()) ?? null; }
  async all() { return { results: this.db.prepare(this.sql).all(...this.#norm()) }; }
  async run() { return this.runSync(); }
  runSync() {
    const r = this.db.prepare(this.sql).run(...this.#norm());
    return { meta: { last_row_id: Number(r.lastInsertRowid), changes: r.changes } };
  }
}

export function d1() {
  const db = new DatabaseSync(':memory:');
  db.exec('PRAGMA foreign_keys = ON');
  for (const f of readdirSync(join(root, 'migrations')).sort()) db.exec(readFileSync(join(root, 'migrations', f), 'utf8'));
  return {
    raw: db,
    prepare: sql => new Stmt(db, sql),
    // D1 runs a batch as one transaction: all or nothing
    async batch(stmts) {
      db.exec('BEGIN');
      try { const out = stmts.map(s => s.runSync()); db.exec('COMMIT'); return out; }
      catch (e) { db.exec('ROLLBACK'); throw e; }
    },
  };
}
