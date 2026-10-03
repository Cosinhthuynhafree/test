# HoshinoTransfer API contract (v1)

Base URL: `https://rt2ucj.taild7fb6f.ts.net/api/v1/`

Authenticated requests use `Authorization: Bearer <accessToken>`. The SSE event endpoint accepts the bearer header; clients should not place credentials in URLs.

| Method | Route | Purpose |
|---|---|---|
| GET | `/health` | Liveness and API version |
| POST | `/auth/register` | Create account and session |
| POST | `/auth/login` | Create session |
| POST | `/auth/refresh` | Rotate refresh credential |
| POST | `/auth/logout` | Revoke current session |
| GET | `/users/me` | Current profile |
| GET/POST | `/devices` | List/register devices |
| DELETE | `/devices/{id}` | Remove own device |
| GET | `/friends` | Friend/request states |
| POST | `/friends/request` | Request by username |
| POST | `/friends/accept` | Accept an incoming request |
| POST | `/friends/reject` | Reject an incoming request |
| DELETE | `/friends/{id}` | Remove friendship |
| GET | `/chats` | List 1-to-1 chats |
| GET | `/chats/{id}/messages` | Read chat history |
| POST | `/chats/{id}/messages` | Send a message |
| GET | `/events` | Authenticated SSE event stream |
| GET | `/transfers` | List the caller's recent transfers |
| POST | `/transfers/create` | Create file-transfer metadata/session |
| GET | `/transfers/{id}` | Read authorized transfer state |
| POST | `/transfers/{id}/accept` | Receiver accepts |
| POST | `/transfers/{id}/decline` | Receiver declines |
| PUT | `/transfers/{id}/items/{itemId}/chunks/{index}` | Stream one bounded upload chunk |
| POST | `/transfers/{id}/complete` | Assemble chunks and verify SHA-256 |
| GET | `/transfers/{id}/items/{itemId}/download` | Authorized receiver download |
| POST | `/transfers/{id}/cancel` | Cancel transfer |
| POST | `/transfers/{id}/resume` | Resume eligible transfer |

Transfer bytes routed through the server are **server relay**. No endpoint may label a relay transfer as direct Wi-Fi/P2P/Lightning. Event messages are emitted to the authenticated user's live SSE connections; clients can reconnect and then fetch history/state via REST.
