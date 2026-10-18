# ServerConfigSync

Make every player on your Valheim server use **the server's settings for any mod**, even mods that have no server sync of their own.

Many mods read their settings from a BepInEx `.cfg` file on each player's PC. Without a sync system, every player can have different values: stack sizes, arrow speed, crafting rules, and so on. Some mod authors add sync support (ServerSync, Jötunn), many don't. ServerConfigSync does it for all of them, with nothing to change in the mods themselves.

## How it works

- The **server is the source of truth**. When a player joins, the server sends the values from its own BepInEx config files.
- The player's game applies them **in memory only**. The player's `.cfg` files are never overwritten, and their own values come back as soon as they disconnect. Playing on several servers, or single player, is unaffected.
- Mods are notified through the normal BepInEx `SettingChanged` event, so mods that react to setting changes pick up the server's values immediately.
- If the admin edits a `.cfg` file while the server is running, the new values are pushed to connected players (can be turned off).

## Installation

Install it **on the server and on every player's game** (with r2modman, Gale or Thunderstore Mod Manager, or by copying `ServerConfigSync.dll` into `BepInEx/plugins`).

Players without the mod can still join, they just keep their own settings, unless the server sets `RequireClientMod = true`.

## Configuration

The settings are in `BepInEx/config/peppeaiello.ServerConfigSync.cfg`. The server only reads the `Server` section and players only read the `Client` section.

### Server

| Setting | Default | Description |
|---|---|---|
| `Enabled` | `true` | Send the server's settings to players who join. |
| `SyncMode` | `All` | `All`: every mod. `Whitelist`: only the mods in `PluginList`. `Blacklist`: every mod except those in `PluginList`. |
| `PluginList` | *(empty)* | Comma-separated plugin GUIDs for `Whitelist`/`Blacklist`. A mod's GUID is written at the top of its `.cfg` file. |
| `ExcludedEntries` | *(empty)* | Comma-separated entries that are never synced: `GUID`, `GUID:Section` or `GUID:Section:Key`, with `*` as a wildcard. Example: `ishid4.mods.betterarchery:Bow Zoom:*` |
| `SyncKeybinds` | `false` | When off, hotkeys and keyboard shortcuts stay as each player set them. |
| `SkipSelfSyncingMods` | `true` | Skip mods that already sync their settings with ServerSync, and Jötunn entries marked admin-only, so the two systems don't fight. |
| `AllowedSecretEntries` | *(empty)* | Entries that look like secrets are never sent (see below). List here, in the `ExcludedEntries` format, the ones you really want to send. |
| `RequireClientMod` | `false` | Refuse players who don't have ServerConfigSync. |
| `LiveUpdates` | `true` | Reload a `.cfg` file edited while the server runs and push the new values to connected players. |

When the server starts, its log says how many entries from how many mods will be sent, and which ones were skipped and why.

### Client

| Setting | Default | Description |
|---|---|---|
| `AcceptServerConfig` | `true` | Apply the server's settings while connected. |
| `ShowRestartWarning` | `true` | Show a message when settings were applied to mods that may only read them at startup. |
| `ListAffectedMods` | `true` | Name those mods in the message. |

### Debug

| Setting | Default | Description |
|---|---|---|
| `VerboseLogging` | `false` | Log every synced entry (old and new value). |

## What is sent, and what is not

Only **config values** are sent. No files and no code, nothing else from the server.

Never sent:

- `BepInEx.cfg` and ServerConfigSync's own settings.
- **Entries that look like secrets**: a section or key mentioning a webhook, token, password, secret, API key, auth, credential, URL, URI, endpoint or connection string, and any value that is a URL. A Discord webhook in a server-side mod's config never leaves the server. If you really want one of these to be sent, list it in `AllowedSecretEntries`.
- Keybinds, unless `SyncKeybinds` is on.
- Mods and entries the admin excluded.

## Security on the player's side

- Data is accepted only from the server the player is connected to.
- Only **existing entries of mods that are loaded** are changed. Nothing is created.
- Transfers are size-limited (compressed and uncompressed), and malformed data is rejected.
- Nothing is ever written to disk or executed. If a mod saves its config while you're connected, your own values are what get written.
- If you change a synced setting while connected (for example in a config manager), it's saved as your value, and the server's value stays active until you leave.

## Limitations

- **Mods that only read their settings at startup** (for example, ones that set up items or recipes once when the game loads) won't see values that arrive when you join. ServerConfigSync can't detect this with certainty. It warns when a mod with changed values doesn't listen for setting changes, but some of those mods work fine anyway because they read the value every time they use it.
- Only the main config file of each mod (`<GUID>.cfg`) is synced. Extra `.cfg` files a mod creates itself, and YAML/JSON configs, are not.
- Entries a mod creates only later in the game (after you join) are not synced.

## Building from source

Requires the .NET SDK (8 or later) and a Valheim install with BepInEx (game or dedicated server) to compile against.

```bash
./build.sh -p:ValheimDir=/path/to/Valheim
```

This produces `dist/ServerConfigSync-<version>.zip`, a Thunderstore package. Tests that run without the game:

```bash
dotnet run --project tests/ServerConfigSync.Tests -p:ValheimDir=/path/to/Valheim
```

## License

MIT. Source code: https://github.com/peppeaiello/ServerConfigSync
