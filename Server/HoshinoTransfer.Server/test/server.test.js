'use strict';

const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');

const serverDir = path.resolve(__dirname, '..');
let tempDir;
let child;
let baseUrl;
let stdout = '';
let stderr = '';

before(async () => {
  tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'hoshino-transfer-test-'));
  child = spawn(process.execPath, ['server.js'], {
    cwd: serverDir,
    env: { ...process.env, HOST: '127.0.0.1', PORT: '0', HOSHINO_TEST_MEMORY: '1', HOSHINO_DATA_DIR: path.join(tempDir, 'data'), HOSHINO_UPLOAD_DIR: path.join(tempDir, 'uploads') },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  child.stdout.setEncoding('utf8').on('data', (part) => { stdout += part; });
  child.stderr.setEncoding('utf8').on('data', (part) => { stderr += part; });
  const startedAt = Date.now();
  while (Date.now() - startedAt < 20000) {
    const match = /LISTENING http:\/\/127\.0\.0\.1:(\d+)/.exec(stdout);
    if (match) { baseUrl = `http://127.0.0.1:${match[1]}`; return; }
    if (child.exitCode !== null) throw new Error(`Server exited early. stdout=${stdout} stderr=${stderr}`);
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`Server did not start. stdout=${stdout} stderr=${stderr}`);
});

after(async () => {
  if (child && child.exitCode === null) {
    child.kill('SIGTERM');
    await Promise.race([new Promise((resolve) => child.once('exit', resolve)), new Promise((resolve) => setTimeout(resolve, 3000))]);
  }
  if (tempDir) fs.rmSync(tempDir, { recursive: true, force: true });
});

async function request(route, { method = 'GET', token, body, raw, headers = {} } = {}) {
  const response = await fetch(`${baseUrl}${route}`, {
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
  return { response, data };
}

test('health, auth, friend requests, chat events, transfer integrity, and access control', { timeout: 60000 }, async () => {
  let result = await request('/api/health');
  assert.equal(result.response.status, 200);
  assert.equal(result.data.status, 'ok');
  const openApi = await request('/api/openapi.json');
  assert.equal(openApi.response.status, 200);
  assert.equal(openApi.data.openapi, '3.0.3');
  assert.equal(openApi.response.headers.get('access-control-allow-origin'), null, 'browser origins are not wildcard-enabled');

  result = await request('/api/v1/users/me');
  assert.equal(result.response.status, 401);

  const aliceResult = await request('/api/v1/auth/register', { method: 'POST', body: { username: 'alice_test', displayName: 'Alice', password: 'correct horse battery' } });
  assert.equal(aliceResult.response.status, 201, JSON.stringify(aliceResult.data));
  const alice = aliceResult.data.user;
  let aliceToken = aliceResult.data.session.accessToken;
  let aliceRefresh = aliceResult.data.session.refreshToken;
  assert.equal((await request('/api/v1/users/me', { token: aliceToken })).data.user.id, alice.id);

  const bobResult = await request('/api/v1/auth/register', { method: 'POST', body: { username: 'bob_test', displayName: 'Bob', password: 'another safe password' } });
  assert.equal(bobResult.response.status, 201, JSON.stringify(bobResult.data));
  const bob = bobResult.data.user;
  const bobToken = bobResult.data.session.accessToken;

  result = await request('/api/v1/auth/login', { method: 'POST', body: { username: 'alice_test', password: 'incorrect password' } });
  assert.equal(result.response.status, 401);
  result = await request('/api/v1/auth/login', { method: 'POST', body: { username: 'alice_test', password: 'correct horse battery' } });
  assert.equal(result.response.status, 200);

  const refreshed = await request('/api/v1/auth/refresh', { method: 'POST', body: { refreshToken: aliceRefresh } });
  assert.equal(refreshed.response.status, 200);
  aliceToken = refreshed.data.session.accessToken;
  aliceRefresh = refreshed.data.session.refreshToken;
  result = await request('/api/v1/auth/refresh', { method: 'POST', body: { refreshToken: aliceResult.data.session.refreshToken } });
  assert.equal(result.response.status, 401, 'rotated refresh credentials cannot be reused');

  result = await request('/api/v1/friends/request', { method: 'POST', token: aliceToken, body: { username: 'bob_test' } });
  assert.equal(result.response.status, 201);
  assert.equal(result.data.state, 'Pending');
  result = await request('/api/v1/friends/accept', { method: 'POST', token: bobToken, body: { userId: alice.id } });
  assert.equal(result.response.status, 200);
  assert.equal(result.data.state, 'Accepted');

  const device = await request('/api/v1/devices', { method: 'POST', token: aliceToken, body: { deviceName: 'Test PC', platform: 'Windows' } });
  assert.equal(device.response.status, 201);
  assert.equal((await request('/api/v1/devices', { token: aliceToken })).data.devices.length, 1);

  const pairing = await request('/api/v1/devices/pairing', { method: 'POST', token: aliceToken });
  assert.equal(pairing.response.status, 201);
  assert.match(pairing.data.pairingCode, /^[0-9]{8}$/);
  const bobDevice = await request('/api/v1/devices', { method: 'POST', token: bobToken, body: { deviceName: "Bob's iPhone", platform: 'iOS' } });
  const paired = await request('/api/v1/devices/pair', { method: 'POST', token: bobToken, body: { pairingCode: pairing.data.pairingCode, deviceName: "Bob's iPhone", platform: 'iOS' } });
  assert.equal(paired.response.status, 404, 'a pairing code can only be consumed by its own account');
  const selfPaired = await request('/api/v1/devices/pair', { method: 'POST', token: aliceToken, body: { pairingCode: pairing.data.pairingCode, deviceName: 'Second PC', platform: 'Windows' } });
  assert.equal(selfPaired.response.status, 201, 'same-account pairing succeeds');
  assert.equal((await request('/api/v1/devices', { token: aliceToken })).data.devices.length, 2);
  result = await request('/api/v1/devices/pair', { method: 'POST', token: aliceToken, body: { pairingCode: pairing.data.pairingCode, deviceName: 'Third PC', platform: 'Windows' } });
  assert.equal(result.response.status, 404, 'pairing codes are single-use');

  const search = await request('/api/v1/users/search?q=bob', { token: aliceToken });
  assert.equal(search.response.status, 200);
  assert.equal(search.data.users.some((user) => user.id === bob.id), true);
  const forbiddenSearch = await request('/api/v1/users/search?q=ab', { token: aliceToken });
  assert.equal(forbiddenSearch.response.status, 400);

  const chatResult = await request('/api/v1/chats', { method: 'POST', token: aliceToken, body: { userId: bob.id } });
  assert.equal(chatResult.response.status, 200);
  const chatId = chatResult.data.chat.id;

  const abort = new AbortController();
  const eventResponse = await fetch(`${baseUrl}/api/v1/events`, { headers: { authorization: `Bearer ${bobToken}` }, signal: abort.signal });
  assert.equal(eventResponse.status, 200);
  const reader = eventResponse.body.getReader();
  const ready = await reader.read();
  assert.match(new TextDecoder().decode(ready.value), /event: ready/);
  const sent = await request(`/api/v1/chats/${chatId}/messages`, { method: 'POST', token: aliceToken, body: { content: 'hello over SSE' } });
  assert.equal(sent.response.status, 201);
  let received = '';
  const eventWait = (async () => {
    while (!received.includes('hello over SSE')) {
      const next = await reader.read();
      if (next.done) break;
      received += new TextDecoder().decode(next.value);
    }
  })();
  await Promise.race([eventWait, new Promise((_, reject) => setTimeout(() => reject(new Error('chat event not delivered')), 3000))]);
  abort.abort();
  assert.match(received, /event: chat\.message/);
  assert.deepEqual((await request(`/api/v1/chats/${chatId}/messages`, { token: bobToken })).data.messages.map((item) => item.content), ['hello over SSE']);

  const bytes = Buffer.from('actual streamed transfer data\n');
  const fileHash = crypto.createHash('sha256').update(bytes).digest('hex');
  const created = await request('/api/v1/transfers/create', {
    method: 'POST', token: aliceToken,
    body: { receiverId: bob.id, items: [{ fileName: '../unsafe.txt', size: bytes.length, mimeType: 'text/plain', sha256: fileHash }, { fileName: 'duplicate.bin', size: bytes.length, mimeType: 'text/plain', sha256: fileHash }] },
  });
  assert.equal(created.response.status, 201, JSON.stringify(created.data));
  const transfer = created.data.transfer;
  assert.equal(transfer.transport, 'Server Relay');
  assert.equal(transfer.items[0].fileName, 'unsafe.txt');
  assert.equal(transfer.items[1].fileName, 'duplicate.bin');
  assert.equal(transfer.items[1].duplicateDetected, false, 'first completed transfer with this digest has not happened yet');
  const duplicates = created.data.transfer.items.map((item) => item.id);

  const unauthorizedChunk = await request(`/api/v1/transfers/${transfer.id}/items/${transfer.items[0].id}/chunks/0`, {
    method: 'PUT', token: bobToken, raw: bytes, headers: { 'content-type': 'application/octet-stream', 'content-length': String(bytes.length) },
  });
  assert.equal(unauthorizedChunk.response.status, 403, 'only the sender uploads chunks');
  const skippedIndex = await request(`/api/v1/transfers/${transfer.id}/items/${transfer.items[0].id}/chunks/1`, {
    method: 'PUT', token: aliceToken, raw: bytes, headers: { 'content-type': 'application/octet-stream', 'content-length': String(bytes.length) },
  });
  assert.equal(skippedIndex.response.status, 409, 'chunks must arrive in order');

  result = await request(`/api/v1/transfers/${transfer.id}/accept`, { method: 'POST', token: bobToken });
  assert.equal(result.data.transfer.status, 'Transferring');
  result = await request(`/api/v1/transfers/${transfer.id}/items/${transfer.items[0].id}/chunks/0`, {
    method: 'PUT', token: aliceToken, raw: bytes, headers: { 'content-type': 'application/octet-stream', 'content-length': String(bytes.length) },
  });
  assert.equal(result.response.status, 201, JSON.stringify(result.data));
  assert.equal(result.data.bytesReceived, bytes.length);
  result = await request(`/api/v1/transfers/${transfer.id}/items/${transfer.items[0].id}/chunks/0`, {
    method: 'PUT', token: aliceToken, raw: bytes, headers: { 'content-type': 'application/octet-stream', 'content-length': String(bytes.length) },
  });
  assert.equal(result.response.status, 200, 'duplicate chunk upload is acknowledged idempotently');
  assert.equal(result.data.alreadyReceived, true);
  result = await request(`/api/v1/transfers/${transfer.id}/items/${transfer.items[1].id}/chunks/0`, {
    method: 'PUT', token: aliceToken, raw: bytes, headers: { 'content-type': 'application/octet-stream', 'content-length': String(bytes.length) },
  });
  assert.equal(result.response.status, 201);
  const progress = await request(`/api/v1/transfers/${transfer.id}/progress`, { token: bobToken });
  assert.equal(progress.data.receivedBytes, bytes.length * 2);
  result = await request(`/api/v1/transfers/${transfer.id}/complete`, { method: 'POST', token: aliceToken });
  assert.equal(result.data.transfer.status, 'Completed');
  result = await request('/api/v1/transfers/create', {
    method: 'POST', token: aliceToken,
    body: { receiverId: bob.id, items: [{ fileName: 'second.bin', size: bytes.length, mimeType: 'text/plain', sha256: fileHash }] },
  });
  assert.equal(result.data.transfer.items[0].duplicateDetected, true, 'identical digest from an earlier completed transfer is flagged');
  const senderDownload = await request(`/api/v1/transfers/${transfer.id}/items/${transfer.items[0].id}/download`, { token: aliceToken });
  assert.equal(senderDownload.response.status, 403, 'the sender cannot use the receiver download route');
  for (const item of transfer.items) {
    const downloaded = await request(`/api/v1/transfers/${transfer.id}/items/${item.id}/download`, { token: bobToken });
    assert.equal(downloaded.response.status, 200);
    assert.deepEqual(downloaded.data, bytes);
    assert.equal(crypto.createHash('sha256').update(downloaded.data).digest('hex'), fileHash);
  }

  const directTransfer = await request('/api/v1/transfers/create', {
    method: 'POST', token: aliceToken,
    body: { receiverId: bob.id, items: [{ fileName: 'direct.bin', size: bytes.length, mimeType: 'text/plain', sha256: fileHash }] },
  });
  const direct = directTransfer.data.transfer;
  const directByReceiver = await request(`/api/v1/transfers/${direct.id}/direct`, {
    method: 'POST', token: bobToken, body: { host: '192.168.1.10', port: 52317, token: 'a'.repeat(64) },
  });
  assert.equal(directByReceiver.response.status, 403, 'only the sender registers a direct endpoint');
  await request(`/api/v1/transfers/${direct.id}/accept`, { method: 'POST', token: bobToken });
  const directRegister = await request(`/api/v1/transfers/${direct.id}/direct`, {
    method: 'POST', token: aliceToken, body: { host: '192.168.1.10', port: 52317, token: 'a'.repeat(64) },
  });
  assert.equal(directRegister.response.status, 200);
  const directView = await request(`/api/v1/transfers/${direct.id}`, { token: bobToken });
  assert.equal(directView.data.transfer.directInfo.host, '192.168.1.10');
  assert.equal(directView.data.transfer.directInfo.token, 'a'.repeat(64), 'the receiver receives the capability token over TLS');
  const senderView = await request(`/api/v1/transfers/${direct.id}`, { token: aliceToken });
  assert.equal(senderView.data.transfer.directInfo.token, undefined, 'the sender already knows the token; it is not echoed back');

  const senderCompleteDirect = await request(`/api/v1/transfers/${direct.id}/complete-direct`, { method: 'POST', token: aliceToken });
  assert.equal(senderCompleteDirect.response.status, 409, 'only the receiver completes a direct transfer');
  const completedDirect = await request(`/api/v1/transfers/${direct.id}/complete-direct`, { method: 'POST', token: bobToken });
  assert.equal(completedDirect.response.status, 200);
  assert.equal(completedDirect.data.transfer.status, 'Completed');
  assert.equal(completedDirect.data.transfer.transport, 'Direct Wi-Fi', 'the completed transfer reports the transport that actually moved the bytes');

  const fallbackTransfer = await request('/api/v1/transfers/create', {
    method: 'POST', token: aliceToken,
    body: { receiverId: bob.id, items: [{ fileName: 'fallback.bin', size: bytes.length, mimeType: 'text/plain', sha256: fileHash }] },
  });
  const fallback = fallbackTransfer.data.transfer;
  await request(`/api/v1/transfers/${fallback.id}/accept`, { method: 'POST', token: bobToken });
  const senderFallback = await request(`/api/v1/transfers/${fallback.id}/fallback-relay`, { method: 'POST', token: aliceToken });
  assert.equal(senderFallback.response.status, 409, 'only the receiver requests the relay fallback');
  const receiverFallback = await request(`/api/v1/transfers/${fallback.id}/fallback-relay`, { method: 'POST', token: bobToken });
  assert.equal(receiverFallback.response.status, 200);
  const progressAfterFallback = await request(`/api/v1/transfers/${fallback.id}/progress`, { token: bobToken });
  assert.equal(progressAfterFallback.data.relayRequested, true);
  const failed = await request(`/api/v1/transfers/${fallback.id}/fail`, { method: 'POST', token: aliceToken, body: { reason: 'direct_channel_unreachable' } });
  assert.equal(failed.response.status, 200);
  assert.equal(failed.data.transfer.status, 'Failed');

  result = await request(`/api/v1/transfers/${transfer.id}`, { token: 'not-a-valid-token' });
  assert.equal(result.response.status, 401);
  result = await request('/api/v1/auth/logout', { method: 'POST', token: aliceToken });
  assert.equal(result.response.status, 200);
  result = await request('/api/v1/users/me', { token: aliceToken });
  assert.equal(result.response.status, 401);
});
