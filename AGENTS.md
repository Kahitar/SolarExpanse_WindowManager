# AGENTS.md

This repository is the base window manager mod for Solar Expanse. It provides shared notification-area buttons, draggable button grouping, resizable game-styled windows, button status indicators, and ESC handling for UI mods.

## Project Rules

- Resolve game and BepInEx references through `SOLAR_EXPANSE_ROOT`.
- Use `mise run build` from this directory for normal local builds.
- Use `mise run package` from this directory for release packaging.
- The built DLL is `SolarExpanse.WindowManager.dll`.
- The plugin GUID is `com.mod.solarexpanse.windowmanager`.
- The public namespace for dependent mods is `SolarExpanse.WindowManager`.
- This window manager owns the shared `NotificationManager.Awake` UI injection point for window-manager-based UI mods. Dependent mods should register windows with `SolarExpanseWindowManager.RegisterWindow(...)` instead of patching `NotificationManager.Awake` for their own button/window injection.

## Agent Integration Documentation

Agents building or migrating another mod to use this base window manager must read and follow:

- `docs/agent-window-manager-integration.md`

That document is specifically written for AI agents implementing new UI mods based on this window manager. It includes the public API, expected integration steps, migration notes, and an agent-focused compatibility changelog.

Always keep `docs/agent-window-manager-integration.md` up to date when changing public APIs, lifecycle behavior, styling behavior, layout assumptions, build/reference requirements, or compatibility expectations for dependent mods.
If a window manager change would require dependent mods to change code, add an entry to the compatibility changelog in that document in the same change.

## Implementation Notes

- Keep public API changes conservative. Existing dependent mods may compile against `UiWindowRegistration`, `UiWindowContext`, `IUiWindowHandle`, and `UiButtonStatus`. If a change would require an API change, or the API change would make the API much cleaner, ask the user for confirmation before making the change.
- Do not duplicate per-mod button creation, draggable button groups, resizers, or pause-screen ESC suppression in dependent mods. Those behaviors belong here.
- Preserve the game-native visual direction: dark beveled notification-side buttons, compact icons, dark translucent window backgrounds, and subdued status text.
- Prefer generated sprites or game-discovered sprites over external image assets unless a release explicitly packages those assets.
- Always update README.md when the feature set changes, or the readme would be outdated otherwise.
