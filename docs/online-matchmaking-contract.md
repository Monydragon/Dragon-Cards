# Matchmaking client contract

Dragon Cards 0.3 continues to use direct LAN play for real multiplayer. Internet queueing is intentionally not configured in the shipped client.

`IMatchmakingClient` isolates the future online transport behind four operations:

1. `QueueAsync` submits authenticated player, mode, deck identity/code, region, and client-version data.
2. `GetStatusAsync` polls or mirrors WebSocket queue state until an assignment is available.
3. `ReconnectAsync` exchanges a match id and resume token for a new short-lived connection assignment.
4. `CancelAsync` removes a pending ticket.

The production service should expose authenticated REST endpoints for queue, status, cancellation, and reconnect, plus a match WebSocket endpoint returned only in a short-lived `MatchAssignment`. The service must validate deck codes, ownership, client version, and queue eligibility before assignment. Authoritative match simulation, account identity, ranking, cloud saves, and deployment are intentionally outside 0.3.

## Player-data boundary

Client-hosted LAN matches exchange the selected deck snapshot and temporary match commands only. They never exchange a SQLite database, profile export, collection, quests, audit data, or local backup. A future authenticated profile-sync service is separately represented by `IProfileSyncClient`: it accepts revisioned mutation envelopes and cursors, not raw database files. This keeps profile ownership local today and allows a hosted service to add authentication and conflict resolution later without changing the LAN protocol.
