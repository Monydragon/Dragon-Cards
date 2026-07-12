# Changelog

Dragon Cards follows `major.minor.patch` versioning. `Directory.Build.props` is the release-version source of truth; each shipped user-requested change increments the patch version unless a feature or breaking release warrants a minor or major bump.

## [Unreleased]

- Fixed Dragon Avatar matches stalling when the AI queued a Ready effect with no exhausted friendly Unit, displayed each mode's real damage limit on the board, and added a regression proving Avatar victory at 10 damage.
- Enabled profile progression in Dragon Duel, Starter Clash, Dragon Avatar, Sealed Gauntlet, and Tutorial Trials while keeping Sandbox Lab reward-free; fixed Avatar deck generation and added selected-mode legality/launch coverage for every match mode.
- Redesigned the main menu as the Dragon's Roost: original blue-dragon key art frames the entry screen with its eye, wing, and tail; players now select a mode before entering setup, and the visible deck avatar is chosen from the actual active deck rather than a fixed placeholder card.
- Added the completed local SQLite profile-storage rollout: an EF Core migration-backed store with normalized profile, collection, deck, quest, tutorial, last-active, revision, audit, and deterministic-seed-run data. JSON remains active until the player opts in; SQLite is then the sole active store with no dual writes.
- Added a Main Menu Profile Data workspace for visible profile/collection/deck/quest/audit data, previewed JSON-to-SQLite migration, checksummed JSON export/import-as-copy, verified backup/restore with a safety backup, and deterministic seed preview/application.
- Added an explicit JSON-to-SQLite importer that preserves profile IDs, verifies a SHA-256 JSON backup before database creation, imports into an empty target only, and compares imported profile/deck/collection/quest data before succeeding.
- Added provider-neutral revisioned profile-sync contracts for a future authenticated hosted service. The shipped unavailable client keeps player data local, and LAN/direct matches never receive profile databases or progression data.
- Reworked standard-mode energy into reusable, visible persistent sources. Basic Energy, free Add Energy, sacrifices, and energy effects now create field cards that exhaust for payment, refresh at their owner's Ready, retain unspent energy, and support source-selected conversions. Avatar, Sealed, and Tutorials retain their previous energy economy.
- Added the 0.3 foundation pass: eight zero-cost Basic Energy cards, one hand-play per turn into a persistent Energy row, a separate free Add Energy action, and rebuilt 50-card starter curves with 12 matching Basic Energy cards each.
- Added local UTC daily/weekly Quest Board progression with automatic Coin/standard-pack rewards, result summaries, and profile version 4 migration. Sandbox and tutorial modes do not advance quests.
- Added checksummed canonical `DCD1-` deck-code export/import with clipboard controls, game-legality validation, missing-ownership preview, and no card grants.
- Added provider-neutral matchmaking queue/status/assignment/reconnect contracts plus an explicit unavailable implementation and future REST/WebSocket architecture document. LAN/direct multiplayer remains the working path.
- Made direct multiplayer lobby joins recover cleanly from stale or rejected peers and fail with a clear timeout if a reachable host never completes its handshake. The host remains open for the next valid guest.
- Added LAN and same-PC invite guidance, including a direct This PC Code that bypasses discovery and firewall rules for two local app instances.
- Added a persisted Interaction Pace option (Cinematic, Natural, Quick, Fast). Presentation playback now follows that setting, and AI turns advance one decision at a time after their feedback has played.

## [0.2.0] - 2026-07-10

- Replaced the single root save with an always-first local Profile Picker and an atomic `%AppData%\DragonCards\profiles\` repository. Profiles support unique 1–18 character names, select/rename/permanent delete confirmation, last-active focus, recovery messaging for malformed files, and automatic safe 0.1.x migration.
- Made progression, tutorials, inventory, rules, active deck, and deck storage profile-owned while preserving shared display/audio/accessibility settings. Renaming a profile updates the LAN multiplayer display name.
- Added unlimited named profile deck libraries with starter read-only references, save-as-copy from starters, create, load, rename, duplicate, delete, active-deck restore, and invalid-deck fallback.
- Expanded Deck Builder discovery with ownership, element, type, rarity, and set filtering; name/cost/rarity/owned-copy sorting; distinct/copy/set-aware counts; and explicit sandbox usability feedback.
- Added 0.2.0 profile/deck/collection persistence coverage and five deterministic profile/deck/collection captures. Published self-contained Windows `win-x64` and Linux Mint `linux-x64` packages.

## [0.1.2] - 2026-07-10

- Added SDL-backed Copy/Paste Code support for Linux Mint and macOS while retaining the native Windows clipboard path.
- Published and smoke-tested the cross-platform clipboard-compatible Windows and Linux multiplayer-test packages.

## [0.1.1] - 2026-07-10

- Fixed LAN discovery to broadcast on every active adapter and connect to the host interface that actually answers the discovery request.
- Added an end-to-end short-code host/discover/join test, clipboard paste with `Ctrl+V`, a Paste Code UI action, and Private-network UDP/TCP firewall guidance.
- Published and smoke-tested self-contained Windows and Linux `x64` multiplayer-test packages.

## [0.1.0] - 2026-07-10

- Established the first production release workflow for self-contained desktop packages.
- Added Local Hotseat and client-hosted LAN multiplayer lobbies with a five-character Copy Code invite, LAN discovery, lobby roster, explicit host start, and legacy direct-invite compatibility.
- Shipped the UI, scrolling, accessibility, presentation, audio, progression, Store, Deck Builder, tutorial, and match-history baseline recorded in the living documents.
