# Sims3Online icon system v1

## Concept

**Two players. One shared simulation.**

- The cyan and lime faceted prisms represent two players or authority domains.
- Their interlock represents a shared native Sims 3 session rather than two disconnected replays.
- The centre diamond represents synchronized authoritative state.
- The orbit and endpoint nodes represent the online session linking both players.
- The dark violet/navy field subtly connects the mark to Obsidian Network.

The artwork is intentionally original. It evokes a life-simulation game through faceted geometry without copying EA's exact plumbob, The Sims wordmark, or official launcher artwork.

## Files

- `source/sims-online-icon.svg` — full vector master.
- `source/sims-online-icon-small.svg` — simplified vector for 16–48 px rendering.
- `source/sims-online-glyph-*.svg` — transparent colour, white, and dark glyphs.
- `png/primary/` — optimized PNGs from 16 to 1024 px.
- `png/glyph-*/` — transparent glyph exports.
- `windows/sims-online.ico` — multi-resolution Windows icon.
- `web/` — favicon, Apple touch, and Android Chrome names.
- `linux/hicolor/` — freedesktop hicolor app-icon directory structure.
- `preview/` — presentation board and size checks.

## Recommended usage

- Launcher / desktop app: use `windows/sims-online.ico` on Windows or the matching `linux/hicolor` PNG on Linux.
- Installer and package art: use the 256, 512, or 1024 px primary PNG.
- Website: copy the files from `web/`.
- Dark navigation or tray UI: use the white monochrome glyph.
- Light UI: use the dark monochrome glyph.

Do not add text inside the square icon. Use a separate wordmark beside it when the name is needed.

## Core palette

- Obsidian violet: `#17152D`
- Midnight navy: `#0A1830`
- Player cyan: `#2ADCE3`
- Shared mint: `#56E8C2`
- Player lime: `#A8FA63`

## License

Artwork © 2026 Obsidian Network, released under the repository's [MIT License](../LICENSE).

## Trademark note

Sims3Online is an unofficial fan/mod project and is not related to EA's 2002 game *The Sims Online*. Keep an
unofficial-project notice wherever the project is distributed, and do not imply endorsement by Electronic Arts.
