# Changelog

Dragon Cards follows `major.minor.patch` versioning. `Directory.Build.props` is the release-version source of truth; each shipped user-requested change increments the patch version unless a feature or breaking release warrants a minor or major bump.

## [Unreleased]

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
