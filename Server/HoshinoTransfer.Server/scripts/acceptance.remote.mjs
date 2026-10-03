'use strict';
// End-to-end acceptance against the production public funnel URL.
// Registers two throwaway accounts, friends them, relays a real file, verifies SHA-256.
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const BASE = process.env.HOSHINO_BASE_URL || 'https://rt2ucj.taild7fb6f.ts.net';
const stamp = Date.now().toString(36);
const password = `acceptance-${stamp}-passw0rd`;

async function api(route, { method = 'GET', token, body, raw, headers = {} } = {}) {
  const response = await fetch(`${BASE}${route}`, {
    method,
    headers: {
      ...(token ? { authorization: `Bearer ${token}` } : {}),
      ...(body !== undefined ? { 'content-type': 'application/json' } : {}),
      ...headers,
    },
    body: raw !== undefined ? raw : body !== undefined ? JSON.stringify(body) : undefined,
  });
  const contentType = response.headers.get('content-type') || '';
  const data = contentType.includes('application/json') ? await response.json() : Buffer.from(await response.arrayBuffer());
  if (!response.ok) throw new Error(`${method} ${route} -> ${response.status}: ${JSON.stringify(data).slice(0, 200)}`);
  return data;
}

function assert(condition, label) {
  if (!condition) throw new Error(`ACCEPTANCE FAIL: ${label}`);
  console.log(`[PASS] ${label}`);
}

(async () => {
  assert((await api('/api/health')).status === 'ok', 'public health via TLS funnel');
  assert((await api('/api/openapi.json')).openapi === '3.0.3', 'public OpenAPI discovery');

  const alice = await api('/api/v1/auth/register', { method: 'POST', body: { username: `acc_a_${stamp}`, displayName: 'Acceptance Alice', password } });
  const bob = await api('/api/v1/auth/register', { method: 'POST', body: { username: `acc_b_${stamp}`, displayName: 'Acceptance Bob', password } });
  assert(alice.session.accessToken && bob.session.accessToken, 'two real accounts registered over the public endpoint');

  await api('/api/v1/friends/request', { method: 'POST', token: alice.session.accessToken, body: { username: `acc_b_${stamp}` } });
  await api('/api/v1/friends/accept', { method: 'POST', token: bob.session.accessToken, body: { userId: alice.user.id } });
  assert((await api('/api/v1/friends', { token: bob.session.accessToken })).friends[0].state === 'Accepted', 'friendship accepted');

  const chat = await api('/api/v1/chats', { method: 'POST', token: alice.session.accessToken, body: { userId: bob.user.id } });
  const message = await api(`/api/v1/chats/${chat.chat.id}/messages`, { method: 'POST', token: alice.session.accessToken, body: { content: 'acceptance hello' } });
  assert(message.message.status === 'Sent' || message.message.status === 'Delivered', 'chat message relayed');

  const bytes = crypto.randomBytes(5 * 1024 * 1024 + 12345);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'hoshino-accept-'));
  const filePath = path.join(dir, 'acceptance.bin');
  fs.writeFileSync(filePath, bytes);
  const fileHash = crypto.createHash('sha256').update(bytes).digest('hex');

  const created = await api('/api/v1/transfers/create', {
    method: 'POST', token: alice.session.accessToken,
    body: { receiverId: bob.user.id, items: [{ fileName: 'acceptance.bin', size: bytes.length, mimeType: 'application/octet-stream', sha256: fileHash }] },
  });
  const transfer = created.transfer;
  assert(transfer.transport === 'Server Relay', 'transfer created with explicit Server Relay transport');

  await api(`/api/v1/transfers/${transfer.id}/accept`, { method: 'POST', token: bob.session.accessToken });
  const chunkSize = created.chunkSize;
  for (let index = 0; index * chunkSize < bytes.length; index++) {
    const start = index * chunkSize;
    const chunk = bytes.subarray(start, Math.min(start + chunkSize, bytes.length));
    await api(`/api/v1/transfers/${transfer.id}/items/${transfer.items[0].id}/chunks/${index}`, {
      method: 'PUT', token: alice.session.accessToken, raw: chunk,
      headers: { 'content-type': 'application/octet-stream', 'content-length': String(chunk.length) },
    });
  }
  const progress = await api(`/api/v1/transfers/${transfer.id}/progress`, { token: bob.session.accessToken });
  assert(progress.receivedBytes === bytes.length, `resume-accurate progress reports ${bytes.length} bytes through the funnel`);

  const completed = await api(`/api/v1/transfers/${transfer.id}/complete`, { method: 'POST', token: alice.session.accessToken });
  assert(completed.transfer.status === 'Completed', 'server assembled and SHA-256 verified the relayed file');

  const downloaded = await api(`/api/v1/transfers/${transfer.id}/items/${transfer.items[0].id}/download`, { token: bob.session.accessToken });
  assert(Buffer.compare(downloaded, bytes) === 0, 'downloaded bytes are byte-identical over the public endpoint');
  assert(crypto.createHash('sha256').update(downloaded).digest('hex') === fileHash, 'SHA-256 of downloaded file matches');

  const senderForbidden = await fetch(`${BASE}/api/v1/transfers/${transfer.id}/items/${transfer.items[0].id}/download`, { headers: { authorization: `Bearer ${alice.session.accessToken}` } });
  assert(senderForbidden.status === 403, 'sender cannot download (receiver-only authorization)');

  await api('/api/v1/auth/logout', { method: 'POST', token: alice.session.accessToken });
  const revoked = await fetch(`${BASE}/api/v1/users/me`, { headers: { authorization: `Bearer ${alice.session.accessToken}` } });
  assert(revoked.status === 401, 'logout revokes the session on the public endpoint');

  fs.rmSync(dir, { recursive: true, force: true });
  console.log(`ACCEPTANCE: ALL CHECKS PASSED (base URL: ${BASE})`);
})().catch((error) => { console.error(error.message); process.exit(1); });
