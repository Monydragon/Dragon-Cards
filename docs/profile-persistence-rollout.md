# Profile Persistence Rollout

## Step 1: SQLite Foundation

`DragonCards.Persistence` provides a local-first SQLite persistence layer. A fresh installation continues to use `%AppData%\DragonCards\profiles\` JSON until the player explicitly completes the in-game migration. Once `%AppData%\DragonCards\profiles\dragoncards.db` exists, SQLite is the only active profile store; the client does not dual-write JSON and SQLite.

The SQLite store owns a single device-local `dragoncards.db` file. It uses EF Core migrations, foreign keys, WAL journaling, a stable profile revision, and an append-only audit table. A new database is created and migrated only when `SqliteProfileStore.InitializeAsync` is explicitly called.

### Tables

- `Profiles`: stable profile ID, display name, progression, rules, active deck, timestamps, and revision.
- `CardCopies`, `PackInventory`, `StarterDeckOwnership`: collection state.
- `Decks`, `DeckCards`: user-created decks and their card counts.
- `QuestStates`, `QuestEntries`, `TutorialCompletions`: progression state.
- `AppSettings`: device-local metadata such as the last active profile.
- `ProfileEvents`: user-visible audit events for create, profile save, deck changes, and seed application.
- `ProfileSeedRuns`: seed value, versioned algorithm, scenario, deterministic input summary, and applied UTC timestamp.

All timestamps are stored as UTC Unix milliseconds. This avoids relying on SQLite ordering/comparison behavior for `DateTimeOffset` values.

### Contract Boundary

`IProfileStore` exposes asynchronous profile, deck, selection, deletion, revision, and audit operations. `SqliteProfileStore` creates a fresh EF `DbContext` per operation, so it is suitable for a future desktop background save queue or an online synchronization adapter. It does not allow a LAN match peer to access another user's database.

The desktop deliberately publishes on the managed .NET runtime rather than NativeAOT. EF Core NativeAOT/compiled-query support is still experimental, while this store uses normal EF model construction and dynamic LINQ queries. Persistence JSON documents use source-generated metadata, so migration, export/import, rules, and audit records do not depend on reflection serialization.

### Migrations

The project-local EF tool is pinned in `.config/dotnet-tools.json`.

```powershell
dotnet tool restore
dotnet tool run dotnet-ef migrations add MeaningfulName --project src\DragonCards.Persistence --startup-project src\DragonCards.Persistence --output-dir Migrations
dotnet tool run dotnet-ef database update --project src\DragonCards.Persistence --startup-project src\DragonCards.Persistence
```

Set `DRAGON_CARDS_DB` before running the command to choose the design-time database path.

## Deferred Steps

## Step 2: Verified JSON Importer

`JsonProfileMigrationService` reads the current JSON profile index and profile/deck files without changing them. It also supports the older root `profile.json` layout without first running the legacy JSON migration.

Before SQLite is created, migration:

1. Refuses an existing target database; it never merges data.
2. Copies every relevant JSON profile/deck/index file to `%AppData%\DragonCards\json-backups\`.
3. Computes SHA-256 for every source and copied file and writes `migration-manifest.json` only after every hash matches.
4. Imports every profile in one SQLite transaction, preserving profile IDs and last-active selection.
5. Reads SQLite back and compares profile names, progression, collection, packs, quests, tutorials, decks, deck cards, and active profile against the JSON source.

If import or verification fails, only the newly created SQLite database and its WAL sidecars are removed. The verified JSON backup and all source files remain intact.

The importer is never called automatically. In the Profile Data workspace, the player first runs a preview and then confirms **Back Up + Migrate**. On success the running client switches to the SQLite repository, leaving the JSON source and verified backup available for inspection.

## Step 3: Player Data Workspace

The Main Menu **Profile Data** workspace gives the active player visibility and control over their local data:

- The profile identity, progress, rules, collection, decks, quests, audit entries, and latest seed are visible in one screen.
- SQLite profiles export a checksummed, readable JSON snapshot. Import reads `%AppData%\DragonCards\profiles\imports\profile-import.json`, validates it, and always creates a separate profile; it never overwrites or grants cards to the current profile. The workspace can also re-back up the preserved JSON source into `json-backups`.
- SQLite backup produces a timestamped `.db` copy. Restore requires a second confirmation, verifies the backup, and takes a safety backup before replacement.
- **Verify SQLite** runs `PRAGMA integrity_check` and reports the persisted profile/card/deck totals plus the applied EF migrations.
- The deterministic seed preview shows its scenario, seed, and versioned `splitmix64-v1` algorithm before applying it. The exact effective UTC input is recorded so the seed can be reproduced later.

The desktop app uses `IProfileRepository` so JSON and SQLite implementations share the same profile/deck workflow. The selected store is exclusive: JSON until an opt-in migration exists, then SQLite.

## Step 4: Future Hosted Profile Sync Boundary

`DragonCards.Networking.ProfileSyncContracts` defines revisioned profile mutation, cursor, push, pull, conflict, rejection, and authentication-required models. It is deliberately provider-neutral and ships with `UnavailableProfileSyncClient`, which reports that the profile remains local.

For a future authenticated service:

1. The client sends a player-owned mutation envelope containing profile ID, device ID, base/replacement revision, kind, JSON payload, UTC timestamp, and client version.
2. The service authenticates the account and device, validates the envelope, resolves revision conflicts, and returns an acknowledged cursor or a conflict response.
3. The client pulls accepted mutations after its cursor and applies them locally through the repository layer.

LAN/direct matches remain isolated from this flow. A LAN host receives match handshakes, deck snapshots, and deterministic match commands only; it never receives another player's profile database, collection, quests, audit trail, or backups. Hosted match authority, account systems, cloud synchronization, and conflict-resolution policy are intentionally not enabled in 0.3.
