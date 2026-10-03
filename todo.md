# HoshinoTransfer — Development Tracker

Status reflects code that exists and has passed its checks; planned work is not marked done merely because a scaffold exists.

## Milestone checklist

- [x] Architecture — monorepo layout and production endpoint contract
- [ ] Backend — API runtime and route coverage (health/auth are implemented first; verify later domains individually)
- [ ] Database — SQLite schema/migrations and PostgreSQL migration adapter
- [ ] Authentication — register/login/logout/refresh and expiry
- [ ] Friend system — requests, accept/reject/remove/block
- [ ] Chat — message history and realtime delivery
- [ ] Transfer engine — streamed chunk relay, resume, cancellation, SHA-256
- [ ] Wi-Fi — direct local-network transport
- [ ] P2P — ICE/STUN/TURN negotiation and relay fallback
- [ ] Lightning — native device capability investigation and transport
- [ ] Windows UI — native WPF shell, authentication, navigation
- [ ] iOS UI — native SwiftUI shell and authentication
- [ ] GitHub Actions — unsigned iOS IPA artifact for LiveContainer
- [ ] IPA build — verify on macOS GitHub runner
- [ ] Security — authorization, validation, rate limits, safe file handling
- [ ] Tests — unit/integration/self-test coverage
- [ ] Performance — stream large files, async UI/network, bounded resource use
- [ ] Documentation — setup, deployment, LiveContainer, troubleshooting

## Milestones

- [x] M1 — repository layout, architecture notes, README and this tracker
- [ ] M2 — backend runtime, schema and health check (build/test required)
- [ ] M3 — account/session API (build/test required)
- [ ] M4 — Windows WPF shell and authentication
- [ ] M5 — iOS SwiftUI shell and authentication
- [ ] M6 — device management and pairing
- [ ] M7 — friend system
- [ ] M8 — realtime chat
- [ ] M9 — chunked transfer engine
- [ ] M10 — direct Wi-Fi transport
- [ ] M11 — P2P transport and explicit relay fallback
- [ ] M12 — Lightning transport investigation/implementation
- [ ] M13 — chat attachments
- [ ] M14 — unsigned IPA workflow and artifact verification
- [ ] M15 — security hardening
- [ ] M16 — performance optimization
- [ ] M17 — full integration and acceptance flow

## Implemented pieces (not end-to-end milestones)

- Node.js built-in API routes exist for health, account register/login/logout/refresh, profile, device registration, friend requests and blocking, 1-to-1 chat history/send, authenticated SSE events, transfer request/accept/decline/cancel/pause/resume, bounded streamed chunks, receiver-only download, SHA-256 verification, expiry cleanup, strict default CORS and an OpenAPI 3 JSON reference.
- Windows WPF shell, navigation, login/register client calls, friend/device/chat/transfer pages and client-side streaming/hash code are present. Latest expanded WPF edits were not rebuilt; see final session notes.
- iOS SwiftUI authentication and navigation shell, Files picker entry point, account/service settings and unsigned IPA GitHub workflow are present. iOS build/artifact has not been run on macOS.
- Verified API test: a real local HTTP child process exercised health, registration/login, refresh rotation, friend acceptance, device registration, chat over SSE, real relay chunk upload, SHA-256 verified download, sender/receiver authorization and logout. Test uses an isolated in-memory database; file payloads use a temporary directory.

## Known open limitations

- Database is SQLite only; persistent file-backed startup must be re-verified on a normal server host, explicit numbered migrations are not implemented, and PostgreSQL is not wired.
- Client refresh-token renewal/persistent secure token storage is not connected. iOS login state is memory-only.
- iOS file selection is UI-only; no native iOS transfer sender/receiver, iOS device pairing UI, realtime chat client, friend UI, notifications or background transfer support is implemented.
- Windows does not yet persist pause/resume offsets or provide a real source-file retry path; chat attachments are not connected.
- No direct local Wi-Fi, Wi-Fi Direct, ICE/STUN/TURN P2P, Lightning cable transfer or automatic transport fallback. Actual wired file relay is labelled `Server Relay`.
- Unsigned IPA package and LiveContainer import have not been verified on a macOS runner/device. There is no Git repository/remote configured in this workspace, so no Actions run was triggered.

## Verification notes — 2026-10-03

- Backend syntax checks: PASS. Real local HTTP integration test: PASS (1 suite) using test-only in-memory SQLite; covers health, auth/refresh rotation/logout, friend acceptance, device registration, SSE chat, streamed Server Relay upload/download, SHA-256 and download authorization.
- WPF initial shell build: PASS (0 warnings/errors); initial publish also completed. Later WPF API/UI additions were followed by a build attempt that exposed missing `Stream` and generic command declarations; both were patched. The subsequent build invocation was blocked by the environment security policy, so the latest WPF source is **not certified** and the existing `dist/windows` publish is an earlier build.
- iOS/Xcode/unsigned IPA workflow: not run; this host is Windows and no GitHub repository/remote is configured.
- LiveContainer, two-device pairing, iOS transfer, direct transports and end-to-end acceptance flow: not tested/not complete.

## Completion rule

A milestone is checked only after the relevant code builds and its tests pass. Platform-dependent work that cannot be verified here must remain open and be stated plainly in the completion report.
