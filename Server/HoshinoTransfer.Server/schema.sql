PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS users (
  id TEXT PRIMARY KEY,
  username TEXT NOT NULL UNIQUE COLLATE NOCASE,
  display_name TEXT NOT NULL,
  password_hash TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  last_seen INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS sessions (
  id TEXT PRIMARY KEY,
  user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  access_hash TEXT NOT NULL UNIQUE,
  refresh_hash TEXT NOT NULL UNIQUE,
  access_expires_at INTEGER NOT NULL,
  refresh_expires_at INTEGER NOT NULL,
  revoked_at INTEGER,
  created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sessions_user ON sessions(user_id);

CREATE TABLE IF NOT EXISTS devices (
  id TEXT PRIMARY KEY,
  user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  device_name TEXT NOT NULL,
  platform TEXT NOT NULL CHECK(platform IN ('Windows', 'iOS', 'Other')),
  last_seen INTEGER NOT NULL,
  created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_devices_user ON devices(user_id);

CREATE TABLE IF NOT EXISTS pairing_tokens (
  id TEXT PRIMARY KEY,
  user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  code_hash TEXT NOT NULL,
  expires_at INTEGER NOT NULL,
  used_at INTEGER,
  created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_pairing_user ON pairing_tokens(user_id, expires_at);

CREATE TABLE IF NOT EXISTS friendships (
  user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  friend_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  state TEXT NOT NULL CHECK(state IN ('Pending', 'Accepted', 'Rejected', 'Blocked')),
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  PRIMARY KEY(user_id, friend_id),
  CHECK(user_id <> friend_id)
);
CREATE INDEX IF NOT EXISTS idx_friendships_incoming ON friendships(friend_id, state);

CREATE TABLE IF NOT EXISTS chats (
  id TEXT PRIMARY KEY,
  user_a TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  user_b TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  created_at INTEGER NOT NULL,
  UNIQUE(user_a, user_b),
  CHECK(user_a < user_b)
);
CREATE TABLE IF NOT EXISTS messages (
  id TEXT PRIMARY KEY,
  chat_id TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
  sender_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  content TEXT NOT NULL,
  transfer_id TEXT,
  created_at INTEGER NOT NULL,
  delivered_at INTEGER,
  read_at INTEGER
);
CREATE INDEX IF NOT EXISTS idx_messages_chat ON messages(chat_id, created_at);

CREATE TABLE IF NOT EXISTS transfers (
  id TEXT PRIMARY KEY,
  sender_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  receiver_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  status TEXT NOT NULL CHECK(status IN ('Pending', 'Transferring', 'Paused', 'Completed', 'Cancelled', 'Failed')),
  transport TEXT NOT NULL DEFAULT 'Server Relay',
  expires_at INTEGER NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_transfers_participants ON transfers(sender_id, receiver_id, created_at);

CREATE TABLE IF NOT EXISTS transfer_items (
  id TEXT PRIMARY KEY,
  transfer_id TEXT NOT NULL REFERENCES transfers(id) ON DELETE CASCADE,
  file_name TEXT NOT NULL,
  size INTEGER NOT NULL CHECK(size >= 0),
  mime_type TEXT NOT NULL,
  sha256 TEXT NOT NULL,
  duplicate_detected INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL DEFAULT 'Pending',
  created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_transfer_items_transfer ON transfer_items(transfer_id);

CREATE TABLE IF NOT EXISTS transfer_chunks (
  transfer_id TEXT NOT NULL REFERENCES transfers(id) ON DELETE CASCADE,
  item_id TEXT NOT NULL REFERENCES transfer_items(id) ON DELETE CASCADE,
  chunk_index INTEGER NOT NULL CHECK(chunk_index >= 0),
  size INTEGER NOT NULL CHECK(size >= 0),
  sha256 TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  PRIMARY KEY(item_id, chunk_index)
);
