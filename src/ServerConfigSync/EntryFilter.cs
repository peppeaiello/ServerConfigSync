using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ServerConfigSync
{
    /// <summary>Decides, on the server, which plugins and entries are sent.</summary>
    internal static class EntryFilter
    {
        private static readonly Regex SecretName = new Regex(
            @"webhook|token|passw|pwd|secret|api[\s_\-]?key|auth|credential|private[\s_\-]?key|url|\buri\b|endpoint|connection[\s_\-]?string",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex SecretValue = new Regex(
            @"^\s*[a-z][a-z0-9+.\-]*://|discord(app)?\.com/api/webhooks",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex KeybindName = new Regex(
            @"hotkey|keybind|key\s*bind|shortcut|keycode|modifier\s*key",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Dictionary<Assembly, bool> ServerSyncCache = new Dictionary<Assembly, bool>();

        public static bool IsPluginSelected(string guid)
        {
            switch (Plugin.Mode.Value)
            {
                case SyncMode.Whitelist:
                    return SplitList(Plugin.PluginList.Value).Contains(guid);
                case SyncMode.Blacklist:
                    return !SplitList(Plugin.PluginList.Value).Contains(guid);
                default:
                    return true;
            }
        }

        public static bool IsKeybind(ConfigEntryBase entry)
        {
            return entry.SettingType == typeof(KeyboardShortcut)
                   || entry.SettingType == typeof(KeyCode)
                   || KeybindName.IsMatch(entry.Definition.Key);
        }

        public static bool LooksSecret(ConfigEntryBase entry, string serializedValue)
        {
            return SecretName.IsMatch(entry.Definition.Key)
                   || SecretName.IsMatch(entry.Definition.Section)
                   || (!string.IsNullOrEmpty(serializedValue) && SecretValue.IsMatch(serializedValue));
        }

        private static readonly Regex OwnSyncTypeName = new Regex(@"ServerSync|ConfigSync",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// True if the plugin syncs its own settings: it embeds ServerSync and registered config entries
        /// with it, or it has its own sync code (a type named like *ServerSync* or *ConfigSync*).
        /// Mods with sync code named otherwise can't be recognized; admins can blacklist them.
        /// </summary>
        public static bool SyncsItself(PluginInfo info, out string how)
        {
            var assembly = info.Instance.GetType().Assembly;
            if (UsesServerSync(assembly))
            {
                how = "ServerSync";
                return true;
            }

            how = null;
            try
            {
                foreach (var type in AccessTools.GetTypesFromAssembly(assembly))
                {
                    if (type.Namespace == "ServerSync" || !OwnSyncTypeName.IsMatch(type.Name)) continue;
                    how = "own sync code";
                    return true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"Could not inspect {info.Metadata.GUID}: {e.Message}");
            }
            return false;
        }

        private static bool UsesServerSync(Assembly assembly)
        {
            if (ServerSyncCache.TryGetValue(assembly, out bool cached)) return cached;

            bool result = false;
            Type configSync = assembly.GetType("ServerSync.ConfigSync", false);
            if (configSync != null)
            {
                // If the internals cannot be read, assume the mod syncs itself.
                result = true;
                try
                {
                    var syncsField = AccessTools.Field(configSync, "configSyncs");
                    var configsField = AccessTools.Field(configSync, "allConfigs");
                    if (syncsField?.GetValue(null) is IEnumerable syncs && configsField != null)
                    {
                        result = false;
                        foreach (var sync in syncs)
                        {
                            if (configsField.GetValue(sync) is IEnumerable configs && configs.GetEnumerator().MoveNext())
                            {
                                result = true;
                                break;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogDebug($"Could not inspect ServerSync in {assembly.GetName().Name}: {e.Message}");
                }
            }

            ServerSyncCache[assembly] = result;
            return result;
        }

        /// <summary>
        /// True for entries that Jotunn already syncs: tagged with Jotunn's own ConfigurationManagerAttributes
        /// (not a copy of that class in another mod) with IsAdminOnly set.
        /// </summary>
        public static bool IsJotunnAdminOnly(ConfigEntryBase entry)
        {
            var tags = entry.Description?.Tags;
            if (tags == null) return false;
            foreach (var tag in tags)
            {
                if (tag == null) continue;
                var type = tag.GetType();
                if (type.Name != "ConfigurationManagerAttributes" || type.Assembly.GetName().Name != "Jotunn") continue;
                object value = AccessTools.Property(type, "IsAdminOnly")?.GetValue(tag, null)
                               ?? AccessTools.Field(type, "IsAdminOnly")?.GetValue(tag);
                if (value is bool b && b) return true;
            }
            return false;
        }

        public static HashSet<string> SplitList(string value)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(value)) return set;
            foreach (var part in value.Split(','))
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0) set.Add(trimmed);
            }
            return set;
        }
    }

    /// <summary>
    /// A list of "GUID", "GUID:Section" or "GUID:Section:Key" patterns, '*' as wildcard.
    /// </summary>
    internal sealed class EntryPatterns
    {
        private readonly List<Regex[]> _patterns = new List<Regex[]>();
        private readonly string _source;

        private EntryPatterns(string source)
        {
            _source = source ?? "";
            foreach (var item in EntryFilter.SplitList(_source))
            {
                var parts = item.Split(new[] { ':' }, 3);
                var regexes = new Regex[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    string pattern = "^" + Regex.Escape(parts[i].Trim()).Replace("\\*", ".*") + "$";
                    regexes[i] = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                _patterns.Add(regexes);
            }
        }

        private static readonly Dictionary<string, EntryPatterns> Cache = new Dictionary<string, EntryPatterns>();

        public static EntryPatterns Get(string source)
        {
            source = source ?? "";
            if (!Cache.TryGetValue(source, out var patterns))
            {
                patterns = new EntryPatterns(source);
                Cache[source] = patterns;
            }
            return patterns;
        }

        public bool Matches(string guid, string section, string key)
        {
            foreach (var p in _patterns)
            {
                if (!p[0].IsMatch(guid)) continue;
                if (p.Length > 1 && !p[1].IsMatch(section)) continue;
                if (p.Length > 2 && !p[2].IsMatch(key)) continue;
                return true;
            }
            return false;
        }
    }

    internal static class SnapshotBuilder
    {
        public static Snapshot Build()
        {
            var snapshot = new Snapshot { ServerVersion = Plugin.Version };
            var excluded = EntryPatterns.Get(Plugin.ExcludedEntries.Value);
            var allowedSecrets = EntryPatterns.Get(Plugin.AllowedSecretEntries.Value);
            var selfSyncing = new List<string>();
            var withheldSecrets = new List<string>();
            int keybinds = 0, jotunn = 0, excludedCount = 0;

            foreach (var info in Chainloader.PluginInfos.Values)
            {
                if (info?.Instance == null) continue;
                string guid = info.Metadata.GUID;
                if (guid == Plugin.Guid || !EntryFilter.IsPluginSelected(guid)) continue;

                ConfigFile config = info.Instance.Config;
                if (config == null || Plugin.IsCoreConfig(config)) continue;

                if (Plugin.SkipSelfSyncingMods.Value && EntryFilter.SyncsItself(info, out string how))
                {
                    selfSyncing.Add($"{info.Metadata.Name} ({how})");
                    continue;
                }

                var list = new List<SyncedValue>();
                foreach (var pair in config)
                {
                    var entry = pair.Value;
                    string section = entry.Definition.Section;
                    string key = entry.Definition.Key;

                    if (excluded.Matches(guid, section, key)) { excludedCount++; continue; }
                    if (!Plugin.SyncKeybinds.Value && EntryFilter.IsKeybind(entry)) { keybinds++; continue; }
                    if (Plugin.SkipSelfSyncingMods.Value && EntryFilter.IsJotunnAdminOnly(entry)) { jotunn++; continue; }

                    string value;
                    try
                    {
                        value = entry.GetSerializedValue();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"Cannot read {guid} [{section}] {key}: {e.Message}");
                        continue;
                    }

                    if (EntryFilter.LooksSecret(entry, value) && !allowedSecrets.Matches(guid, section, key))
                    {
                        withheldSecrets.Add($"{guid}:{section}:{key}");
                        continue;
                    }

                    list.Add(new SyncedValue { Section = section, Key = key, Value = value });
                }

                if (list.Count > 0) snapshot.Plugins[guid] = list;
            }

            var summary = snapshot.Summary;
            summary.Add($"Prepared {snapshot.EntryCount} entries from {snapshot.Plugins.Count} mods for connecting players " +
                        $"(mode {Plugin.Mode.Value}; skipped {keybinds} keybinds, {jotunn} Jotunn admin-only, {excludedCount} excluded).");
            if (selfSyncing.Count > 0)
                summary.Add($"Skipped mods that sync their own settings: {string.Join(", ", selfSyncing.ToArray())}");
            if (withheldSecrets.Count > 0)
                summary.Add($"Withheld entries that look like secrets (add them to AllowedSecretEntries to send them): {string.Join(", ", withheldSecrets.ToArray())}");
            if (Plugin.VerboseLogging.Value)
            {
                foreach (var plugin in snapshot.Plugins)
                    summary.Add($"  {plugin.Key}: {plugin.Value.Count} entries");
            }

            return snapshot;
        }
    }
}
