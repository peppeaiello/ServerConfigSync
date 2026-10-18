# Changelog

## 0.1.3

- The plugin GUID is now `Manke.ServerConfigSync`, so the settings file is `BepInEx/config/Manke.ServerConfigSync.cfg`. Settings in the old `peppeaiello.ServerConfigSync.cfg` are carried over automatically the first time.

## 0.1.2

- Documentation only: clearer README and changelog. No code changes.

## 0.1.1

- Better detection of mods that already sync their own settings, including Jötunn admin-only entries.
- Players' own values are always restored correctly when they disconnect, even when another mod's sync had changed them first.
- The in-game message only says that server settings were applied. Mods that may only read settings at startup are listed in the log; `ShowStartupWarning` shows them in game too.

## 0.1.0

- First test release: the server's BepInEx config values for any mod are applied in memory on players' games while connected, and restored when they disconnect.
- Whitelist/blacklist of mods, per-entry exclusions, keybinds kept local, secrets never sent, mods that sync themselves skipped.
- Live updates when a config file changes on the server.
- Optional requirement for players to have the mod.
