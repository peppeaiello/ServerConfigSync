using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;

namespace ServerConfigSync
{
    internal static class ServerSide
    {
        private sealed class ClientInfo
        {
            public int Protocol;
            public string Version;
        }

        private static readonly ConditionalWeakTable<ZRpc, ClientInfo> Clients = new ConditionalWeakTable<ZRpc, ClientInfo>();

        private static byte[] _payload;
        private static List<byte[]> _chunks;
        private static int _transferId;

        private static FileSystemWatcher _watcher;
        private static readonly object WatcherLock = new object();
        private static readonly HashSet<string> ChangedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastChange;

        public static void RegisterPeer(ZNetPeer peer)
        {
            peer.m_rpc.Register<ZPackage>(Protocol.RpcHello, OnHello);
        }

        private static void OnHello(ZRpc rpc, ZPackage pkg)
        {
            var info = new ClientInfo { Protocol = pkg.ReadInt(), Version = pkg.ReadString() };
            Clients.Remove(rpc);
            Clients.Add(rpc, info);
            Plugin.Log.LogInfo($"Client {Describe(rpc)} has ServerConfigSync {info.Version} (protocol {info.Protocol})");
            StartWatcher();
        }

        /// <summary>Prefix of ZNet.RPC_PeerInfo. Returning false rejects the client.</summary>
        public static bool AllowPeer(ZRpc rpc)
        {
            if (!Plugin.RequireClientMod.Value) return true;
            if (Clients.TryGetValue(rpc, out var info) && info.Protocol == Protocol.Version) return true;

            Plugin.Log.LogWarning($"Rejecting {Describe(rpc)}: ServerConfigSync is required on this server " +
                                  (info == null ? "and the client does not have it." : $"but the client uses protocol {info.Protocol}."));
            rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
            return false;
        }

        /// <summary>Postfix of ZNet.RPC_PeerInfo: the client passed all vanilla checks.</summary>
        public static void OnPeerInfo(ZRpc rpc)
        {
            var peer = FindPeer(rpc);
            if (peer == null || !peer.IsReady()) return;

            if (!Clients.TryGetValue(rpc, out var info))
            {
                Plugin.Log.LogInfo($"{peer.m_playerName} does not have ServerConfigSync, nothing sent.");
                return;
            }
            if (info.Protocol != Protocol.Version)
            {
                Plugin.Log.LogWarning($"{peer.m_playerName} uses ServerConfigSync protocol {info.Protocol}, this server uses {Protocol.Version}; nothing sent.");
                return;
            }
            if (!Plugin.ServerEnabled.Value) return;

            // Rebuilt for every player: some mods create entries late, and values may have changed.
            Rebuild();
            Send(rpc);
            Plugin.Log.LogInfo($"Sent settings to {peer.m_playerName} ({_payload.Length} bytes, {_chunks.Count} part(s)).");
        }

        /// <summary>Rebuilds the payload. Returns true if it differs from the previous one (the summary is logged then).</summary>
        public static bool Rebuild()
        {
            var snapshot = SnapshotBuilder.Build();
            var payload = Protocol.Serialize(snapshot);
            if (_payload != null && _payload.SequenceEqual(payload)) return false;

            _payload = payload;
            _chunks = Protocol.Split(payload);
            _transferId++;
            foreach (var line in snapshot.Summary) Plugin.Log.LogInfo(line);
            return true;
        }

        private static void Send(ZRpc rpc)
        {
            for (int i = 0; i < _chunks.Count; i++)
            {
                var pkg = new ZPackage();
                pkg.Write(Protocol.Version);
                pkg.Write(_transferId);
                pkg.Write(i);
                pkg.Write(_chunks.Count);
                pkg.Write(_chunks[i].Length);
                pkg.Write(_chunks[i]);
                rpc.Invoke(Protocol.RpcData, pkg);
            }
        }

        private static ZNetPeer FindPeer(ZRpc rpc)
        {
            return ZNet.instance?.GetPeers().FirstOrDefault(p => p.m_rpc == rpc);
        }

        private static string Describe(ZRpc rpc)
        {
            try
            {
                return rpc.GetSocket().GetHostName();
            }
            catch
            {
                return "?";
            }
        }

        // ---- Live updates -------------------------------------------------

        private static void StartWatcher()
        {
            if (_watcher != null || !Plugin.LiveUpdates.Value) return;
            try
            {
                _watcher = new FileSystemWatcher(Paths.ConfigPath, "*.cfg")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };
                _watcher.Changed += OnFileEvent;
                _watcher.Created += OnFileEvent;
                _watcher.Renamed += OnFileEvent;
                _watcher.EnableRaisingEvents = true;
                Plugin.Log.LogInfo($"Watching {Paths.ConfigPath} for config changes.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Live updates disabled, cannot watch the config folder: {e.Message}");
                _watcher = null;
            }
        }

        public static void StopWatcher()
        {
            if (_watcher == null) return;
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        /// <summary>Called when the server shuts down.</summary>
        public static void Reset()
        {
            StopWatcher();
            _payload = null;
            _chunks = null;
        }

        private static void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            lock (WatcherLock)
            {
                ChangedFiles.Add(Path.GetFullPath(e.FullPath));
                _lastChange = DateTime.UtcNow;
            }
        }

        /// <summary>Main thread. Reloads changed files after they settle, then pushes new values.</summary>
        public static void Update()
        {
            List<string> changed;
            lock (WatcherLock)
            {
                if (ChangedFiles.Count == 0 || DateTime.UtcNow - _lastChange < TimeSpan.FromSeconds(2)) return;
                changed = ChangedFiles.ToList();
                ChangedFiles.Clear();
            }
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var files = new HashSet<string>(changed, StringComparer.OrdinalIgnoreCase);
            var reloaded = new HashSet<ConfigFile>();
            foreach (var info in Chainloader.PluginInfos.Values)
            {
                var config = info?.Instance?.Config;
                if (config == null || reloaded.Contains(config) || Plugin.IsCoreConfig(config)) continue;
                string path;
                try { path = Path.GetFullPath(config.ConfigFilePath); }
                catch { continue; }
                if (!files.Contains(path) || !File.Exists(path)) continue;

                try
                {
                    config.Reload();
                    reloaded.Add(config);
                    Plugin.Log.LogInfo($"Reloaded {Path.GetFileName(path)}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Could not reload {Path.GetFileName(path)}: {e.Message}");
                }
            }
            if (reloaded.Count == 0) return;

            if (!Rebuild() || !Plugin.ServerEnabled.Value) return;
            int sent = 0;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady() || !Clients.TryGetValue(peer.m_rpc, out var info) || info.Protocol != Protocol.Version) continue;
                Send(peer.m_rpc);
                sent++;
            }
            if (sent > 0) Plugin.Log.LogInfo($"Pushed updated settings to {sent} player(s).");
        }
    }
}
