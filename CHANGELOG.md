# Changelog

## [1.4.0] - 2026-06-04

### Fixed

- Transient invalid canvas layouts during live resizing no longer clear the user's saved dock placement.
- User-positioned docks now preserve their offset from the nearest horizontal and vertical viewport edges across resizing.
- Late rendered-viewport changes now trigger restoration from the saved edge-relative dock position.

## [1.3.0] - 2026-06-04

### Fixed

- The shared button dock now clamps its actual transformed bounds against the rendered canvas viewport immediately before rendering.
- Added automatic dock-position recovery for invalid or fully off-screen positions caused by live game-window resizing.
- Automatic resize clamping no longer overwrites the user's saved relative dock position, so enlarging the game window restores the dock to its prior relative position.

## [1.2.0] - 2026-06-04

### Changed

- Status dots now use the same anti-aliased TextMeshPro `●` glyph style as tracker row indicators.
- Status-dot blinking now runs on an always-active component attached directly to each visible button dot.

### Fixed

- Fixed critical status dots remaining continuously bright instead of blinking.

## [1.1.0] - 2026-06-04

### Added

- Added optional `UiButtonStatus.BlinkOffColor` support for explicit two-color status-dot blinking.
- Open framework windows now move with the shared button dock while the user drags it.

### Fixed

- Repeated identical status updates no longer reset an active status-dot blink cycle.

## [1.0.0] - 2026-06-03

### Added

- Initial shared UI framework mod for Solar Expanse BepInEx UI mods.
