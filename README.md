# Dragon Cards

Dragon Cards is a KNI-powered desktop card game prototype targeting .NET 10 and C# 14.

Current production version: `0.1.2`.

## Current V1 Shape

- Single-player vs deterministic rule-based AI, local hotseat PvP, and direct TCP host/join multiplayer.
- KNI Desktop OpenGL host in `src/DragonCards.Desktop`.
- Framework-free rules engine in `src/DragonCards.Core`.
- Framework-free short-code LAN discovery, invite-code contracts, client-hosted lobby protocol, and direct TCP transport in `src/DragonCards.Networking`.
- 122 passing xUnit test cases in `tests/DragonCards.Tests` as of 2026-07-10, including parameterized aspect-fit checks at all four supported window sizes.
- A 480-card library across Core (120), Ancient Awakening (40), Elemental Ascension (160), and Primal Clash (160), plus eight mono-element starter decks in `data/`.
- Local player profile creation with name, difficulty, playstyle, starter choice, level 1-100 progression, Coins, owned-card inventory, and boosters.
- Casual sandbox rules unlock all cards and unlimited deck building while disabling progression rewards; Easy through Insane preserve inventory progression.
- Resizable/fullscreen desktop UI with mouse, keyboard, and controller input paths.
- Six selectable modes: Dragon Duel, Starter Clash, Dragon Avatar, Sealed Gauntlet, Sandbox Lab, and Tutorial Trials.
- Eleven screen states: Player Creation, Main Menu, Mode Select, Multiplayer, Tutorials, Options, Deck Builder, Store, Pack Opening, Match, and Match Result.
- Shared immediate-mode UI actions, focus/navigation, cached text layout, 44-pixel minimum interactive hit targets, scroll states, draggable scrollbars, held input repeat, and origin-aware Back behavior.
- Options screen with persisted display/audio/gameplay/accessibility settings in AppData, including backward-compatible Reduced Motion; looping BGM and event/UI SFX are loaded and played by the desktop audio service.
- Persistent elemental energy pools capped at 10 per element.
- Starter ramp balance: one free chosen energy per turn plus common ramp supports that help starter decks reach 4+ energy by turn 3.
- Main-phase sacrifice from hand, units, or supports: sacrificed cards move to discard and grant at least 1 energy, rounded up from half their printed total cost, to their first element.
- Desktop match flow auto-resolves Ready/Draw bookkeeping into Main decisions.
- Main menu options are `Play Modes`, `Multiplayer`, `Deck Builder`, `Store / Packs`, `Tutorials`, `Options`, `New Game`, and `Exit`.
- Multiplayer supports Local Hotseat plus client-hosted LAN lobbies: Copy Code shares a five-character, case-insensitive, ambiguity-free code; Join Lobby accepts Ctrl+V or Paste Code; adapter-aware LAN discovery resolves the host interface that answered the request, then the direct TCP compatibility handshake, roster/error/cancel states, explicit host start, and synchronized commands take over. Legacy `DC2-` and `DC1-` invites still decode as fallbacks.
- The 492-entry Store uses Packs, Starter Decks, and Singles tabs, active-tab search, Singles element/rarity/set filters, result counts, empty states, per-tab selection/scroll memory, quantity purchase/open actions, pack opening, and duplicate conversion.
- Deck Builder uses a virtualized six-column scrolling grid with element/type filters, card inspection, ownership, validation, and controller-reachable assistant/save actions.
- Six guided tutorials cover first-turn flow, playing cards, adding energy, sacrifice, blocking, and card effects; each awards 250 Coins once per profile.
- Victory/defeat result screens show progression eligibility, XP, level-ups, Coins, and booster rewards.
- Clickable compact energy counters, Add Energy picker, drag-and-drop match flow, full-zone replacement, target choice, and sacrifice actions.
- Playable cards highlight, unavailable cards dim, illegal drops show clear feedback, and the right-side match rail shows card inspection, actions, abilities, a scrollable compact log, and an expanded Match History overlay.
- Hands larger than nine cards use a horizontal viewport while preserving real hand indexes for selection and drag/drop.
- Support and Unit lanes are laid out to visibly hold 5 cards each.
- Data-driven portrait cards with full element names, colored numeric cost badges, element styling, rules text, source-aware inspection, tags, and optional visual metadata.
- Core action results feed a testable `PresentationDirector` with exhaustive animation recipes, per-action grouping, parallel/coalesced secondary feedback, leftover-delta handling, whole-group skip, eased opacity/travel, activation-timed SFX, instance-safe live-card suppression, and accurate moving-card endpoints.
- Reduced Motion replaces travel, pulses, stagger, and motion blocking with a brief static source/target highlight and caption while retaining timeline and audio meaning.
- 38 deterministic render captures for UI review live in `artifacts/render-captures/`.

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
.\scripts\publish-releases.ps1 -Version 0.1.2 -RuntimeIdentifiers win-x64
```

Without `-Version`, the script increments the patch version before publishing. Each requested runtime is published to its own platform/runtime folder and gets a matching zip:

```powershell
.\scripts\publish-releases.ps1 -Version 0.1.2 -RuntimeIdentifiers win-x64,linux-x64
```

The verified `0.1.2` multiplayer-test packages are available in:

- `artifacts\releases\v0.1.2\Windows\win-x64\`
- `artifacts\releases\v0.1.2\Linux\linux-x64\`
- `artifacts\releases\v0.1.2\DragonCards-Windows-win-x64-v0.1.2.zip`
- `artifacts\releases\v0.1.2\DragonCards-Linux-linux-x64-v0.1.2.zip`

Packages are self-contained, single-file hosts with untrimmed managed dependencies, copied game content/data, and required SDL2/OpenAL native files. The release folder is intentionally ignored by Git so it remains ready to copy or distribute without becoming source history.

For LAN testing, extract/copy the platform folder on each device, run the executable, host on one device, copy/paste the five-character code on the other, and allow Dragon Cards on the Windows Private network if prompted. Clipboard actions use the native Windows clipboard on Windows and SDL clipboard support on Linux Mint/macOS. Discovery uses UDP `47287`; the match lobby uses TCP `47288`.

## Render Captures

```powershell
dotnet run --project src\DragonCards.Desktop\DragonCards.Desktop.csproj -- --capture-screens --capture-dir artifacts\render-captures
```

Notable outputs include:

- `artifacts/render-captures/main-menu.png`
- `artifacts/render-captures/player-creation.png`
- `artifacts/render-captures/mode-select.png`
- `artifacts/render-captures/multiplayer.png`
- `artifacts/render-captures/multiplayer-host-waiting.png`
- `artifacts/render-captures/multiplayer-join-lobby.png`
- `artifacts/render-captures/tutorials-menu.png`
- `artifacts/render-captures/options.png`
- `artifacts/render-captures/deck-builder.png`
- `artifacts/render-captures/store.png`
- `artifacts/render-captures/pack-opening.png`
- `artifacts/render-captures/match.png`
- `artifacts/render-captures/result-screen.png`
- `artifacts/render-captures/single-player-match.png`
- `artifacts/render-captures/hover-zoom.png`
- `artifacts/render-captures/block-choice.png`
- `artifacts/render-captures/animation-showcase.png`
- `artifacts/render-captures/deck-builder-scroll.png`
- `artifacts/render-captures/store-singles.png`
- `artifacts/render-captures/store-filter-empty.png`
- `artifacts/render-captures/pack-opening-overflow.png`
- `artifacts/render-captures/options-reduced-motion.png`
- `artifacts/render-captures/long-hand.png`
- `artifacts/render-captures/match-history.png`
- `artifacts/render-captures/animation-reduced-motion.png`

The capture command currently produces 38 PNGs. Automated aspect-fit calculations pass at 1280x720, 1440x900, 1600x900, and 1920x1080. Hands-on mouse, keyboard, physical-controller, rendered-layout, drag/drop, focus/scrollbar, Back-precedence, and audible A/V timing checks at those sizes remain deferred. A two-app LAN lobby, firewall/port validation, and remote Internet testing also remain deferred. Five-character codes discover only same-LAN hosts; remote Internet play needs a relay/rendezvous service or a reachable address/port through the legacy direct-invite path.

## Living Documents

- Prompt source: `C:\Projects\Documentation\Games\Dragon Cards\Dragon Cards LLM Prompt Source.md`
- Game design document: `C:\Projects\Documentation\Games\Dragon Cards\Dragon Cards Game Design Document.md`

## Customizing Cards

Edit JSON under `data/`:

- `data/game-modes/dragon-duel.json` defines phases, elements, deck rules, zone limits, energy rules, and damage limit.
- The three card-data files define four logical sets: `starter-cards.json` contains the 120-card Core pool plus 40 Ancient Awakening cards, while `elemental-ascension-cards.json` and `primal-clash-cards.json` contain 160 cards each.
- `data/decks/starter-decks.json` defines starter decks.

Cards can use built-in keywords (`Cantrip`, `Refresh`, `Strike`), activated abilities, sacrifice-for-energy, target choices, tags, visual metadata, and named C# hooks registered in `DefaultEffectHookRegistry`. Current hooks include ramp, draw, damage, recovery, energy conversion/refund, chosen target exhaust/ready, and stronger finisher/ramp variants. Costs use element keys plus optional `Generic`, and render as colored numeric badges with full element names.
