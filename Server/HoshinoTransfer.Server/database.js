'use strict';

process.on('warning', (warning) => {
  if (warning.name === 'ExperimentalWarning' && /SQLite/i.test(warning.message)) return;
  console.warn(`${warning.name}: ${warning.message}`);
});

const fs = require('node:fs');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');

const dataDir = path.resolve(process.env.HOSHINO_DATA_DIR || path.join(__dirname, 'data'));
fs.mkdirSync(dataDir, { recursive: true });
const databasePath = process.env.HOSHINO_TEST_MEMORY === '1' ? ':memory:' : path.join(dataDir, 'hoshinotransfer.sqlite');
const db = new DatabaseSync(databasePath, { enableForeignKeyConstraints: true });
db.exec('CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at INTEGER NOT NULL)');

const migrations = [
  { version: 1, name: 'initial-schema', up: () => db.exec(fs.readFileSync(path.join(__dirname, 'schema.sql'), 'utf8')) },
  { version: 2, name: 'pairing-and-duplicate-detection', up: () => {
    const itemColumns = new Set(db.prepare('PRAGMA table_info(transfer_items)').all().map((column) => column.name));
    if (!itemColumns.has('duplicate_detected')) db.exec('ALTER TABLE transfer_items ADD COLUMN duplicate_detected INTEGER NOT NULL DEFAULT 0');
    db.exec(`CREATE TABLE IF NOT EXISTS pairing_tokens (
      id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
      code_hash TEXT NOT NULL, expires_at INTEGER NOT NULL, used_at INTEGER, created_at INTEGER NOT NULL
    ); CREATE INDEX IF NOT EXISTS idx_pairing_user ON pairing_tokens(user_id, expires_at);`);
  } },
];
for (const migration of migrations) {
  if (db.prepare('SELECT 1 FROM schema_migrations WHERE version=?').get(migration.version)) continue;
  // Each migration is idempotent; an interrupted DDL step can be retried before its version is recorded.
  migration.up();
  db.prepare('INSERT INTO schema_migrations(version,name,applied_at) VALUES(?,?,?)').run(migration.version, migration.name, Date.now());
}

function normalize(params = []) {
  return params.map((value) => value === undefined ? null : typeof value === 'boolean' ? Number(value) : value);
}

module.exports = {
  run(sql, params) { return db.prepare(sql).run(...normalize(params)); },
  get(sql, params) { return db.prepare(sql).get(...normalize(params)); },
  all(sql, params) { return db.prepare(sql).all(...normalize(params)); },
  transaction(fn) {
    db.exec('BEGIN IMMEDIATE');
    try {
      const value = fn();
      db.exec('COMMIT');
      return value;
    } catch (error) {
      db.exec('ROLLBACK');
      throw error;
    }
  },
  close() { db.close(); },
};
