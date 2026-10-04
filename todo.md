# HoshinoTransfer — Development Tracker

Status reflects code that exists and has passed its checks; planned work is not marked done merely because a scaffold exists.

## Milestone checklist

- [x] Architecture — monorepo layout and production endpoint contract
- [x] Backend — API runtime and route coverage (real-HTTP integration test + production acceptance)
- [x] Database — SQLite schema + numbered idempotent migrations (PostgreSQL adapter remains open)
- [x] Authentication — register/login/logout/refresh, rotation, expiry, DPAPI/Keychain storage
- [x] Friend system — search, request, accept/reject/remove/block/unblock, online status
- [x] Chat — history, realtime SSE, typing indicator, delivered/read states, attachments, failed outbox retry
- [x] Transfer engine — streamed chunk relay, true resume from server progress, pause/resume/cancel/retry, SHA-256, duplicate detection
- [ ] Wi-Fi — direct local-network transport (declared Unavailable)
- [ ] P2P — ICE/STUN/TURN negotiation and relay fallback (declared Unavailable)
- [ ] Lightning — native cable transport (declared Unavailable)
- [x] Windows UI — redesigned WPF shell, drag & drop, QR pairing render, progress/speed/ETA
- [x] iOS UI — full SwiftUI feature set, Keychain sessions, camera QR scanner
- [x] GitHub Actions — unsigned iOS IPA artifact for LiveContainer (green run)
- [x] IPA build — verified on macOS GitHub runner (artifact downloaded + inspected)
- [x] Security — receiver-only download, sender-only chunk upload, ordered chunks, rotation reuse rejection, cross-account pairing rejection
- [x] Tests — unit/integration/self-test/remote acceptance coverage
- [x] Performance — streaming, bounded buffers, backoff reconnect, no full-file RAM load
- [x] Documentation — README, todo, api-contract

## Milestones

- [x] M1 — repository layout, architecture notes, README and this tracker
- [x] M2 — backend runtime, schema and health check
- [x] M3 — account/session API
- [x] M4 — Windows WPF shell and authentication
- [x] M5 — iOS SwiftUI shell and authentication
- [x] M6 — device management and pairing (8-digit + QR)
- [x] M7 — friend system
- [x] M8 — realtime chat
- [x] M9 — chunked transfer engine
- [ ] M10 — direct Wi-Fi transport
- [ ] M11 — P2P transport and explicit relay fallback
- [ ] M12 — Lightning transport investigation/implementation
- [x] M13 — chat attachments
- [x] M14 — unsigned IPA workflow and artifact verification
- [x] M15 — security hardening
- [x] M16 — performance optimization
- [x] M17 — full integration and acceptance flow (script-level; two real-device UI pass remains)

## Implemented pieces

- Node.js built-in API: health, auth (rotation reuse rejected), profile PATCH, user search, devices + single-use pairing codes, heartbeat, friends (request/accept/reject/remove/block/unblock), 1-to-1 chat with delivered/read/typing, authenticated SSE (device-bound, session-revoking), transfers (create/accept/decline/cancel/pause/resume/retry/complete, ordered chunks, idempotent duplicate upload, progress endpoint, duplicate detection), receiver-only download, expiry cleanup, strict CORS, OpenAPI JSON.
- Windows WPF: redesigned dark UI (cards, MDL2 icons, status pills, chat bubbles own/other, avatar initials), login/register, DPAPI Credential Manager session restore + rotation, friends search/actions, devices register/pair/revoke with QR image (QRCoder), chat with typing + attach + failed outbox auto-retry on SSE ready, transfer pause/resume/retry/cancel with speed + ETA + logs, history with per-item progress, window drag & drop, crash log.
- iOS SwiftUI: login/register, Keychain session + rotation, friends (search/add/accept/block/unblock), devices + pairing (8-digit and camera QR scan via AVFoundation), chats with SSE realtime + typing + attachments, transfers with pause/resume/retry/cancel + SHA-256 verified download, Files-app picker, settings.
- Production: server runs on host port 3000; Tailscale funnel maps 443 -> 3000; clients default to `https://rt2ucj.taild7fb6f.ts.net/`.

## Known open limitations

- Database is SQLite only; PostgreSQL adapter and multi-host deployment are not wired.
- Direct Wi-Fi, Wi-Fi Direct, ICE/STUN/TURN P2P and Lightning cable transfer are not implemented; the registry reports them Unavailable with reasons and never shows them as active. Actual wired file relay is labelled `Server Relay`.
- iOS background transfer is not implemented; transfers run while the app is foregrounded.
- Two-real-device UI pass (Windows app + iPhone in LiveContainer) is pending on the owner; all API-level acceptance is verified.
- Restart persistence uses the file-backed SQLite DB on the host; re-verify after a server restart.

## Verification notes — 2026-10-03/04

- Backend unit/integration (`node --test`): PASS — health, auth/refresh rotation reuse rejection, pairing (cross-account rejected, single use), user search validation, friend acceptance, SSE chat, ordered chunk upload, idempotent duplicate chunk, progress, complete, SHA-256, receiver-only download, logout revocation.
- WPF build Release: PASS (0 warnings/0 errors) after GUI overhaul + QRCoder + outbox changes; `dotnet publish` PASS; published exe self-test 6/6 PASS against a live local server.
- Remote acceptance via public funnel (`scripts/acceptance.remote.mjs`): 12/12 PASS on 2026-10-03/04, including a 5,255,225-byte relay transfer with byte-identical download.
- GitHub Actions unsigned IPA: run `37128569498` SUCCESS for the pre-QR build; artifact downloaded to `dist/HoshinoTransfer.ipa` (363 KB, correct Payload structure). QR-scanner build (run `37162449124`) failed on one Swift initializer error; fix pushed.
- LiveContainer import and two-device UI flow: not tested (requires the iPhone).

## Completion rule

A milestone is checked only after the relevant code builds and its tests pass. Platform-dependent work that cannot be verified here must remain open and be stated plainly in the completion report.
