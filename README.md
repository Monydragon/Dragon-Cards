# Dragon Cards

Dragon Cards is a KNI-powered desktop card game prototype targeting .NET 10 and C# 14.

Current production version: `0.3.0`.

## Current V1 Shape

- Single-player vs deterministic rule-based AI, local hotseat PvP, and direct TCP host/join multiplayer.
- KNI Desktop OpenGL host in `src/DragonCards.Desktop`.
- Framework-free rules engine in `src/DragonCards.Core`.
- Framework-free short-code LAN discovery, invite-code contracts, client-hosted lobby protocol, and direct TCP transport in `src/DragonCards.Networking`.
- 163 passing xUnit test cases in `tests/DragonCards.Tests` as of 2026-07-12, including presentation timing/settings migration, all-mode launch/legality and mode-specific victory coverage, persistent Energy sources, SQLite profile migration/data controls, deterministic seeds, profile-sync contracts, quest, deck-code, matchmaking-contract, profile/deck persistence, collection discovery, LAN/direct-lobby, and aspect-fit coverage.
- A 496-card library across Core, Ancient Awakening, Elemental Ascension, Primal Clash, eight always-available Basic Energy cards, and eight field-only Energy Source tokens, plus eight mono-element starter decks in `data/`.
- Local profile repository at `%AppData%\DragonCards\profiles\`: an always-first Profile Picker supports create, select, rename, and permanently delete with confirmation. Every profile isolates progression, tutorials, inventory, rules, active deck, and unlimited named custom decks; device display/audio/accessibility settings remain shared.
- SQLite/EF Core profile storage is available through an explicit Profile Data migration. JSON stays active until the player previews and confirms migration; after `dragoncards.db` is created, SQLite is the sole active store (no dual writes). The same workspace exposes visible local data, checksummed JSON export/import-as-copy, backup/verified restore, audit history, and versioned deterministic seed previews. The staged rollout and schema are documented in [`docs/profile-persistence-rollout.md`](docs/profile-persistence-rollout.md).
- Casual sandbox rules unlock all cards and unlimited deck building while disabling progression rewards; Easy through Insane preserve inventory progression.
- Resizable/fullscreen desktop UI with mouse, keyboard, and controller input paths.
- The Dragon's Roost main menu uses an original blue-dragon hero composition, shows a real featured card from the active deck as its deck avatar, and lets players cycle their intended mode before opening its setup.
- Six selectable modes: Dragon Duel, Starter Clash, Dragon Avatar, Sealed Gauntlet, Sandbox Lab, and Tutorial Trials. Every mode supports profile progression except Sandbox Lab, which keeps all content unlocked and rewards disabled.
- Twelve screen states: Profile Picker, Player Creation, Main Menu, Mode Select, Multiplayer, Tutorials, Options, Deck Builder, Store, Pack Opening, Match, and Match Result.
- Shared immediate-mode UI actions, focus/navigation, cached text layout, 44-pixel minimum interactive hit targets, scroll states, draggable scrollbars, held input repeat, and origin-aware Back behavior.
- Options screen with persisted display/audio/gameplay/accessibility settings in AppData, including separate Animation Speed and Message Duration selectors with a live pacing preview plus Reduced Motion; looping BGM and event/UI SFX are loaded and played by the desktop audio service.
- Dragon Duel, Starter Clash, and Sandbox Lab use reusable elemental Energy sources: Basic Energy, free Add Energy, sacrifices, and effects create visible field cards. Each owner Ready refreshes sources and restores usable energy to at least its source total; unspent energy is retained. One zero-cost Basic Energy remains playable from hand each turn alongside the separate free Add Energy action.
- Every starter carries 12 matching Basic Energy cards; Basic Energy ignores the three-copy limit, is always available for legality, and is deliberately absent from collection/store/booster content.
- Main-phase sacrifice from hand, units, or supports: sacrificed cards move to discard and grant at least 1 energy, rounded up from half their printed total cost, to their first element.
- Desktop match flow auto-resolves Ready/Draw bookkeeping into Main decisions.
- Main menu options include a Quest Board. Local daily/weekly quests automatically reward eligible standard matches, while Sandbox and Tutorials do not advance quest progress.
- Multiplayer supports Local Hotseat plus client-hosted LAN lobbies: Copy LAN Code shares a five-character, case-insensitive, ambiguity-free code, while Copy This PC Code enables two profiles on one machine without discovery; Join Lobby accepts Ctrl+V or Paste Code. Adapter-aware LAN discovery resolves the host interface that answered the request, then direct TCP compatibility handshakes (with stale-peer recovery and a 10-second join timeout), roster/error/cancel states, explicit host start, and synchronized commands take over. Internet queueing is explicitly not configured; `IMatchmakingClient` and revisioned `IProfileSyncClient` are provider-neutral seams for future authenticated REST/WebSocket services. LAN peers receive temporary match data only, never profile databases or player progression.
- The 492-entry Store uses Packs, Starter Decks, and Singles tabs, active-tab search, Singles element/rarity/set filters, result counts, empty states, per-tab selection/scroll memory, quantity purchase/open actions, pack opening, and duplicate conversion.
- Deck Builder uses a virtualized six-column scrolling grid with element, type, ownership, rarity, and set filters; name/cost/rarity/owned-copy sorting; distinct/copy/set-aware collection totals; clear sandbox labeling; a profile-owned Deck Library; and clipboard `DCD1-` deck-code import/export with checksum, legality, and missing-ownership preview. Imports never grant cards.
- Six guided tutorials cover first-turn flow, playing cards, adding energy, sacrifice, blocking, and card effects; each awards 250 Coins once per profile.
- Victory/defeat result screens show progression eligibility, XP, level-ups, Coins, and booster rewards.
- Clickable compact energy counters, Add Energy picker, drag-and-drop match flow, full-zone replacement, target choice, and sacrifice actions.
- Playable cards highlight, unavailable cards dim, illegal drops show clear feedback, and the right-side match rail shows card inspection, actions, abilities, a scrollable compact log, and an expanded Match History overlay.
- Hands larger than nine cards use a horizontal viewport while preserving real hand indexes for selection and drag/drop.
- Support and Unit lanes are laid out to visibly hold 5 cards each.
- Data-driven portrait cards with full element names, colored numeric cost badges, element styling, rules text, source-aware inspection, tags, and optional visual metadata.
- Core action results feed a testable `PresentationDirector` with anticipation, motion, settle, and independent reading phases; length-aware captions; per-action grouping; parallel/coalesced secondary feedback; explicit whole-group skip; eased arced travel; activation-timed SFX; instance-safe live-card suppression; and separate user-selected animation/message pacing. AI actions resolve one decision at a time after their readable presentation feedback completes.
- Reduced Motion replaces travel, pulses, stagger, and motion blocking with a static source/target highlight while retaining the full caption reading time, timeline, and audio meaning.
- 46 deterministic render captures for UI review live in `artifacts/render-captures/`.

## Build And Run

```powershell
dotnet build DragonCards.slnx
dotnet test DragonCards.slnx
dotnet run --project src\DragonCards.Desktop\DragonCards.Desktop.csproj
```

## Production Releases

The production publisher follows the versioned-folder pattern used by QuickCube and the self-contained desktop settings used by Phantasy Dungeon. Version metadata lives in `Directory.Build.props`, and [CHANGELOG.md](CHANGELOG.md) records shipped changes.

Rebuild the current Windows release exactly:

```powershell
.\scripts\publish-releases.ps1 -Version 0.2.0 -RuntimeIdentifiers win-x64
```

Without `-Version`, the script increments the patch version before publishing. Each requested runtime is published to its own platform/runtime folder and gets a matching zip:

```powershell
.\scripts\publish-releases.ps1 -Version 0.2.0 -RuntimeIdentifiers win-x64,linux-x64
```

The verified `0.2.0` local-profile packages are available in:

- `artifacts\releases\v0.2.0\Windows\win-x64\`
- `artifacts\releases\v0.2.0\Linux\linux-x64\`
- `artifacts\releases\v0.2.0\DragonCards-Windows-win-x64-v0.2.0.zip`
- `artifacts\releases\v0.2.0\DragonCards-Linux-linux-x64-v0.2.0.zip`

Packages are self-contained, single-file hosts with untrimmed managed dependencies, copied game content/data, and required SDL2/OpenAL native files. The release folder is intentionally ignored by Git so it remains ready to copy or distribute without becoming source history.

For LAN testing, extract/copy the platform folder on each device, run the executable, host on one device, copy/paste the five-character LAN code on the other, and allow Dragon Cards on the Windows Private network if prompted. For two profiles on the same device, use the host's This PC Code instead. Clipboard actions use the native Windows clipboard on Windows and SDL clipboard support on Linux Mint/macOS. Discovery uses UDP `47287`; the match lobby uses TCP `47288`.

## Render Captures

```powershell
dotnet run --project src\DragonCards.Desktop\DragonCards.Desktop.csproj -- --capture-screens --capture-dir artifacts\render-captures
```

Notable outputs include:

- `artifacts/render-captures/main-menu.png`
- `artifacts/render-captures/player-creation.png`
- `artifacts/render-captures/profile-picker-empty.png`
- `artifacts/render-captures/profile-picker-multiple.png`
- `artifacts/render-captures/profile-delete-confirmation.png`
- `artifacts/render-captures/mode-select.png`
- `artifacts/render-captures/multiplayer.png`
- `artifacts/render-captures/multiplayer-host-waiting.png`
- `artifacts/render-captures/multiplayer-join-lobby.png`
- `artifacts/render-captures/tutorials-menu.png`
- `artifacts/render-captures/quest-board.png`
- `artifacts/render-captures/profile-data-json.png`
- `artifacts/render-captures/options.png`
- `artifacts/render-captures/deck-builder.png`
- `artifacts/render-captures/store.png`
- `artifacts/render-captures/pack-opening.png`
- `artifacts/render-captures/match.png`
- `artifacts/render-captures/energy-source-conversion.png`
- `artifacts/render-captures/result-screen.png`
- `artifacts/render-captures/single-player-match.png`
- `artifacts/render-captures/hover-zoom.png`
- `artifacts/render-captures/block-choice.png`
- `artifacts/render-captures/animation-showcase.png`
- `artifacts/render-captures/deck-builder-scroll.png`
- `artifacts/render-captures/deck-library.png`
- `artifacts/render-captures/collection-filter-sort.png`
- `artifacts/render-captures/store-singles.png`
- `artifacts/render-captures/store-filter-empty.png`
- `artifacts/render-captures/pack-opening-overflow.png`
- `artifacts/render-captures/options-reduced-motion.png`
- `artifacts/render-captures/long-hand.png`
- `artifacts/render-captures/match-history.png`
- `artifacts/render-captures/animation-reduced-motion.png`

The capture command currently produces 46 PNGs. Automated aspect-fit calculations pass at 1280x720, 1440x900, 1600x900, and 1920x1080. Hands-on mouse, keyboard, physical-controller, rendered-layout, drag/drop, focus/scrollbar, Back-precedence, and audible A/V timing checks at those sizes remain deferred. A two-app LAN lobby, firewall/port validation, and remote Internet testing also remain deferred. Five-character codes discover only same-LAN hosts; remote Internet play needs a relay/rendezvous service or a reachable address/port through the legacy direct-invite path.

## Living Documents

- Prompt source: `C:\Projects\Documentation\Games\Dragon Cards\Dragon Cards LLM Prompt Source.md`
- Game design document: `C:\Projects\Documentation\Games\Dragon Cards\Dragon Cards Game Design Document.md`

## Customizing Cards

Edit JSON under `data/`:

- `data/game-modes/dragon-duel.json` defines phases, elements, deck rules, zone limits, energy rules, and damage limit.
- The three card-data files define four logical sets: `starter-cards.json` contains the 120-card Core pool plus 40 Ancient Awakening cards, while `elemental-ascension-cards.json` and `primal-clash-cards.json` contain 160 cards each.
- `data/decks/starter-decks.json` defines starter decks.

Cards can use built-in keywords (`Cantrip`, `Refresh`, `Strike`), activated abilities, sacrifice-for-energy, target choices, tags, visual metadata, and named C# hooks registered in `DefaultEffectHookRegistry`. Current hooks include ramp, draw, damage, recovery, energy conversion/refund, chosen target exhaust/ready, and stronger finisher/ramp variants. Costs use element keys plus optional `Generic`, and render as colored numeric badges with full element names.
