'use strict';

const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { pipeline } = require('node:stream/promises');
const { Transform } = require('node:stream');
const db = require('./database');
const auth = require('./auth');

const API = '/api/v1';
const CHUNK_SIZE = 4 * 1024 * 1024;
const MAX_TRANSFER_BYTES = 20 * 1024 * 1024 * 1024;
const MAX_FILES = 100;
const uploadDir = path.resolve(process.env.HOSHINO_UPLOAD_DIR || path.join(__dirname, 'uploads'));
fs.mkdirSync(uploadDir, { recursive: true });
const eventClients = new Map();
const eventDeviceClients = new Map();
const eventSessionClients = new Map();
const rateBuckets = new Map();
const allowedOrigins = new Set((process.env.HOSHINO_ALLOWED_ORIGINS || '').split(',').map((origin) => origin.trim()).filter(Boolean));
const openApiDocument = {
  openapi: '3.0.3',
  info: { title: 'HoshinoTransfer API', version: '1.0.0' },
  servers: [{ url: 'https://rt2ucj.taild7fb6f.ts.net/api/v1' }],
  components: { securitySchemes: { bearerAuth: { type: 'http', scheme: 'bearer' } } },
  paths: {
    '/health': { get: { summary: 'Health check', responses: { '200': { description: 'Healthy' } } } },
    '/auth/register': { post: { summary: 'Register and create a session', responses: { '201': { description: 'Created' } } } },
    '/auth/login': { post: { summary: 'Create a session', responses: { '200': { description: 'Authenticated' } } } },
    '/auth/refresh': { post: { summary: 'Rotate session credentials', responses: { '200': { description: 'Rotated' } } } },
    '/auth/logout': { post: { security: [{ bearerAuth: [] }], summary: 'Revoke current session', responses: { '200': { description: 'Revoked' } } } },
    '/users/me': { get: { security: [{ bearerAuth: [] }], summary: 'Current profile', responses: { '200': { description: 'Profile' } } } },
    '/friends': { get: { security: [{ bearerAuth: [] }], summary: 'List friend relationships', responses: { '200': { description: 'Friends' } } } },
    '/friends/request': { post: { security: [{ bearerAuth: [] }], summary: 'Request a friend by username', responses: { '201': { description: 'Pending' } } } },
    '/devices': { get: { security: [{ bearerAuth: [] }], summary: 'List registered devices', responses: { '200': { description: 'Devices' } } }, post: { security: [{ bearerAuth: [] }], summary: 'Register a device', responses: { '201': { description: 'Registered' } } } },
    '/chats': { get: { security: [{ bearerAuth: [] }], summary: 'List 1-to-1 chats', responses: { '200': { description: 'Chats' } } }, post: { security: [{ bearerAuth: [] }], summary: 'Open a friend chat', responses: { '200': { description: 'Chat' } } } },
    '/events': { get: { security: [{ bearerAuth: [] }], summary: 'Authenticated server-sent event stream', responses: { '200': { description: 'Realtime event stream' } } } },
    '/transfers': { get: { security: [{ bearerAuth: [] }], summary: 'List recent transfers', responses: { '200': { description: 'Transfers' } } } },
    '/transfers/create': { post: { security: [{ bearerAuth: [] }], summary: 'Create a Server Relay transfer', responses: { '201': { description: 'Transfer request' } } } },
    '/transfers/{transferId}': { get: { security: [{ bearerAuth: [] }], summary: 'Read authorized transfer status', parameters: [{ name: 'transferId', in: 'path', required: true, schema: { type: 'string', format: 'uuid' } }], responses: { '200': { description: 'Transfer' }, '404': { description: 'Not found' } } } },
    '/transfers/{transferId}/accept': { post: { security: [{ bearerAuth: [] }], summary: 'Accept incoming transfer', responses: { '200': { description: 'Accepted' } } } },
    '/transfers/{transferId}/decline': { post: { security: [{ bearerAuth: [] }], summary: 'Decline incoming transfer', responses: { '200': { description: 'Declined' } } } },
    '/transfers/{transferId}/cancel': { post: { security: [{ bearerAuth: [] }], summary: 'Cancel authorized transfer', responses: { '200': { description: 'Cancelled' } } } },
    '/transfers/{transferId}/resume': { post: { security: [{ bearerAuth: [] }], summary: 'Resume paused transfer', responses: { '200': { description: 'Resumed' } } } },
    '/transfers/{transferId}/complete': { post: { security: [{ bearerAuth: [] }], summary: 'Assemble and SHA-256 verify uploaded chunks', responses: { '200': { description: 'Verified' }, '422': { description: 'Hash mismatch' } } } },
    '/chats/{chatId}/messages': { get: { security: [{ bearerAuth: [] }], summary: 'Read messages', responses: { '200': { description: 'Messages' } } }, post: { security: [{ bearerAuth: [] }], summary: 'Send a chat message', responses: { '201': { description: 'Message sent' } } } },
  },
};

class HttpError extends Error {
  constructor(status, message, code = 'request_failed') { super(message); this.status = status; this.code = code; }
}

function sendJson(res, status, value) {
  const body = Buffer.from(JSON.stringify(value));
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': body.length,
    'cache-control': 'no-store',
    'x-content-type-options': 'nosniff',
    'x-frame-options': 'DENY',
    'referrer-policy': 'no-referrer',
    'content-security-policy': "default-src 'none'; frame-ancestors 'none'",
  });
  res.end(body);
}

function iso(value) { return value == null ? null : new Date(value).toISOString(); }
function publicUser(row) {
  return { id: row.id, username: row.username, displayName: row.display_name, createdAt: iso(row.created_at), lastSeen: iso(row.last_seen) };
}
function fail(status, message, code) { throw new HttpError(status, message, code); }

async function readJson(req, maxBytes = 1024 * 1024) {
  const parts = [];
  let bytes = 0;
  for await (const chunk of req) {
    bytes += chunk.length;
    if (bytes > maxBytes) fail(413, 'Request body is too large.', 'body_too_large');
    parts.push(chunk);
  }
  if (!bytes) return {};
  try {
    const value = JSON.parse(Buffer.concat(parts).toString('utf8'));
    if (!value || typeof value !== 'object' || Array.isArray(value)) fail(400, 'Expected a JSON object.', 'invalid_json');
    return value;
  } catch (error) {
    if (error instanceof HttpError) throw error;
    fail(400, 'Malformed JSON request.', 'invalid_json');
  }
}

function rateLimit(req, pathname) {
  const now = Date.now();
  const authRoute = /^\/api\/v1\/auth\/(register|login|refresh)$/.test(pathname);
  const windowMs = authRoute ? 10 * 60 * 1000 : 60 * 1000;
  const max = authRoute ? 25 : 360;
  const key = `${req.socket.remoteAddress || 'unknown'}:${authRoute ? 'auth' : 'api'}`;
  let bucket = rateBuckets.get(key);
  if (!bucket || now - bucket.start >= windowMs) bucket = { start: now, count: 0 };
  bucket.count += 1;
  rateBuckets.set(key, bucket);
  if (bucket.count > max) fail(429, 'Too many requests. Try again later.', 'rate_limited');
  if (rateBuckets.size > 10000) {
    for (const [id, item] of rateBuckets) if (now - item.start > windowMs * 2) rateBuckets.delete(id);
  }
}

function bearer(req) {
  const value = req.headers.authorization || '';
  const match = /^Bearer ([A-Za-z0-9_-]{32,256})$/.exec(value);
  return match ? match[1] : null;
}

function requireUser(req) {
  const session = auth.authenticateToken(bearer(req));
  if (!session) fail(401, 'Authentication required or session expired.', 'unauthorized');
  return session;
}

function userById(userId) {
  const row = db.get('SELECT id,username,display_name,created_at,last_seen FROM users WHERE id=?', [userId]);
  if (!row) fail(404, 'User not found.', 'not_found');
  return row;
}

function removeTransferFiles(transferId) {
  const folder = path.resolve(uploadDir, transferId);
  if (folder.startsWith(`${uploadDir}${path.sep}`)) fs.rmSync(folder, { recursive: true, force: true });
}

function cleanupExpiredTransfers() {
  const expired = db.all('SELECT id FROM transfers WHERE expires_at<=?', [Date.now()]);
  for (const transfer of expired) removeTransferFiles(transfer.id);
  if (expired.length) db.run('DELETE FROM transfers WHERE expires_at<=?', [Date.now()]);
}

function emit(userId, event, data) {
  const clients = eventClients.get(userId);
  if (!clients) return;
  const payload = `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
  for (const response of clients) {
    if (!response.destroyed && !response.writableEnded) response.write(payload);
  }
}

function safeFileName(input) {
  if (typeof input !== 'string') return '';
  return input.replace(/\\/g, '/').split('/').pop().replace(/[<>:"|?*\u0000-\u001f]/g, '_').replace(/^\.+$/, '_').trim().slice(0, 240);
}

function acceptedFriends(userA, userB) {
  return Boolean(db.get("SELECT 1 FROM friendships WHERE state='Accepted' AND ((user_id=? AND friend_id=?) OR (user_id=? AND friend_id=?)) LIMIT 1", [userA, userB, userB, userA]));
}

function transferRecord(transferId, userId) {
  const transfer = db.get('SELECT * FROM transfers WHERE id=? AND (sender_id=? OR receiver_id=?)', [transferId, userId, userId]);
  if (!transfer) fail(404, 'Transfer not found.', 'not_found');
  const items = db.all('SELECT i.*,COALESCE((SELECT SUM(c.size) FROM transfer_chunks c WHERE c.item_id=i.id),0) AS received_bytes FROM transfer_items i WHERE i.transfer_id=? ORDER BY i.created_at', [transferId]);
  let directInfo = null;
  if (transfer.direct_info) {
    try {
      const registered = JSON.parse(transfer.direct_info);
      directInfo = { host: registered.host, port: registered.port };
      if (userId === transfer.receiver_id) directInfo.token = registered.token;
    } catch { directInfo = null; }
  }
  return {
    id: transfer.id, senderId: transfer.sender_id, receiverId: transfer.receiver_id, status: transfer.status,
    transport: transfer.transport, expiresAt: iso(transfer.expires_at), createdAt: iso(transfer.created_at), chunkSize: CHUNK_SIZE,
    directInfo, relayRequested: Boolean(transfer.relay_requested),
    items: items.map((item) => {
      const nextChunkIndex = db.get('SELECT COUNT(*) AS count FROM transfer_chunks WHERE item_id=?', [item.id]).count;
      return { id: item.id, fileName: item.file_name, size: item.size, mimeType: item.mime_type, sha256: item.sha256,
        status: item.status, duplicateDetected: Boolean(item.duplicate_detected), receivedBytes: item.received_bytes, nextChunkIndex };
    }),
  };
}

function ownedTransfer(transferId, userId) {
  const row = db.get('SELECT * FROM transfers WHERE id=? AND (sender_id=? OR receiver_id=?)', [transferId, userId, userId]);
  if (!row) fail(404, 'Transfer not found.', 'not_found');
  if (row.expires_at <= Date.now()) fail(410, 'Transfer session expired.', 'transfer_expired');
  return row;
}

function pairKey(userA, userB) { return userA < userB ? [userA, userB] : [userB, userA]; }

async function writeChunk(req, destination, expectedSize) {
  if (!Number.isInteger(expectedSize) || expectedSize < 0 || expectedSize > CHUNK_SIZE) fail(413, 'Invalid chunk size.', 'chunk_too_large');
  const contentLength = Number(req.headers['content-length']);
  if (!Number.isInteger(contentLength) || contentLength !== expectedSize) fail(400, 'Content-Length does not match the expected chunk size.', 'invalid_chunk_length');
  let size = 0;
  const digest = crypto.createHash('sha256');
  const limiter = new Transform({
    transform(chunk, encoding, callback) {
      size += chunk.length;
      if (size > expectedSize || size > CHUNK_SIZE) return callback(new HttpError(413, 'Chunk exceeds its permitted size.', 'chunk_too_large'));
      digest.update(chunk);
      callback(null, chunk);
    },
  });
  await pipeline(req, limiter, fs.createWriteStream(destination, { flags: 'wx' }));
  if (size !== expectedSize) {
    fs.rmSync(destination, { force: true });
    fail(400, 'Chunk ended before its declared size.', 'incomplete_chunk');
  }
  return { size, sha256: digest.digest('hex') };
}

async function assembleItem(transferId, item) {
  const itemDir = path.join(uploadDir, transferId, item.id);
  const finalPath = path.join(itemDir, 'assembled.bin');
  const temporaryPath = path.join(itemDir, `assembled.${crypto.randomUUID()}.tmp`);
  fs.mkdirSync(itemDir, { recursive: true });
  const chunkCount = Math.ceil(item.size / CHUNK_SIZE);
  const rows = db.all('SELECT chunk_index,size,sha256 FROM transfer_chunks WHERE item_id=? ORDER BY chunk_index', [item.id]);
  if (rows.length !== chunkCount) fail(409, `File ${item.file_name} is missing chunks.`, 'chunks_incomplete');
  for (let i = 0; i < chunkCount; i += 1) {
    const expected = Math.min(CHUNK_SIZE, item.size - i * CHUNK_SIZE);
    if (rows[i].chunk_index !== i || rows[i].size !== expected) fail(409, `File ${item.file_name} has an incomplete chunk sequence.`, 'chunks_incomplete');
  }
  const output = fs.createWriteStream(temporaryPath, { flags: 'wx' });
  const hash = crypto.createHash('sha256');
  let total = 0;
  try {
    for (let index = 0; index < chunkCount; index += 1) {
      const part = path.join(itemDir, `${index}.chunk`);
      for await (const block of fs.createReadStream(part)) {
        total += block.length;
        hash.update(block);
        if (!output.write(block)) await new Promise((resolve, reject) => {
          output.once('drain', resolve);
          output.once('error', reject);
        });
      }
    }
    await new Promise((resolve, reject) => {
      output.once('error', reject);
      output.end(resolve);
    });
  } catch (error) {
    output.destroy();
    fs.rmSync(temporaryPath, { force: true });
    throw error;
  }
  const actualHash = hash.digest('hex');
  if (total !== item.size || !auth.safeEqual(actualHash, item.sha256)) {
    fs.rmSync(temporaryPath, { force: true });
    fail(422, `SHA-256 verification failed for ${item.file_name}.`, 'hash_mismatch');
  }
  fs.renameSync(temporaryPath, finalPath);
  return finalPath;
}

async function route(req, res, pathname, searchParams) {
  if (req.method === 'GET' && pathname === '/api/openapi.json') return sendJson(res, 200, openApiDocument);
  if (req.method === 'GET' && pathname === '/api/docs') {
    const html = '<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>HoshinoTransfer API</title><body style="font:16px system-ui;max-width:760px;margin:48px auto;padding:0 20px;background:#10141d;color:#f3f4f8"><h1>HoshinoTransfer API</h1><p>Development API reference. Open the OpenAPI document for the endpoint schema.</p><p><a style="color:#b8adff" href="/api/openapi.json">OpenAPI JSON</a></p><p>Transfer bytes are Server Relay. Direct Wi-Fi, P2P and Lightning are not advertised by this API.</p></body></html>';
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'content-length': Buffer.byteLength(html), 'cache-control': 'no-store', 'x-content-type-options': 'nosniff', 'content-security-policy': "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'" });
    return res.end(html);
  }
  if (req.method === 'GET' && (pathname === '/api/health' || pathname === `${API}/health`)) {
    return sendJson(res, 200, { status: 'ok', service: 'HoshinoTransfer.Server', apiVersion: 1, time: new Date().toISOString() });
  }

  if (req.method === 'POST' && pathname === `${API}/auth/register`) {
    const body = await readJson(req);
    const username = typeof body.username === 'string' ? body.username.trim() : '';
    const displayName = typeof body.displayName === 'string' ? body.displayName.trim() : username;
    const password = typeof body.password === 'string' ? body.password : '';
    if (!/^[A-Za-z0-9_.-]{3,32}$/.test(username)) fail(400, 'Username must be 3–32 letters, digits, dots, dashes, or underscores.', 'invalid_username');
    if (password.length < 10 || Buffer.byteLength(password, 'utf8') > 128) fail(400, 'Password must be at least 10 characters and at most 128 bytes.', 'invalid_password');
    if (!displayName || displayName.length > 60) fail(400, 'Display name must be 1–60 characters.', 'invalid_display_name');
    const existing = db.get('SELECT id FROM users WHERE username=? COLLATE NOCASE', [username]);
    if (existing) fail(409, 'Username is already registered.', 'username_taken');
    const id = crypto.randomUUID();
    const now = Date.now();
    const passwordHash = await auth.hashPassword(password);
    try {
      db.run('INSERT INTO users(id,username,display_name,password_hash,created_at,last_seen) VALUES(?,?,?,?,?,?)', [id, username, displayName, passwordHash, now, now]);
    } catch (error) {
      if (String(error.code).startsWith('SQLITE_CONSTRAINT')) fail(409, 'Username is already registered.', 'username_taken');
      throw error;
    }
    const session = auth.issueSession(id);
    return sendJson(res, 201, { user: publicUser(userById(id)), session });
  }

  if (req.method === 'POST' && pathname === `${API}/auth/login`) {
    const body = await readJson(req);
    const username = typeof body.username === 'string' ? body.username.trim() : '';
    const password = typeof body.password === 'string' ? body.password : '';
    const user = db.get('SELECT id,username,display_name,password_hash,created_at,last_seen FROM users WHERE username=? COLLATE NOCASE', [username]);
    if (!user || !(await auth.verifyPassword(password, user.password_hash))) fail(401, 'Username or password is incorrect.', 'invalid_credentials');
    db.run('UPDATE users SET last_seen=? WHERE id=?', [Date.now(), user.id]);
    const session = auth.issueSession(user.id);
    return sendJson(res, 200, { user: publicUser({ ...user, last_seen: Date.now() }), session });
  }

  if (req.method === 'POST' && pathname === `${API}/auth/refresh`) {
    const body = await readJson(req);
    const session = auth.rotateRefreshToken(body.refreshToken);
    if (!session) fail(401, 'Refresh credential is invalid, expired, or already used.', 'invalid_refresh_token');
    return sendJson(res, 200, { session });
  }

  if (req.method === 'GET' && pathname === `${API}/events`) {
    const user = requireUser(req);
    const deviceId = typeof req.headers['x-device-id'] === 'string' ? req.headers['x-device-id'] : '';
    if (deviceId && !db.get('SELECT id FROM devices WHERE id=? AND user_id=?', [deviceId, user.user_id])) fail(403, 'Device does not belong to this account.', 'invalid_device');
    res.writeHead(200, {
      'content-type': 'text/event-stream; charset=utf-8', 'cache-control': 'no-cache, no-transform',
      connection: 'keep-alive', 'x-accel-buffering': 'no', 'x-content-type-options': 'nosniff',
    });
    res.flushHeaders?.();
    res.write(`event: ready\ndata: ${JSON.stringify({ userId: user.user_id, deviceId: deviceId || null, time: new Date().toISOString() })}\n\n`);
    if (!eventClients.has(user.user_id)) eventClients.set(user.user_id, new Set());
    eventClients.get(user.user_id).add(res);
    if (!eventSessionClients.has(user.session_id)) eventSessionClients.set(user.session_id, new Set());
    eventSessionClients.get(user.session_id).add(res);
    if (deviceId) {
      if (!eventDeviceClients.has(deviceId)) eventDeviceClients.set(deviceId, new Set());
      eventDeviceClients.get(deviceId).add(res);
      db.run('UPDATE devices SET last_seen=? WHERE id=? AND user_id=?', [Date.now(), deviceId, user.user_id]);
    }
    db.run('UPDATE users SET last_seen=? WHERE id=?', [Date.now(), user.user_id]);
    const heartbeat = setInterval(() => { if (!res.destroyed && !res.writableEnded) res.write(': ping\n\n'); }, 25000);
    heartbeat.unref();
    const expiresIn = Math.max(0, user.access_expires_at - Date.now());
    const expireTimer = setTimeout(() => res.end(), expiresIn);
    expireTimer.unref();
    const cleanup = () => {
      clearInterval(heartbeat);
      clearTimeout(expireTimer);
      const clients = eventClients.get(user.user_id);
      if (clients) { clients.delete(res); if (!clients.size) eventClients.delete(user.user_id); }
      const sessionClients = eventSessionClients.get(user.session_id);
      if (sessionClients) { sessionClients.delete(res); if (!sessionClients.size) eventSessionClients.delete(user.session_id); }
      if (deviceId) {
        const deviceClients = eventDeviceClients.get(deviceId);
        if (deviceClients) { deviceClients.delete(res); if (!deviceClients.size) eventDeviceClients.delete(deviceId); }
        db.run('UPDATE devices SET last_seen=? WHERE id=?', [Date.now(), deviceId]);
      }
    };
    res.on('close', cleanup);
    return;
  }

  const user = requireUser(req);

  if (req.method === 'POST' && pathname === `${API}/auth/logout`) {
    db.run('UPDATE sessions SET revoked_at=? WHERE id=? AND revoked_at IS NULL', [Date.now(), user.session_id]);
    const connections = eventSessionClients.get(user.session_id);
    if (connections) for (const response of connections) response.end();
    return sendJson(res, 200, { status: 'logged_out' });
  }

  if (req.method === 'GET' && pathname === `${API}/users/me`) {
    return sendJson(res, 200, { user: publicUser(userById(user.user_id)) });
  }

  if (req.method === 'PATCH' && pathname === `${API}/users/me`) {
    const body = await readJson(req);
    const displayName = typeof body.displayName === 'string' ? body.displayName.trim() : '';
    if (!displayName || displayName.length > 60) fail(400, 'Display name must be 1–60 characters.', 'invalid_display_name');
    db.run('UPDATE users SET display_name=? WHERE id=?', [displayName, user.user_id]);
    return sendJson(res, 200, { user: publicUser(userById(user.user_id)) });
  }

  if (req.method === 'GET' && pathname === `${API}/users/search`) {
    const query = (searchParams.get('q') || '').trim();
    if (query.length < 3 || query.length > 32) fail(400, 'Search text must be 3–32 characters.', 'invalid_search');
    const escaped = query.replace(/[\\%_]/g, (character) => `\\${character}`);
    const rows = db.all(`SELECT id,username,display_name,created_at,last_seen FROM users
      WHERE id<>? AND username LIKE ? ESCAPE '\\'
      AND NOT EXISTS (SELECT 1 FROM friendships f WHERE ((f.user_id=? AND f.friend_id=users.id) OR (f.user_id=users.id AND f.friend_id=?)) AND f.state='Blocked')
      ORDER BY username LIMIT 20`, [user.user_id, `%${escaped}%`, user.user_id, user.user_id]);
    return sendJson(res, 200, { users: rows.map(publicUser) });
  }

  if (req.method === 'GET' && pathname === `${API}/devices`) {
    const devices = db.all('SELECT id,device_name,platform,last_seen,created_at FROM devices WHERE user_id=? ORDER BY last_seen DESC', [user.user_id]);
    return sendJson(res, 200, { devices: devices.map((device) => ({ id: device.id, deviceName: device.device_name, platform: device.platform, lastSeen: iso(device.last_seen), createdAt: iso(device.created_at), online: eventDeviceClients.has(device.id), status: eventDeviceClients.has(device.id) ? 'Online' : 'Offline' })) });
  }

  if (req.method === 'POST' && pathname === `${API}/devices`) {
    const body = await readJson(req);
    const name = typeof body.deviceName === 'string' ? body.deviceName.trim() : '';
    const platform = body.platform;
    if (!name || name.length > 80) fail(400, 'Device name must be 1–80 characters.', 'invalid_device_name');
    if (!['Windows', 'iOS', 'Other'].includes(platform)) fail(400, 'Platform must be Windows, iOS, or Other.', 'invalid_platform');
    const id = crypto.randomUUID();
    const now = Date.now();
    db.run('INSERT INTO devices(id,user_id,device_name,platform,last_seen,created_at) VALUES(?,?,?,?,?,?)', [id, user.user_id, name, platform, now, now]);
    return sendJson(res, 201, { device: { id, deviceName: name, platform, lastSeen: iso(now), createdAt: iso(now), status: 'Offline' } });
  }

  if (req.method === 'POST' && pathname === `${API}/devices/pairing`) {
    const pairingCode = String(crypto.randomInt(0, 100000000)).padStart(8, '0');
    const id = crypto.randomUUID();
    const now = Date.now();
    db.run('DELETE FROM pairing_tokens WHERE user_id=? AND (expires_at<=? OR used_at IS NOT NULL)', [user.user_id, now]);
    db.run('INSERT INTO pairing_tokens(id,user_id,code_hash,expires_at,created_at) VALUES(?,?,?,?,?)', [id, user.user_id, auth.tokenHash(pairingCode), now + 5 * 60 * 1000, now]);
    return sendJson(res, 201, { pairingId: id, pairingCode, expiresAt: iso(now + 5 * 60 * 1000), expiresInSeconds: 300 });
  }

  if (req.method === 'POST' && pathname === `${API}/devices/pair`) {
    const body = await readJson(req);
    const code = typeof body.pairingCode === 'string' ? body.pairingCode.replace(/\D/g, '') : '';
    const name = typeof body.deviceName === 'string' ? body.deviceName.trim() : '';
    const platform = body.platform;
    if (!/^[0-9]{8}$/.test(code) || !name || name.length > 80 || !['Windows', 'iOS', 'Other'].includes(platform)) fail(400, 'A valid eight-digit pairing code, device name, and platform are required.', 'invalid_pairing_request');
    const pairing = db.get('SELECT id,user_id FROM pairing_tokens WHERE code_hash=? AND expires_at>? AND used_at IS NULL', [auth.tokenHash(code), Date.now()]);
    if (!pairing || pairing.user_id !== user.user_id) fail(404, 'Pairing code is invalid, expired, or belongs to another account.', 'pairing_not_found');
    const deviceId = crypto.randomUUID();
    const now = Date.now();
    db.transaction(() => {
      const updated = db.run('UPDATE pairing_tokens SET used_at=? WHERE id=? AND used_at IS NULL AND expires_at>?', [now, pairing.id, now]);
      if (updated.changes !== 1) fail(409, 'Pairing code has already been used.', 'pairing_consumed');
      db.run('INSERT INTO devices(id,user_id,device_name,platform,last_seen,created_at) VALUES(?,?,?,?,?,?)', [deviceId, user.user_id, name, platform, now, now]);
    });
    emit(user.user_id, 'device.paired', { deviceId, deviceName: name, platform });
    return sendJson(res, 201, { device: { id: deviceId, deviceName: name, platform, lastSeen: iso(now), createdAt: iso(now), status: 'Online' } });
  }

  let match = new RegExp(`^${API}/devices/([0-9a-f-]{36})/heartbeat$`).exec(pathname);
  if (req.method === 'POST' && match) {
    const now = Date.now();
    const changed = db.run('UPDATE devices SET last_seen=? WHERE id=? AND user_id=?', [now, match[1], user.user_id]);
    if (!changed.changes) fail(404, 'Device not found.', 'not_found');
    return sendJson(res, 200, { status: 'Connecting', lastSeen: iso(now) });
  }

  match = new RegExp(`^${API}/devices/([0-9a-f-]{36})$`).exec(pathname);
  if (req.method === 'DELETE' && match) {
    const deviceClients = eventDeviceClients.get(match[1]);
    if (deviceClients) for (const response of deviceClients) response.end();
    const result = db.run('DELETE FROM devices WHERE id=? AND user_id=?', [match[1], user.user_id]);
    if (!result.changes) fail(404, 'Device not found.', 'not_found');
    return sendJson(res, 200, { status: 'removed' });
  }

  if (req.method === 'GET' && pathname === `${API}/friends`) {
    const rows = db.all(`SELECT f.user_id,f.friend_id,f.state,f.created_at,f.updated_at,
      CASE WHEN f.user_id=? THEN u2.id ELSE u1.id END AS peer_id,
      CASE WHEN f.user_id=? THEN u2.username ELSE u1.username END AS peer_username,
      CASE WHEN f.user_id=? THEN u2.display_name ELSE u1.display_name END AS peer_display_name,
      CASE WHEN f.user_id=? THEN u2.last_seen ELSE u1.last_seen END AS peer_last_seen
      FROM friendships f JOIN users u1 ON u1.id=f.user_id JOIN users u2 ON u2.id=f.friend_id
      WHERE f.user_id=? OR f.friend_id=? ORDER BY f.updated_at DESC`,
    [user.user_id, user.user_id, user.user_id, user.user_id, user.user_id, user.user_id]);
    return sendJson(res, 200, { friends: rows.map((row) => ({ user: { id: row.peer_id, username: row.peer_username, displayName: row.peer_display_name, lastSeen: iso(row.peer_last_seen) }, state: row.state, direction: row.user_id === user.user_id ? 'outgoing' : 'incoming', online: eventClients.has(row.peer_id), createdAt: iso(row.created_at), updatedAt: iso(row.updated_at) })) });
  }

  if (req.method === 'POST' && pathname === `${API}/friends/request`) {
    const body = await readJson(req);
    const target = db.get('SELECT id FROM users WHERE username=? COLLATE NOCASE', [typeof body.username === 'string' ? body.username.trim() : '']);
    if (!target) fail(404, 'User not found.', 'not_found');
    if (target.id === user.user_id) fail(400, 'You cannot add yourself.', 'self_friend_request');
    const block = db.get("SELECT 1 FROM friendships WHERE user_id=? AND friend_id=? AND state='Blocked'", [target.id, user.user_id]);
    if (block) fail(403, 'This user does not accept friend requests.', 'blocked');
    const existing = db.get('SELECT state FROM friendships WHERE user_id=? AND friend_id=?', [user.user_id, target.id]);
    if (existing?.state === 'Accepted') return sendJson(res, 200, { state: 'Accepted', user: publicUser(userById(target.id)) });
    if (existing?.state === 'Pending') fail(409, 'Friend request is already pending.', 'request_exists');
    const incoming = db.get("SELECT state FROM friendships WHERE user_id=? AND friend_id=?", [target.id, user.user_id]);
    const now = Date.now();
    if (incoming?.state === 'Pending') {
      db.run("UPDATE friendships SET state='Accepted',updated_at=? WHERE user_id=? AND friend_id=?", [now, target.id, user.user_id]);
      db.run('INSERT INTO friendships(user_id,friend_id,state,created_at,updated_at) VALUES(?,?,?,?,?) ON CONFLICT(user_id,friend_id) DO UPDATE SET state=excluded.state,updated_at=excluded.updated_at', [user.user_id, target.id, 'Accepted', now, now]);
      emit(target.id, 'friend.accept', { userId: user.user_id, state: 'Accepted' });
      emit(user.user_id, 'friend.accept', { userId: target.id, state: 'Accepted' });
      return sendJson(res, 200, { state: 'Accepted', user: publicUser(userById(target.id)) });
    }
    db.run('INSERT INTO friendships(user_id,friend_id,state,created_at,updated_at) VALUES(?,?,?,?,?) ON CONFLICT(user_id,friend_id) DO UPDATE SET state=excluded.state,updated_at=excluded.updated_at', [user.user_id, target.id, 'Pending', now, now]);
    emit(target.id, 'friend.request', { from: publicUser(userById(user.user_id)) });
    return sendJson(res, 201, { state: 'Pending', user: publicUser(userById(target.id)) });
  }

  for (const action of ['accept', 'reject']) {
    if (req.method === 'POST' && pathname === `${API}/friends/${action}`) {
      const body = await readJson(req);
      const requesterId = typeof body.userId === 'string' ? body.userId : '';
      const requestedState = action === 'accept' ? 'Accepted' : 'Rejected';
      const changed = db.run("UPDATE friendships SET state=?,updated_at=? WHERE user_id=? AND friend_id=? AND state='Pending'", [requestedState, Date.now(), requesterId, user.user_id]);
      if (!changed.changes) fail(404, 'Incoming friend request not found.', 'not_found');
      emit(requesterId, `friend.${action}`, { userId: user.user_id, state: requestedState });
      return sendJson(res, 200, { state: requestedState, user: publicUser(userById(requesterId)) });
    }
  }

  if (req.method === 'POST' && pathname === `${API}/friends/block`) {
    const body = await readJson(req);
    const targetId = typeof body.userId === 'string' ? body.userId : '';
    if (targetId === user.user_id) fail(400, 'You cannot block yourself.', 'invalid_user');
    userById(targetId);
    const now = Date.now();
    db.run('INSERT INTO friendships(user_id,friend_id,state,created_at,updated_at) VALUES(?,?,?,?,?) ON CONFLICT(user_id,friend_id) DO UPDATE SET state=\'Blocked\',updated_at=excluded.updated_at', [user.user_id, targetId, 'Blocked', now, now]);
    db.run("DELETE FROM friendships WHERE user_id=? AND friend_id=? AND state<>'Blocked'", [targetId, user.user_id]);
    emit(targetId, 'friend.blocked', { userId: user.user_id });
    return sendJson(res, 200, { state: 'Blocked' });
  }

  if (req.method === 'POST' && pathname === `${API}/friends/unblock`) {
    const body = await readJson(req);
    const targetId = typeof body.userId === 'string' ? body.userId : '';
    const changed = db.run("DELETE FROM friendships WHERE user_id=? AND friend_id=? AND state='Blocked'", [user.user_id, targetId]);
    if (!changed.changes) fail(404, 'Blocked user not found.', 'not_found');
    emit(targetId, 'friend.unblocked', { userId: user.user_id });
    return sendJson(res, 200, { status: 'unblocked' });
  }

  match = new RegExp(`^${API}/friends/([0-9a-f-]{36})$`).exec(pathname);
  if (req.method === 'DELETE' && match) {
    db.run('DELETE FROM friendships WHERE (user_id=? AND friend_id=?) OR (user_id=? AND friend_id=?)', [user.user_id, match[1], match[1], user.user_id]);
    return sendJson(res, 200, { status: 'removed' });
  }

  if (req.method === 'GET' && pathname === `${API}/chats`) {
    const chats = db.all('SELECT c.id,c.user_a,c.user_b,c.created_at,(SELECT content FROM messages m WHERE m.chat_id=c.id ORDER BY m.created_at DESC LIMIT 1) AS last_message,(SELECT created_at FROM messages m WHERE m.chat_id=c.id ORDER BY m.created_at DESC LIMIT 1) AS last_message_at FROM chats c WHERE c.user_a=? OR c.user_b=? ORDER BY COALESCE(last_message_at,c.created_at) DESC', [user.user_id, user.user_id]);
    return sendJson(res, 200, { chats: chats.map((chat) => {
      const peerId = chat.user_a === user.user_id ? chat.user_b : chat.user_a;
      const peer = userById(peerId);
      return { id: chat.id, user: publicUser(peer), online: eventClients.has(peerId), lastMessage: chat.last_message, lastMessageAt: iso(chat.last_message_at), createdAt: iso(chat.created_at) };
    }) });
  }

  if (req.method === 'POST' && pathname === `${API}/chats`) {
    const body = await readJson(req);
    const peerId = typeof body.userId === 'string' ? body.userId : '';
    userById(peerId);
    if (!acceptedFriends(user.user_id, peerId)) fail(403, 'A chat requires an accepted friendship.', 'friendship_required');
    const [userA, userB] = pairKey(user.user_id, peerId);
    let chat = db.get('SELECT id,user_a,user_b,created_at FROM chats WHERE user_a=? AND user_b=?', [userA, userB]);
    if (!chat) {
      const id = crypto.randomUUID();
      const now = Date.now();
      db.run('INSERT INTO chats(id,user_a,user_b,created_at) VALUES(?,?,?,?)', [id, userA, userB, now]);
      chat = { id, user_a: userA, user_b: userB, created_at: now };
    }
    return sendJson(res, 200, { chat: { id: chat.id, userId: peerId, createdAt: iso(chat.created_at) } });
  }

  match = new RegExp(`^${API}/chats/([0-9a-f-]{36})/messages$`).exec(pathname);
  if (match && ['GET', 'POST'].includes(req.method)) {
    const chatId = match[1];
    const chat = db.get('SELECT * FROM chats WHERE id=? AND (user_a=? OR user_b=?)', [chatId, user.user_id, user.user_id]);
    if (!chat) fail(404, 'Chat not found.', 'not_found');
    const peerId = chat.user_a === user.user_id ? chat.user_b : chat.user_a;
    if (req.method === 'GET') {
      const before = Number(searchParams.get('before') || Date.now() + 1);
      const limit = Math.max(1, Math.min(100, Number(searchParams.get('limit') || 50)));
      if (!Number.isFinite(before)) fail(400, 'Invalid message cursor.', 'invalid_cursor');
      const messages = db.all('SELECT id,sender_id,content,transfer_id,created_at,delivered_at,read_at FROM messages WHERE chat_id=? AND created_at<? ORDER BY created_at DESC LIMIT ?', [chatId, before, limit]).reverse();
      const incomingIds = messages.filter((message) => message.sender_id !== user.user_id && !message.delivered_at).map((message) => message.id);
      if (incomingIds.length) {
        const now = Date.now();
        db.run('UPDATE messages SET delivered_at=COALESCE(delivered_at,?) WHERE chat_id=? AND sender_id<>?', [now, chatId, user.user_id]);
        emit(peerId, 'chat.delivered', { chatId, messageIds: incomingIds, deliveredAt: iso(now) });
      }
      return sendJson(res, 200, { messages: messages.map((message) => ({ id: message.id, senderId: message.sender_id, content: message.content, transferId: message.transfer_id, timestamp: iso(message.created_at), status: message.read_at ? 'Read' : message.delivered_at || message.sender_id !== user.user_id ? 'Delivered' : 'Sent' })) });
    }
    const body = await readJson(req);
    const content = typeof body.content === 'string' ? body.content.trim() : '';
    const transferId = typeof body.transferId === 'string' ? body.transferId : null;
    if (content.length > 4000 || (!content && !transferId)) fail(400, 'Message must contain 1–4000 characters or a valid transfer attachment.', 'invalid_message');
    if (transferId) {
      const transfer = db.get('SELECT id FROM transfers WHERE id=? AND sender_id=? AND receiver_id=?', [transferId, user.user_id, peerId]);
      if (!transfer) fail(400, 'Attachment transfer does not belong to this conversation.', 'invalid_attachment');
    }
    const id = crypto.randomUUID();
    const now = Date.now();
    const deliveredAt = eventClients.has(peerId) ? now : null;
    db.run('INSERT INTO messages(id,chat_id,sender_id,content,transfer_id,created_at,delivered_at) VALUES(?,?,?,?,?,?,?)', [id, chatId, user.user_id, content, transferId, now, deliveredAt]);
    const result = { id, chatId, senderId: user.user_id, receiverId: peerId, content, transferId, timestamp: iso(now), status: deliveredAt ? 'Delivered' : 'Sent' };
    emit(peerId, 'chat.message', result);
    emit(user.user_id, 'chat.message', result);
    return sendJson(res, 201, { message: result });
  }

  match = new RegExp(`^${API}/chats/([0-9a-f-]{36})/read$`).exec(pathname);
  if (req.method === 'POST' && match) {
    const chat = db.get('SELECT id FROM chats WHERE id=? AND (user_a=? OR user_b=?)', [match[1], user.user_id, user.user_id]);
    if (!chat) fail(404, 'Chat not found.', 'not_found');
    db.run('UPDATE messages SET read_at=COALESCE(read_at,?),delivered_at=COALESCE(delivered_at,?) WHERE chat_id=? AND sender_id<>?', [Date.now(), Date.now(), match[1], user.user_id]);
    const peerId = db.get('SELECT CASE WHEN user_a=? THEN user_b ELSE user_a END AS id FROM chats WHERE id=?', [user.user_id, match[1]]).id;
    emit(peerId, 'chat.read', { chatId: match[1], readerId: user.user_id, readAt: new Date().toISOString() });
    return sendJson(res, 200, { status: 'read' });
  }

  match = new RegExp(`^${API}/chats/([0-9a-f-]{36})/typing$`).exec(pathname);
  if (req.method === 'POST' && match) {
    const chat = db.get('SELECT user_a,user_b FROM chats WHERE id=? AND (user_a=? OR user_b=?)', [match[1], user.user_id, user.user_id]);
    if (!chat) fail(404, 'Chat not found.', 'not_found');
    const peerId = chat.user_a === user.user_id ? chat.user_b : chat.user_a;
    const body = await readJson(req);
    emit(peerId, 'chat.typing', { chatId: match[1], userId: user.user_id, isTyping: body.isTyping === true, expiresAt: iso(Date.now() + 4000) });
    return sendJson(res, 202, { status: 'accepted' });
  }

  if (req.method === 'GET' && pathname === `${API}/transfers`) {
    const rows = db.all('SELECT id FROM transfers WHERE sender_id=? OR receiver_id=? ORDER BY updated_at DESC LIMIT 100', [user.user_id, user.user_id]);
    return sendJson(res, 200, { transfers: rows.map((row) => transferRecord(row.id, user.user_id)) });
  }

  if (req.method === 'POST' && pathname === `${API}/transfers/create`) {
    const body = await readJson(req);
    const receiverId = typeof body.receiverId === 'string' ? body.receiverId : '';
    const files = body.items;
    userById(receiverId);
    if (receiverId === user.user_id || !acceptedFriends(user.user_id, receiverId)) fail(403, 'Transfers are available between accepted friends only.', 'friendship_required');
    if (!Array.isArray(files) || files.length < 1 || files.length > MAX_FILES) fail(400, `Transfer must contain 1–${MAX_FILES} files.`, 'invalid_items');
    const total = files.reduce((sum, file) => sum + (Number.isSafeInteger(file?.size) && file.size >= 0 ? file.size : 0), 0);
    if (files.some((file) => !Number.isSafeInteger(file?.size) || file.size < 0) || total > MAX_TRANSFER_BYTES) fail(413, 'Transfer size is invalid or exceeds the 20 GiB session limit.', 'transfer_too_large');
    const active = db.get(`SELECT
      (SELECT COUNT(*) FROM transfers WHERE sender_id=? AND status IN ('Pending','Transferring','Paused') AND expires_at>?) AS active_count,
      (SELECT COALESCE(SUM(i.size),0) FROM transfer_items i JOIN transfers t ON t.id=i.transfer_id WHERE t.sender_id=? AND t.status IN ('Pending','Transferring','Paused') AND t.expires_at>?) AS active_bytes`,
    [user.user_id, Date.now(), user.user_id, Date.now()]);
    if (active.active_count >= 10 || active.active_bytes + total > 40 * 1024 * 1024 * 1024) fail(429, 'Active transfer quota reached. Finish or cancel a transfer before creating another.', 'transfer_quota');
    const items = files.map((file) => {
      const fileName = safeFileName(file.fileName);
      const sha256 = typeof file.sha256 === 'string' ? file.sha256.toLowerCase() : '';
      if (!fileName || fileName.length > 240 || !/^[a-f0-9]{64}$/.test(sha256)) fail(400, 'Each file requires a safe file name and SHA-256 digest.', 'invalid_item');
      const candidateMime = typeof file.mimeType === 'string' ? file.mimeType : '';
      const mimeType = /^[A-Za-z0-9!#$&^_.+-]+\/[A-Za-z0-9!#$&^_.+-]+$/.test(candidateMime) && candidateMime.length <= 120 ? candidateMime : 'application/octet-stream';
      const duplicateDetected = Boolean(db.get("SELECT 1 FROM transfer_items i JOIN transfers t ON t.id=i.transfer_id WHERE t.sender_id=? AND t.status='Completed' AND i.size=? AND i.sha256=? LIMIT 1", [user.user_id, file.size, sha256]));
      return { id: crypto.randomUUID(), fileName, size: file.size, sha256, mimeType, duplicateDetected };
    });
    const id = crypto.randomUUID();
    const now = Date.now();
    db.transaction(() => {
      db.run("INSERT INTO transfers(id,sender_id,receiver_id,status,transport,expires_at,created_at,updated_at) VALUES(?,?,?,'Pending','Server Relay',?,?,?)", [id, user.user_id, receiverId, now + 24 * 60 * 60 * 1000, now, now]);
      for (const item of items) db.run("INSERT INTO transfer_items(id,transfer_id,file_name,size,mime_type,sha256,duplicate_detected,status,created_at) VALUES(?,?,?,?,?,?,?,'Pending',?)", [item.id, id, item.fileName, item.size, item.mimeType, item.sha256, item.duplicateDetected, now]);
    });
    const view = transferRecord(id, user.user_id);
    emit(receiverId, 'transfer.request', view);
    return sendJson(res, 201, { transfer: view, chunkSize: CHUNK_SIZE, transport: 'Server Relay' });
  }

  match = new RegExp(`^${API}/transfers/([0-9a-f-]{36})$`).exec(pathname);
  if (req.method === 'GET' && match) return sendJson(res, 200, { transfer: transferRecord(match[1], user.user_id) });

  match = new RegExp(`^${API}/transfers/([0-9a-f-]{36})/progress$`).exec(pathname);
  if (req.method === 'GET' && match) {
    const transfer = ownedTransfer(match[1], user.user_id);
    const record = transferRecord(transfer.id, user.user_id);
    const totalBytes = record.items.reduce((sum, item) => sum + item.size, 0);
    const receivedBytes = record.items.reduce((sum, item) => sum + item.receivedBytes, 0);
    return sendJson(res, 200, { transferId: transfer.id, status: transfer.status, transport: transfer.transport, chunkSize: CHUNK_SIZE, totalBytes, receivedBytes, directInfo: record.directInfo, relayRequested: record.relayRequested, items: record.items.map((item) => ({ itemId: item.id, fileName: item.fileName, size: item.size, receivedBytes: item.receivedBytes, nextChunkIndex: item.nextChunkIndex, chunkCount: Math.ceil(item.size / CHUNK_SIZE) })) });
  }

  match = new RegExp(`^${API}/transfers/([0-9a-f-]{36})/retry$`).exec(pathname);
  if (req.method === 'POST' && match) {
    const prior = ownedTransfer(match[1], user.user_id);
    if (prior.sender_id !== user.user_id || !['Failed', 'Cancelled'].includes(prior.status)) fail(409, 'Only a failed or cancelled transfer may be retried by its sender.', 'invalid_transfer_state');
    if (!acceptedFriends(prior.sender_id, prior.receiver_id)) fail(403, 'The sender and receiver are no longer friends.', 'friendship_required');
    const oldItems = db.all('SELECT * FROM transfer_items WHERE transfer_id=? ORDER BY created_at', [prior.id]);
    const id = crypto.randomUUID();
    const now = Date.now();
    db.transaction(() => {
      db.run("INSERT INTO transfers(id,sender_id,receiver_id,status,transport,expires_at,created_at,updated_at) VALUES(?,?,?,'Pending','Server Relay',?,?,?)", [id, prior.sender_id, prior.receiver_id, now + 24 * 60 * 60 * 1000, now, now]);
      for (const item of oldItems) db.run("INSERT INTO transfer_items(id,transfer_id,file_name,size,mime_type,sha256,duplicate_detected,status,created_at) VALUES(?,?,?,?,?,?,?,'Pending',?)", [crypto.randomUUID(), id, item.file_name, item.size, item.mime_type, item.sha256, item.duplicate_detected, now]);
    });
    const retry = transferRecord(id, user.user_id);
    emit(prior.receiver_id, 'transfer.request', retry);
    return sendJson(res, 201, { transfer: retry, chunkSize: CHUNK_SIZE, transport: 'Server Relay', retryOf: prior.id });
  }

  match = new RegExp(`^${API}/transfers/([0-9a-f-]{36})/direct$`).exec(pathname);
  if (req.method === 'POST' && match) {
    const transfer = ownedTransfer(match[1], user.user_id);
    if (transfer.sender_id !== user.user_id) fail(403, 'Only the sender may register a direct endpoint.', 'forbidden');
    if (transfer.status !== 'Transferring') fail(409, 'Direct endpoints are registered after the receiver accepts.', 'invalid_transfer_state');
    const body = await readJson(req);
    const host = typeof body.host === 'string' ? body.host.trim() : '';
    const port = Number(body.port);
    const token = typeof body.token === 'string' ? body.token : '';
    if (!host || host.length > 253 || !/^[A-Za-z0-9._-]+$/.test(host)) fail(400, 'A valid LAN host is required.', 'invalid_direct_host');
    if (!Number.isInteger(port) || port < 1 || port > 65535) fail(400, 'A valid LAN port is required.', 'invalid_direct_port');
    if (token.length < 32 || token.length > 128 || !/^[a-f0-9]+$/.test(token)) fail(400, 'A direct capability token is required.', 'invalid_direct_token');
    db.run('UPDATE transfers SET direct_info=?,updated_at=? WHERE id=?', [JSON.stringify({ host, port, token }), Date.now(), transfer.id]);
    emit(transfer.receiver_id, 'transfer.direct', { transferId: transfer.id, host, port });
    return sendJson(res, 200, { status: 'registered' });
  }

  match = new RegExp(`^${API}/transfers/([0-9a-f-]{36})/fallback-relay$`).exec(pathname);
  if (req.method === 'POST' && match) {
    const transfer = ownedTransfer(match[1], user.user_id);
    if (transfer.receiver_id !== user.user_id || transfer.status !== 'Transferring') fail(409, 'Only the receiver may request the relay fallback.', 'invalid_transfer_state');
    db.run('UPDATE transfers SET relay_requested=1,updated_at=? WHERE id=?', [Date.now(), transfer.id]);
    emit(transfer.sender_id, 'transfer.fallback', { transferId: transfer.id });
    return sendJson(res, 200, { status: 'relay_requested' });
  }

  match = new RegExp(`^${API}/transfers/([0-9a-f-]{36})/complete-direct$`).exec(pathname);
  if (req.method === 'POST' && match) {
    const transfer = ownedTransfer(match[1], user.user_id);
    if (transfer.receiver_id !== user.user_id || transfer.status !== 'Transferring') fail(409, 'Only the receiver may complete a direct transfer.', 'invalid_transfer_state');
    if (!transfer.direct_info) fail(409, 'No direct endpoint was registered for this transfer.', 'no_direct_channel');
    db.transaction(() => {
      db.run("UPDATE transfer_items SET status='Completed' WHERE transfer_id=?", [transfer.id]);
      db.run("UPDATE transfers SET status='Completed',transport='Direct Wi-Fi',updated_at=? WHERE id=?", [Date.now(), transfer.id]);
    });
    db.run('DELETE FROM transfer_chunks WHERE transfer_id=?', [transfer.id]);
    removeTransferFiles(transfer.id);
    emit(transfer.sender_id, 'transfer.completed', { transferId: transfer.id, transport: 'Direct Wi-Fi' });
    return sendJson(res, 200, { transfer: transferRecord(transfer.id, user.user_id) });
  }

  match = new RegExp(`^${API}/transfers/([0-9a-f-]{36})/fail$`).exec(pathname);
  if (req.method === 'POST' && match) {
    const transfer = ownedTransfer(match[1], user.user_id);
    if (!['Transferring', 'Paused'].includes(transfer.status)) fail(409, 'This transfer cannot be marked failed now.', 'invalid_transfer_state');
    const body = await readJson(req);
    const reason = typeof body.reason === 'string' && body.reason.length <= 200 ? body.reason : 'direct_transfer_failed';
    db.transaction(() => {
      db.run("UPDATE transfers SET status='Failed',updated_at=? WHERE id=?", [Date.now(), transfer.id]);
      db.run("UPDATE transfer_items SET status='Failed' WHERE transfer_id=?", [transfer.id]);
    });
    db.run('DELETE FROM transfer_chunks WHERE transfer_id=?', [transfer.id]);
    removeTransferFiles(transfer.id);
    const peer = transfer.sender_id === user.user_id ? transfer.receiver_id : transfer.sender_id;
    emit(peer, 'transfer.failed', { transferId: transfer.id, reason });
    return sendJson(res, 200, { transfer: transferRecord(transfer.id, user.user_id) });
  }

  match = new RegExp(`^${API}/transfers/([0-9a-f-]{36})/(accept|decline|cancel|pause|resume|complete)$`).exec(pathname);
  if (match && req.method === 'POST') {
    const transferId = match[1];
    const action = match[2];
    const transfer = ownedTransfer(transferId, user.user_id);
    const now = Date.now();
    if (action === 'accept' || action === 'decline') {
      if (transfer.receiver_id !== user.user_id || transfer.status !== 'Pending') fail(409, 'Only the receiver can accept or decline a pending transfer.', 'invalid_transfer_state');
      const status = action === 'accept' ? 'Transferring' : 'Cancelled';
      db.run('UPDATE transfers SET status=?,updated_at=? WHERE id=?', [status, now, transferId]);
      if (action === 'decline') { db.run('DELETE FROM transfer_chunks WHERE transfer_id=?', [transferId]); removeTransferFiles(transferId); }
      emit(transfer.sender_id, `transfer.${action}`, { transferId, status });
    } else if (action === 'cancel') {
      if (['Completed', 'Cancelled', 'Failed'].includes(transfer.status)) fail(409, 'This transfer cannot be cancelled in its current state.', 'invalid_transfer_state');
      db.run("UPDATE transfers SET status='Cancelled',updated_at=? WHERE id=?", [now, transferId]);
      db.run('DELETE FROM transfer_chunks WHERE transfer_id=?', [transferId]);
      db.run("UPDATE transfer_items SET status='Cancelled' WHERE transfer_id=?", [transferId]);
      removeTransferFiles(transferId);
      emit(transfer.sender_id, 'transfer.cancelled', { transferId });
      emit(transfer.receiver_id, 'transfer.cancelled', { transferId });
    } else if (action === 'pause' || action === 'resume') {
      const expected = action === 'pause' ? 'Transferring' : 'Paused';
      const next = action === 'pause' ? 'Paused' : 'Transferring';
      if (transfer.sender_id !== user.user_id || transfer.status !== expected) fail(409, 'Transfer is not in a resumable state for this sender.', 'invalid_transfer_state');
      db.run('UPDATE transfers SET status=?,updated_at=? WHERE id=?', [next, now, transferId]);
      emit(transfer.receiver_id, `transfer.${action}`, { transferId, status: next });
    } else if (action === 'complete') {
      if (transfer.sender_id !== user.user_id || transfer.status !== 'Transferring') fail(409, 'Only the sender can complete an accepted transfer.', 'invalid_transfer_state');
      const items = db.all('SELECT * FROM transfer_items WHERE transfer_id=? ORDER BY created_at', [transferId]);
      try {
        for (const item of items) await assembleItem(transferId, item);
      } catch (error) {
        if (error.code !== 'chunks_incomplete') {
          db.run("UPDATE transfers SET status='Failed',updated_at=? WHERE id=?", [Date.now(), transferId]);
          db.run("UPDATE transfer_items SET status='Failed' WHERE transfer_id=?", [transferId]);
          emit(transfer.receiver_id, 'transfer.failed', { transferId, reason: error.code || 'verification_failed' });
        }
        throw error;
      }
      db.transaction(() => {
        db.run("UPDATE transfer_items SET status='Completed' WHERE transfer_id=?", [transferId]);
        db.run("UPDATE transfers SET status='Completed',updated_at=? WHERE id=?", [Date.now(), transferId]);
      });
      emit(transfer.receiver_id, 'transfer.completed', { transferId });
    }
    return sendJson(res, 200, { transfer: transferRecord(transferId, user.user_id) });
  }

  match = new RegExp(`^${API}/transfers/([0-9a-f-]{36})/items/([0-9a-f-]{36})/chunks/(\\d+)$`).exec(pathname);
  if (req.method === 'PUT' && match) {
    const transfer = ownedTransfer(match[1], user.user_id);
    if (transfer.sender_id !== user.user_id) fail(403, 'Only the sender may upload to an accepted transfer.', 'forbidden');
    if (transfer.status === 'Paused') fail(409, 'Transfer is paused.', 'transfer_paused');
    if (transfer.status !== 'Transferring') fail(409, `Transfer is ${transfer.status}.`, 'invalid_transfer_state');
    const index = Number(match[3]);
    const item = db.get('SELECT * FROM transfer_items WHERE id=? AND transfer_id=?', [match[2], match[1]]);
    if (!item) fail(404, 'Transfer item not found.', 'not_found');
    const chunkCount = Math.ceil(item.size / CHUNK_SIZE);
    if (!Number.isSafeInteger(index) || index < 0 || index >= chunkCount) fail(400, 'Chunk index is outside the expected file range.', 'invalid_chunk_index');
    const expectedSize = Math.min(CHUNK_SIZE, item.size - index * CHUNK_SIZE);
    const existingChunk = db.get('SELECT size,sha256 FROM transfer_chunks WHERE item_id=? AND chunk_index=?', [item.id, index]);
    const nextChunkIndex = db.get('SELECT COUNT(*) AS count FROM transfer_chunks WHERE item_id=?', [item.id]).count;
    if (!existingChunk && index !== nextChunkIndex) fail(409, `Expected chunk ${nextChunkIndex} for this file.`, 'unexpected_chunk_index');
    const itemDir = path.join(uploadDir, transfer.id, item.id);
    fs.mkdirSync(itemDir, { recursive: true });
    const tempPath = path.join(itemDir, `${index}.${crypto.randomUUID()}.tmp`);
    let result;
    try { result = await writeChunk(req, tempPath, expectedSize); }
    catch (error) { fs.rmSync(tempPath, { force: true }); throw error; }
    if (existingChunk) {
      fs.rmSync(tempPath, { force: true });
      if (existingChunk.size === result.size && auth.safeEqual(existingChunk.sha256, result.sha256))
        return sendJson(res, 200, { chunkIndex: index, bytesReceived: result.size, alreadyReceived: true, transport: 'Server Relay' });
      fail(409, 'A different chunk already occupies this index.', 'chunk_conflict');
    }
    const finalPath = path.join(itemDir, `${index}.chunk`);
    let ownsFinalPath = false;
    try {
      fs.linkSync(tempPath, finalPath);
      ownsFinalPath = true;
      fs.rmSync(tempPath, { force: true });
      db.run('INSERT INTO transfer_chunks(transfer_id,item_id,chunk_index,size,sha256,created_at) VALUES(?,?,?,?,?,?)', [transfer.id, item.id, index, result.size, result.sha256, Date.now()]);
    } catch (error) {
      fs.rmSync(tempPath, { force: true });
      if (ownsFinalPath) fs.rmSync(finalPath, { force: true });
      if (String(error.code).startsWith('SQLITE_CONSTRAINT')) fail(409, 'Chunk has already been received.', 'duplicate_chunk');
      if (error.code === 'EEXIST') {
        const raced = db.get('SELECT size,sha256 FROM transfer_chunks WHERE item_id=? AND chunk_index=?', [item.id, index]);
        if (raced && raced.size === result.size && auth.safeEqual(raced.sha256, result.sha256))
          return sendJson(res, 200, { chunkIndex: index, bytesReceived: result.size, alreadyReceived: true, transport: 'Server Relay' });
        fail(409, 'A different chunk already occupies this index.', 'chunk_conflict');
      }
      throw error;
    }
    const total = db.get('SELECT COALESCE(SUM(size),0) AS received FROM transfer_chunks WHERE item_id=?', [item.id]).received;
    emit(transfer.receiver_id, 'transfer.progress', { transferId: transfer.id, itemId: item.id, receivedBytes: total, size: item.size, transport: 'Server Relay' });
    return sendJson(res, 201, { chunkIndex: index, bytesReceived: result.size, chunkSha256: result.sha256, transport: 'Server Relay' });
  }

  match = new RegExp(`^${API}/transfers/([0-9a-f-]{36})/items/([0-9a-f-]{36})/download$`).exec(pathname);
  if (req.method === 'GET' && match) {
    const transfer = ownedTransfer(match[1], user.user_id);
    if (transfer.receiver_id !== user.user_id || transfer.status !== 'Completed') fail(403, 'Only the receiver can download a completed transfer.', 'forbidden');
    const item = db.get('SELECT * FROM transfer_items WHERE id=? AND transfer_id=? AND status=\'Completed\'', [match[2], match[1]]);
    if (!item) fail(404, 'Completed file not found.', 'not_found');
    const filePath = path.join(uploadDir, transfer.id, item.id, 'assembled.bin');
    if (!fs.existsSync(filePath)) fail(410, 'Relayed file is no longer available.', 'file_expired');
    res.writeHead(200, {
      'content-type': item.mime_type, 'content-length': item.size,
      'content-disposition': `attachment; filename*=UTF-8''${encodeURIComponent(item.file_name)}`,
      'cache-control': 'private, no-store', 'x-content-type-options': 'nosniff',
    });
    await pipeline(fs.createReadStream(filePath), res);
    return;
  }

  fail(404, 'Endpoint not found.', 'not_found');
}

function createServer() {
  return http.createServer(async (req, res) => {
    let pathname = '/';
    let searchParams = new URLSearchParams();
    try {
      const parsed = new URL(req.url, 'http://api.invalid');
      pathname = parsed.pathname.replace(/\/$/, '') || '/';
      searchParams = parsed.searchParams;
      const origin = req.headers.origin;
      if (origin && allowedOrigins.has(origin)) {
        res.setHeader('access-control-allow-origin', origin);
        res.setHeader('vary', 'Origin');
        res.setHeader('access-control-allow-headers', 'Authorization, Content-Type, Content-Length');
        res.setHeader('access-control-allow-methods', 'GET, POST, PUT, DELETE, OPTIONS');
        res.setHeader('access-control-max-age', '600');
      }
      if (req.method === 'OPTIONS' && origin && allowedOrigins.has(origin)) {
        res.writeHead(204, { 'cache-control': 'no-store' });
        res.end();
        return;
      }
      rateLimit(req, pathname);
      await route(req, res, pathname, searchParams);
    } catch (error) {
      if (res.headersSent) { if (!res.writableEnded) res.destroy(error); return; }
      const status = error instanceof HttpError ? error.status : 500;
      if (status >= 500) console.error(`[server] ${req.method} ${pathname}: ${error.stack || error.message}`);
      sendJson(res, status, { error: { code: error.code || 'internal_error', message: status >= 500 ? 'The server could not complete the request.' : error.message } });
    }
  });
}

const server = createServer();
server.requestTimeout = 5 * 60 * 1000;
server.headersTimeout = 30 * 1000;
server.keepAliveTimeout = 5 * 1000;

if (require.main === module) {
  const port = Number(process.env.PORT || 3000);
  const host = process.env.HOST || '0.0.0.0';
  cleanupExpiredTransfers();
  const cleanupTimer = setInterval(() => {
    try { cleanupExpiredTransfers(); }
    catch (error) { console.error(`[cleanup] ${error.message}`); }
  }, 60 * 60 * 1000);
  cleanupTimer.unref();
  server.listen(port, host, () => console.log(`LISTENING http://${host}:${server.address().port}`));
  const shutdown = () => server.close(() => { clearInterval(cleanupTimer); db.close(); process.exit(0); });
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
}

module.exports = { createServer, CHUNK_SIZE };
