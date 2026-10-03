'use strict';

const crypto = require('node:crypto');
const { promisify } = require('node:util');
const scrypt = promisify(crypto.scrypt);
const db = require('./database');

const ACCESS_TTL_MS = 30 * 60 * 1000;
const REFRESH_TTL_MS = 30 * 24 * 60 * 60 * 1000;
const SCRYPT_OPTIONS = { N: 32768, r: 8, p: 1, maxmem: 64 * 1024 * 1024 };

function randomToken() { return crypto.randomBytes(32).toString('base64url'); }
function tokenHash(token) { return crypto.createHash('sha256').update(token).digest('hex'); }
function safeEqual(left, right) {
  const a = Buffer.from(left, 'hex');
  const b = Buffer.from(right, 'hex');
  return a.length === b.length && crypto.timingSafeEqual(a, b);
}

async function hashPassword(password) {
  const salt = crypto.randomBytes(16);
  const key = await scrypt(password, salt, 32, SCRYPT_OPTIONS);
  return `$scrypt$32768-8-1$${salt.toString('base64url')}$${key.toString('base64url')}`;
}

async function verifyPassword(password, encoded) {
  const parts = encoded.split('$');
  if (parts.length !== 5 || parts[1] !== 'scrypt' || parts[2] !== '32768-8-1') return false;
  try {
    const salt = Buffer.from(parts[3], 'base64url');
    const expected = Buffer.from(parts[4], 'base64url');
    if (salt.length !== 16 || expected.length !== 32) return false;
    const actual = await scrypt(password, salt, expected.length, SCRYPT_OPTIONS);
    return crypto.timingSafeEqual(actual, expected);
  } catch {
    return false;
  }
}

function issueSession(userId) {
  const now = Date.now();
  const id = crypto.randomUUID();
  const accessToken = randomToken();
  const refreshToken = randomToken();
  db.run('INSERT INTO sessions(id,user_id,access_hash,refresh_hash,access_expires_at,refresh_expires_at,created_at) VALUES(?,?,?,?,?,?,?)',
    [id, userId, tokenHash(accessToken), tokenHash(refreshToken), now + ACCESS_TTL_MS, now + REFRESH_TTL_MS, now]);
  return { id, accessToken, refreshToken, accessExpiresAt: now + ACCESS_TTL_MS, refreshExpiresAt: now + REFRESH_TTL_MS };
}

function authenticateToken(token) {
  if (!token || token.length > 256) return null;
  const row = db.get('SELECT s.id AS session_id,s.user_id,s.access_expires_at,u.username,u.display_name,u.created_at,u.last_seen FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.access_hash=? AND s.access_expires_at>? AND s.revoked_at IS NULL',
    [tokenHash(token), Date.now()]);
  return row || null;
}

function rotateRefreshToken(token) {
  if (!token || token.length > 256) return null;
  const row = db.get('SELECT id,user_id FROM sessions WHERE refresh_hash=? AND refresh_expires_at>? AND revoked_at IS NULL', [tokenHash(token), Date.now()]);
  if (!row) return null;
  const now = Date.now();
  const accessToken = randomToken();
  const refreshToken = randomToken();
  const previousHash = tokenHash(token);
  const changed = db.run('UPDATE sessions SET access_hash=?,refresh_hash=?,access_expires_at=?,refresh_expires_at=? WHERE id=? AND refresh_hash=? AND refresh_expires_at>? AND revoked_at IS NULL',
    [tokenHash(accessToken), tokenHash(refreshToken), now + ACCESS_TTL_MS, now + REFRESH_TTL_MS, row.id, previousHash, now]);
  if (changed.changes !== 1) return null;
  return { userId: row.user_id, accessToken, refreshToken, accessExpiresAt: now + ACCESS_TTL_MS, refreshExpiresAt: now + REFRESH_TTL_MS };
}

module.exports = { hashPassword, verifyPassword, issueSession, authenticateToken, rotateRefreshToken, tokenHash, safeEqual };
