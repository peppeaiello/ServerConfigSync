using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

namespace ServerConfigSync
{
    /// <summary>
    /// Applies server values in memory and guarantees that the player's own values are the
    /// only ones ever written to disk, and the ones restored on disconnect.
    /// </summary>
    internal static class ClientSide
    {
        private sealed class Tracked
        {
            public string Guid;
            public string Local;   // the player's own value (what belongs on disk)
            public string Server;  // the value sent by the server
            public string Applied; // the value the entry actually holds after applying (after clamping)
        }

        private static readonly object Lock = new object();
        private static readonly Dictionary<ConfigEntryBase, Tracked> TrackedEntries = new Dictionary<ConfigEntryBase, Tracked>();
        private static readonly HashSet<ConfigFile> TrackedFiles = new HashSet<ConfigFile>();
        private static readonly Dictionary<Type, FieldInfo> ValueFields = new Dictionary<Type, FieldInfo>();

        // Depth of our own changes on this thread; SettingChanged events raised meanwhile are ours.
        [ThreadStatic] private static int _busy;
        [ThreadStatic] private static HashSet<ConfigFile> _reloading;

        private static ZNetPeer _serverPeer;
        private static ChunkAssembler _assembler;
        private static readonly List<string> PendingMessages = new List<string>();
        private static bool _loggedRefusal;

        // ---- Network -------------------------------------------------------

        public static void RegisterPeer(ZNetPeer peer)
        {
            _serverPeer = peer;
            _assembler = new ChunkAssembler();
            peer.m_rpc.Register<ZPackage>(Protocol.RpcData, OnData);

            var hello = new ZPackage();
            hello.Write(Protocol.Version);
            hello.Write(Plugin.Version);
            peer.m_rpc.Invoke(Protocol.RpcHello, hello);
        }

        private static void OnData(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                // A client only has one peer, the server it connected to. Anything else is ignored.
                if (ZNet.instance == null || ZNet.instance.IsServer() || _serverPeer == null || rpc != _serverPeer.m_rpc)
                {
                    Plugin.Log.LogWarning("Ignored config data that did not come from the connected server.");
                    return;
                }
                if (!Plugin.AcceptServerConfig.Value)
                {
                    if (!_loggedRefusal) Plugin.Log.LogInfo("Server sent its settings, but AcceptServerConfig is off. Ignored.");
                    _loggedRefusal = true;
                    return;
                }

                int protocol = pkg.ReadInt();
                if (protocol != Protocol.Version)
                {
                    Plugin.Log.LogWarning($"Server uses ServerConfigSync protocol {protocol}, this client uses {Protocol.Version}. Update the mod. Nothing applied.");
                    return;
                }
                int transferId = pkg.ReadInt();
                int index = pkg.ReadInt();
                int count = pkg.ReadInt();
                int length = pkg.ReadInt();
                if (length < 0 || length > Protocol.ChunkSize || pkg.ReadInt() != length)
                    throw new InvalidDataException("bad chunk length");
                byte[] data = pkg.ReadByteArray(length);

                byte[] payload = _assembler.Add(transferId, index, count, data);
                if (payload == null) return;

                Apply(Protocol.Deserialize(payload));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Rejected config data from the server: {e.Message}");
            }
        }

        // ---- Apply / restore ----------------------------------------------

        /// <summary>Finds the config file of a loaded plugin. Replaceable for tests.</summary>
        internal static Func<string, ConfigFile> ResolveConfig = guid =>
            Chainloader.PluginInfos.TryGetValue(guid, out var info) ? info?.Instance?.Config : null;

        internal static void Apply(Snapshot snapshot)
        {
            int unknownPlugins = 0, unknownEntries = 0, restored = 0;
            var differing = new Dictionary<string, List<ConfigEntryBase>>();

            lock (Lock)
            {
                _busy++;
                try
                {
                    var desired = new Dictionary<ConfigEntryBase, KeyValuePair<string, string>>();
                    var onDisk = new Dictionary<ConfigFile, Dictionary<ConfigDefinition, string>>();
                    foreach (var plugin in snapshot.Plugins)
                    {
                        string guid = plugin.Key;
                        if (guid == Plugin.Guid) continue;
                        ConfigFile config = ResolveConfig(guid);
                        if (config == null)
                        {
                            unknownPlugins++;
                            continue;
                        }
                        if (Plugin.IsCoreConfig(config)) continue;

                        foreach (var value in plugin.Value)
                        {
                            ConfigDefinition definition;
                            try { definition = new ConfigDefinition(value.Section, value.Key); }
                            catch { unknownEntries++; continue; }

                            // Only entries the plugin itself created are touched.
                            if (!config.ContainsKey(definition)) { unknownEntries++; continue; }
                            desired[config[definition]] = new KeyValuePair<string, string>(guid, value.Value);
                        }
                    }

                    // Entries the server no longer sends go back to the player's value.
                    foreach (var pair in TrackedEntries.ToList())
                    {
                        if (desired.ContainsKey(pair.Key)) continue;
                        SetValue(pair.Key, pair.Value.Local);
                        TrackedEntries.Remove(pair.Key);
                        restored++;
                    }

                    foreach (var pair in desired)
                    {
                        var entry = pair.Key;
                        if (!TrackedEntries.TryGetValue(entry, out var tracked))
                        {
                            tracked = new Tracked { Guid = pair.Value.Key, Local = PlayerValue(entry, onDisk) };
                            TrackedEntries[entry] = tracked;
                        }
                        tracked.Server = pair.Value.Value;
                        SetValue(entry, tracked.Server);
                        tracked.Applied = entry.GetSerializedValue();

                        if (tracked.Applied != tracked.Local)
                        {
                            if (!differing.TryGetValue(tracked.Guid, out var list))
                                differing[tracked.Guid] = list = new List<ConfigEntryBase>();
                            list.Add(entry);
                            Plugin.Verbose($"  {tracked.Guid} [{entry.Definition.Section}] {entry.Definition.Key}: {tracked.Local} -> {tracked.Applied}");
                        }
                    }

                    UpdateFileSubscriptions();
                }
                finally
                {
                    _busy--;
                }
            }

            int differingCount = differing.Values.Sum(l => l.Count);
            Plugin.Log.LogInfo($"Applied server settings (server mod {snapshot.ServerVersion}): {TrackedEntries.Count} entries from {snapshot.Plugins.Count} mods, " +
                               $"{differingCount} differ from your own values" +
                               (restored > 0 ? $", {restored} restored to yours" : "") +
                               (unknownPlugins > 0 ? $", {unknownPlugins} mods not installed here" : "") +
                               (unknownEntries > 0 ? $", {unknownEntries} unknown entries ignored" : "") + ".");

            if (differingCount > 0) QueueMessages(differing, differingCount);
        }

        private static void QueueMessages(Dictionary<string, List<ConfigEntryBase>> differing, int differingCount)
        {
            var maybeStartup = new List<string>();
            foreach (var pair in differing)
            {
                if (!pair.Value.Any(HasListeners)) maybeStartup.Add(PluginName(pair.Key));
            }
            maybeStartup.Sort(StringComparer.OrdinalIgnoreCase);

            if (maybeStartup.Count > 0)
                Plugin.Log.LogInfo($"These mods do not listen for setting changes; if a server value seems to have no effect, they may only read it at startup: {string.Join(", ", maybeStartup.ToArray())}");

            lock (PendingMessages)
            {
                PendingMessages.Clear();
                PendingMessages.Add($"Server settings applied: {differingCount} value(s) in {differing.Count} mod(s)");
                if (Plugin.ShowStartupWarning.Value && maybeStartup.Count > 0)
                {
                    PendingMessages.Add(Plugin.ListAffectedMods.Value
                        ? $"Some mods may only read these settings at startup: {string.Join(", ", maybeStartup.ToArray())}"
                        : $"{maybeStartup.Count} mod(s) may only read these settings at startup (see the log)");
                }
            }
        }

        /// <summary>
        /// The player's own value is the one in their file. The value in memory may already come from the
        /// server through a mod's own sync system, which would then be "restored" and saved as theirs.
        /// </summary>
        private static string PlayerValue(ConfigEntryBase entry, Dictionary<ConfigFile, Dictionary<ConfigDefinition, string>> cache)
        {
            var file = entry.ConfigFile;
            if (!cache.TryGetValue(file, out var values))
            {
                values = ReadFile(file.ConfigFilePath);
                cache[file] = values;
            }
            return values != null && values.TryGetValue(entry.Definition, out var value) ? value : entry.GetSerializedValue();
        }

        // Same parsing as ConfigFile.Reload.
        private static Dictionary<ConfigDefinition, string> ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var values = new Dictionary<ConfigDefinition, string>();
                string section = string.Empty;
                foreach (var rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2);
                        continue;
                    }
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    try { values[new ConfigDefinition(section, parts[0].Trim())] = parts[1].Trim(); }
                    catch (ArgumentException) { }
                }
                return values;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not read {Path.GetFileName(path)}: {e.Message}");
                return null;
            }
        }

        public static void RestoreAll(string reason)
        {
            int count;
            lock (Lock)
            {
                count = TrackedEntries.Count;
                if (count > 0)
                {
                    _busy++;
                    try
                    {
                        foreach (var pair in TrackedEntries)
                        {
                            try { SetValue(pair.Key, pair.Value.Local); }
                            catch (Exception e) { Plugin.Log.LogWarning($"Could not restore {pair.Key.Definition}: {e.Message}"); }
                        }
                        TrackedEntries.Clear();
                        UpdateFileSubscriptions();
                    }
                    finally
                    {
                        _busy--;
                    }
                }
            }
            _serverPeer = null;
            _assembler = null;
            _loggedRefusal = false;
            lock (PendingMessages) PendingMessages.Clear();
            if (count > 0) Plugin.Log.LogInfo($"Restored your own values for {count} entries ({reason}).");
        }

        private static void UpdateFileSubscriptions()
        {
            var needed = new HashSet<ConfigFile>(TrackedEntries.Keys.Select(e => e.ConfigFile));
            foreach (var file in TrackedFiles.Where(f => !needed.Contains(f)).ToList())
            {
                file.SettingChanged -= OnSettingChanged;
                TrackedFiles.Remove(file);
            }
            foreach (var file in needed)
            {
                if (TrackedFiles.Add(file)) file.SettingChanged += OnSettingChanged;
            }
        }

        /// <summary>Sets a value without saving the file. SettingChanged still fires, so mods react.</summary>
        private static void SetValue(ConfigEntryBase entry, string serialized)
        {
            var file = entry.ConfigFile;
            bool save = file.SaveOnConfigSet;
            file.SaveOnConfigSet = false;
            try
            {
                entry.SetSerializedValue(serialized);
            }
            finally
            {
                file.SaveOnConfigSet = save;
            }
        }

        /// <summary>Sets the backing field directly: no events, no save, no clamping.</summary>
        private static bool SetSilently(ConfigEntryBase entry, string serialized)
        {
            var type = entry.GetType();
            if (!ValueFields.TryGetValue(type, out var field))
            {
                field = AccessTools.Field(type, "_typedValue");
                ValueFields[type] = field;
                if (field == null) Plugin.Log.LogWarning($"Unsupported BepInEx version: no value field on {type}.");
            }
            if (field == null) return false;
            field.SetValue(entry, TomlTypeConverter.ConvertToValue(serialized, entry.SettingType));
            return true;
        }

        // A player edit while connected (for example in a config manager) becomes their own value,
        // but the server value stays active until they disconnect.
        private static void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            if (_busy > 0) return;
            var entry = e.ChangedSetting;
            lock (Lock)
            {
                if (!TrackedEntries.TryGetValue(entry, out var tracked)) return;
                string now = entry.GetSerializedValue();
                if (now == tracked.Applied) return;

                tracked.Local = now;
                _busy++;
                try
                {
                    SetValue(entry, tracked.Server);
                    tracked.Applied = entry.GetSerializedValue();
                    if (entry.ConfigFile.SaveOnConfigSet) entry.ConfigFile.Save();
                }
                finally
                {
                    _busy--;
                }
                Plugin.Log.LogInfo($"[{entry.Definition.Section}] {entry.Definition.Key} changed locally to {now}: saved as your value, the server value stays active until you disconnect.");
            }
        }

        // ---- ConfigFile.Save / Reload hooks -------------------------------

        /// <summary>Before Save: put the player's values back so only they reach the disk.</summary>
        public static bool BeforeSave(ConfigFile file, out List<KeyValuePair<ConfigEntryBase, string>> swapped)
        {
            swapped = null;
            lock (Lock)
            {
                if (!TrackedFiles.Contains(file)) return true;
                // Saving while the file is being read back would write half-loaded values; skip it.
                if (_reloading != null && _reloading.Contains(file)) return false;
                swapped = new List<KeyValuePair<ConfigEntryBase, string>>();
                foreach (var pair in TrackedEntries)
                {
                    if (pair.Key.ConfigFile != file) continue;
                    string current = pair.Key.GetSerializedValue();
                    if (current == pair.Value.Local) continue;
                    if (!SetSilently(pair.Key, pair.Value.Local))
                    {
                        AfterSave(swapped);
                        swapped = null;
                        Plugin.Log.LogWarning($"Skipped saving {Path.GetFileName(file.ConfigFilePath)} to protect your own values.");
                        return false;
                    }
                    swapped.Add(new KeyValuePair<ConfigEntryBase, string>(pair.Key, current));
                }
                return true;
            }
        }

        public static void AfterSave(List<KeyValuePair<ConfigEntryBase, string>> swapped)
        {
            if (swapped == null) return;
            lock (Lock)
            {
                foreach (var pair in swapped) SetSilently(pair.Key, pair.Value);
            }
        }

        /// <summary>
        /// Before Reload (usually a mod watching its own file): switch to the player's values so the
        /// file is compared against them, and ignore the change events the reload raises.
        /// </summary>
        public static bool BeforeReload(ConfigFile file)
        {
            lock (Lock)
            {
                if (!TrackedFiles.Contains(file)) return false;
                foreach (var pair in TrackedEntries)
                {
                    if (pair.Key.ConfigFile == file) SetSilently(pair.Key, pair.Value.Local);
                }
                _busy++;
                (_reloading ?? (_reloading = new HashSet<ConfigFile>())).Add(file);
                return true;
            }
        }

        /// <summary>
        /// After the file was read, before ConfigReloaded fires: what was read is the player's value,
        /// then the server values are applied again.
        /// </summary>
        public static void AfterReload(ConfigFile file)
        {
            if (_reloading == null || !_reloading.Remove(file)) return;
            lock (Lock)
            {
                try
                {
                    foreach (var pair in TrackedEntries)
                    {
                        if (pair.Key.ConfigFile != file) continue;
                        pair.Value.Local = pair.Key.GetSerializedValue();
                        SetValue(pair.Key, pair.Value.Server);
                        pair.Value.Applied = pair.Key.GetSerializedValue();
                    }
                }
                finally
                {
                    _busy--;
                }
            }
            Plugin.Verbose($"{Path.GetFileName(file.ConfigFilePath)} was reloaded; server values applied again.");
        }

        // ---- Messages ------------------------------------------------------

        public static void Update()
        {
            var player = Player.m_localPlayer;
            if (player == null || MessageHud.instance == null) return;
            lock (PendingMessages)
            {
                foreach (var message in PendingMessages) ShowMessage(player, message);
                PendingMessages.Clear();
            }
        }

        private static MethodInfo _messageMethod;

        // Called by reflection: this DLL is built against the dedicated server assembly, and the
        // optional parameters of Character.Message are not guaranteed to match the game client.
        private static void ShowMessage(Player player, string text)
        {
            try
            {
                if (_messageMethod == null)
                {
                    _messageMethod = typeof(Character).GetMethods(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(m =>
                    {
                        var p = m.GetParameters();
                        return m.Name == "Message" && p.Length >= 2 && p[0].ParameterType == typeof(MessageHud.MessageType) && p[1].ParameterType == typeof(string);
                    });
                }
                if (_messageMethod == null) return;

                var parameters = _messageMethod.GetParameters();
                var args = new object[parameters.Length];
                args[0] = MessageHud.MessageType.TopLeft;
                args[1] = text;
                for (int i = 2; i < parameters.Length; i++)
                {
                    var type = parameters[i].ParameterType;
                    args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue
                        : type.IsValueType ? Activator.CreateInstance(type) : null;
                }
                _messageMethod.Invoke(player, args);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"Cannot show message: {e.Message}");
            }
        }

        private static bool HasListeners(ConfigEntryBase entry)
        {
            try
            {
                if (AccessTools.Field(entry.GetType(), "SettingChanged")?.GetValue(entry) is Delegate own && own.GetInvocationList().Length > 0)
                    return true;

                if (AccessTools.Field(typeof(ConfigFile), "SettingChanged")?.GetValue(entry.ConfigFile) is Delegate fileHandlers)
                {
                    foreach (var handler in fileHandlers.GetInvocationList())
                    {
                        // Every ConfigEntry subscribes itself to forward events; that is not a listener.
                        if (handler.Target is ConfigEntryBase) continue;
                        if (handler.Method.DeclaringType?.Assembly == typeof(ClientSide).Assembly) continue;
                        return true;
                    }
                }
            }
            catch
            {
                return true;
            }
            return false;
        }

        private static string PluginName(string guid)
        {
            return Chainloader.PluginInfos.TryGetValue(guid, out var info) ? info.Metadata.Name : guid;
        }
    }
}
