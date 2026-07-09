# Dragon Cards

Dragon Cards is a KNI-powered desktop card game prototype targeting .NET 10 and C# 14.

## Current V1 Shape

- Single-player vs deterministic rule-based AI, local hotseat PvP, and direct TCP host/join multiplayer.
- KNI Desktop OpenGL host in `src/DragonCards.Desktop`.
- Framework-free rules engine in `src/DragonCards.Core`.
- Framework-free invite-code contracts in `src/DragonCards.Networking`.
- xUnit coverage in `tests/DragonCards.Tests`.
- Data-driven Dragon Duel rules, a 160-card starter library, and eight mono-element starter decks in `data/`.
- Local player profile creation with name, difficulty, playstyle, starter choice, level 1-100 progression, Coins, owned-card inventory, and boosters.
- Casual sandbox rules unlock all cards and unlimited deck building while disabling progression rewards; Easy through Insane preserve inventory progression.
- Resizable/fullscreen desktop UI with mouse, keyboard, and controller input paths.
- Options screen with persisted display/audio/gameplay settings in AppData.
- Persistent elemental energy pools capped at 10 per element.
- Starter ramp balance: one free chosen energy per turn plus common ramp supports that help starter decks reach 4+ energy by turn 3.
- Main-phase sacrifice from hand, units, or supports: sacrificed cards move to discard and grant at least 1 energy, rounded up from half their printed total cost, to their first element.
- Desktop match flow auto-resolves Ready/Draw bookkeeping into Main decisions.
- Main menu options are `Start Game (Single Player)`, `Multiplayer`, `Deck Builder`, `Store / Packs`, `Options`, `New Game`, and `Exit`.
- Multiplayer supports Local Hotseat plus Direct Host/Join LAN play with invite codes and shared rules configuration.
- Store/Packs screen supports 5-card boosters, starter deck purchases, pack opening, duplicate conversion, and owned-card deck building.
- Victory/defeat result screens show progression eligibility, XP, level-ups, Coins, and booster rewards.
- Clickable compact energy counters, Add Energy picker, drag-and-drop match flow, full-zone replacement, target choice, and sacrifice actions.
- Playable cards highlight, unavailable cards dim, illegal drops show clear feedback, and the right-side match rail shows card inspection, actions, abilities, and the match log.
- Support and Unit lanes are laid out to visibly hold 5 cards each.
- Data-driven portrait cards with full element names, colored numeric cost badges, element styling, rules text, source-aware inspection, tags, and optional visual metadata.
- Core action results emit structured match events that the desktop presentation queue turns into card travel, glows, pulses, impacts, phase banners, and energy/damage beats.
- Render captures for UI review in `artifacts/render-captures/`.

## Build And Run

```powershell
dotnet build DragonCards.slnx
dotnet test DragonCards.slnx
dotnet run --project src\DragonCards.Desktop\DragonCards.Desktop.csproj
```

## Render Captures

```powershell
dotnet run --project src\DragonCards.Desktop\DragonCards.Desktop.csproj -- --capture-screens --capture-dir artifacts\render-captures
```

This writes:

- `artifacts/render-captures/main-menu.png`
- `artifacts/render-captures/player-creation.png`
- `artifacts/render-captures/multiplayer.png`
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

## Living Documents

- Prompt source: `C:\Projects\Documentation\Games\Dragon Cards\Dragon Cards LLM Prompt Source.md`
- Game design document: `C:\Projects\Documentation\Games\Dragon Cards\Dragon Cards Game Design Document.md`

## Customizing Cards

Edit JSON under `data/`:

- `data/game-modes/dragon-duel.json` defines phases, elements, deck rules, zone limits, energy rules, and damage limit.
- `data/cards/starter-cards.json` defines the starter card library.
- `data/decks/starter-decks.json` defines starter decks.

Cards can use built-in keywords (`Cantrip`, `Refresh`, `Strike`), activated abilities, sacrifice-for-energy, target choices, tags, visual metadata, and named C# hooks registered in `DefaultEffectHookRegistry`. Current hooks include ramp, draw, damage, recovery, energy conversion/refund, chosen target exhaust/ready, and stronger finisher/ramp variants. Costs use element keys plus optional `Generic`, and render as colored numeric badges with full element names.
