using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ServerConfigSync
{
    public enum SyncMode
    {
        All,
        Whitelist,
        Blacklist
    }

    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "peppeaiello.ServerConfigSync";
        public const string Name = "ServerConfigSync";
        public const string Version = "0.1.2";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        // Server settings
        internal static ConfigEntry<bool> ServerEnabled;
        internal static ConfigEntry<SyncMode> Mode;
        internal static ConfigEntry<string> PluginList;
        internal static ConfigEntry<string> ExcludedEntries;
        internal static ConfigEntry<bool> SyncKeybinds;
        internal static ConfigEntry<bool> SkipSelfSyncingMods;
        internal static ConfigEntry<string> AllowedSecretEntries;
        internal static ConfigEntry<bool> RequireClientMod;
        internal static ConfigEntry<bool> LiveUpdates;

        // Client settings
        internal static ConfigEntry<bool> AcceptServerConfig;
        internal static ConfigEntry<bool> ShowStartupWarning;
        internal static ConfigEntry<bool> ListAffectedMods;

        // Debug
        internal static ConfigEntry<bool> VerboseLogging;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            const string server = "1 - Server";
            ServerEnabled = Config.Bind(server, "Enabled", true,
                "Send this server's mod settings to connecting players. Only used when this game instance is the server.");
            Mode = Config.Bind(server, "SyncMode", SyncMode.All,
                "All: sync every installed mod. Whitelist: only the mods in PluginList. Blacklist: every mod except those in PluginList.");
            PluginList = Config.Bind(server, "PluginList", "",
                "Comma-separated plugin GUIDs used by the Whitelist and Blacklist modes (example: ishid4.mods.betterarchery, torokal.afkmanager). The GUID of a mod is written at the top of its .cfg file.");
            ExcludedEntries = Config.Bind(server, "ExcludedEntries", "",
                "Comma-separated entries that are never synced. Format: GUID, GUID:Section or GUID:Section:Key. '*' works as a wildcard (example: ishid4.mods.betterarchery:Bow Zoom:*).");
            SyncKeybinds = Config.Bind(server, "SyncKeybinds", false,
                "If false, keyboard shortcuts and hotkeys stay as each player set them.");
            SkipSelfSyncingMods = Config.Bind(server, "SkipSelfSyncingMods", true,
                "Skip mods that already sync their own settings (with ServerSync, or with their own code when it can be recognized) and Jotunn entries marked admin-only. Mods whose sync code cannot be recognized are synced too; blacklist them if you prefer.");
            AllowedSecretEntries = Config.Bind(server, "AllowedSecretEntries", "",
                "Entries that look like secrets (webhooks, tokens, passwords, URLs...) are never sent. List here, with the same format as ExcludedEntries, the ones you really want to send.");
            RequireClientMod = Config.Bind(server, "RequireClientMod", false,
                "If true, players without ServerConfigSync cannot join.");
            LiveUpdates = Config.Bind(server, "LiveUpdates", true,
                "When a .cfg file in BepInEx/config changes while the server is running, reload it and send the new values to connected players.");

            const string client = "2 - Client";
            AcceptServerConfig = Config.Bind(client, "AcceptServerConfig", true,
                "Apply the settings sent by the server while connected. Your own config files are never modified and your values come back when you disconnect.");
            ShowStartupWarning = Config.Bind(client, "ShowStartupWarning", false,
                "Also show in game which mods might only read their settings at startup. This is a guess and often wrong (many mods read the value every time and work fine), so by default it only goes to the log.");
            ListAffectedMods = Config.Bind(client, "ListAffectedMods", true,
                "Include the names of the affected mods in the in-game message.");

            VerboseLogging = Config.Bind("3 - Debug", "VerboseLogging", false,
                "Log every synced entry.");

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{Name} {Version} loaded");
        }

        private void Update()
        {
            ServerSide.Update();
            ClientSide.Update();
        }

        private void OnDestroy()
        {
            ClientSide.RestoreAll("plugin unloaded");
            ServerSide.StopWatcher();
            _harmony?.UnpatchSelf();
        }

        internal static bool IsCoreConfig(ConfigFile file)
        {
            try
            {
                string core = Paths.BepInExConfigPath;
                if (string.IsNullOrEmpty(core)) return false;
                return string.Equals(Path.GetFullPath(file.ConfigFilePath), Path.GetFullPath(core),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }

        internal static void Verbose(string message)
        {
            if (VerboseLogging.Value) Log.LogInfo(message);
        }
    }
}
