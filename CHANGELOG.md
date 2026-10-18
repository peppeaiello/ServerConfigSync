# Changelog

## 0.1.1

- Mods that sync themselves are recognized better: Jötunn admin-only entries (the check never matched before), and mods with their own sync code named like ServerSync/ConfigSync.
- The player's own value is read from their config file, so it is restored correctly even when another sync system had already changed it in memory.
- The in-game message only says that server settings were applied. The guess about mods that may only read settings at startup goes to the log; `ShowStartupWarning` (off by default) shows it in game too.

## 0.1.0

- First test release: the server's BepInEx config values for any mod are applied in memory on players' games while connected, and restored when they disconnect.
- Whitelist/blacklist of mods, per-entry exclusions, keybinds kept local, secrets never sent, mods that sync themselves skipped.
- Live updates when a config file changes on the server.
- Optional requirement for players to have the mod.
