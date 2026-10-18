using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerConfigSync;

internal static class Program
{
    private static int _failures;
    private static string _dir;

    private static int Main()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        try
        {
            Setup();
            Run(nameof(ProtocolRoundTrip), ProtocolRoundTrip);
            Run(nameof(ProtocolRejectsBadData), ProtocolRejectsBadData);
            Run(nameof(ChunksReassembleOutOfOrder), ChunksReassembleOutOfOrder);
            Run(nameof(Filters), Filters);
            Run(nameof(ApplySaveReloadRestore), ApplySaveReloadRestore);
            Run(nameof(NewSnapshotRestoresDroppedEntries), NewSnapshotRestoresDroppedEntries);
            Run(nameof(PlayerValueComesFromDisk), PlayerValueComesFromDisk);
        }
        finally
        {
            Directory.Delete(_dir, true);
        }

        System.Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Setup()
    {
        Plugin.Log = BepInEx.Logging.Logger.CreateLogSource("test");
        var listener = new ConsoleListener();
        BepInEx.Logging.Logger.Listeners.Add(listener);
        var settings = new ConfigFile(Path.Combine(_dir, "settings.cfg"), false);
        Plugin.VerboseLogging = settings.Bind("d", "VerboseLogging", true, "");
        Plugin.ShowStartupWarning = settings.Bind("c", "ShowStartupWarning", true, "");
        Plugin.ListAffectedMods = settings.Bind("c", "ListAffectedMods", true, "");
        Plugin.AcceptServerConfig = settings.Bind("c", "AcceptServerConfig", true, "");

        var harmony = new Harmony("tests");
        harmony.CreateClassProcessor(typeof(ConfigFileSavePatch)).Patch();
        harmony.CreateClassProcessor(typeof(ConfigFileReloadPatch)).Patch();
        harmony.CreateClassProcessor(typeof(ConfigFileOnConfigReloadedPatch)).Patch();
    }

    private sealed class ConsoleListener : ILogListener
    {
        public void LogEvent(object sender, LogEventArgs e) => System.Console.WriteLine($"    [{e.Level}] {e.Data}");
        public void Dispose() { }
    }

    private static void Run(string name, Action test)
    {
        System.Console.WriteLine($"- {name}");
        try { test(); }
        catch (Exception e) { Fail($"threw {e}"); }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) Fail(what);
    }

    private static void Fail(string what)
    {
        _failures++;
        System.Console.WriteLine($"    FAIL: {what}");
    }

    private static Snapshot MakeSnapshot(string guid, params (string section, string key, string value)[] entries)
    {
        var snapshot = new Snapshot { ServerVersion = "test" };
        snapshot.Plugins[guid] = entries.Select(e => new SyncedValue { Section = e.section, Key = e.key, Value = e.value }).ToList();
        return snapshot;
    }

    // ---- Protocol ----------------------------------------------------------

    private static void ProtocolRoundTrip()
    {
        var snapshot = MakeSnapshot("a.b.c", ("Sezione è", "Chiave = x", "valore ✓"), ("S", "K", ""));
        snapshot.Plugins["other"] = new List<SyncedValue>();
        var back = Protocol.Deserialize(Protocol.Serialize(snapshot));
        Check(back.ServerVersion == "test", "server version");
        Check(back.Plugins.Count == 2, "plugin count");
        var e = back.Plugins["a.b.c"][0];
        Check(e.Section == "Sezione è" && e.Key == "Chiave = x" && e.Value == "valore ✓", "unicode strings");
        Check(back.Plugins["a.b.c"][1].Value == "", "empty value");
    }

    private static void ProtocolRejectsBadData()
    {
        // Decompression bomb: 40 MB of zeros.
        byte[] bomb;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
            using (var w = new BinaryWriter(gz))
            {
                w.Write(Protocol.Version);
                w.Write(new string('x', 10)); // version
                w.Write(1);                   // plugins
                w.Write("g");
                w.Write(1);
                gz.Write(new byte[40 * 1024 * 1024], 0, 40 * 1024 * 1024);
            }
            bomb = ms.ToArray();
        }
        Check(Throws(() => Protocol.Deserialize(bomb)), "trailing data rejected");

        // Decompression bomb made of valid entries: ~108 MB once decompressed.
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
            using (var w = new BinaryWriter(gz))
            {
                string big = new string('a', 60000);
                w.Write(Protocol.Version);
                w.Write("v");
                w.Write(1);
                w.Write("g");
                w.Write(600);
                for (int i = 0; i < 600; i++) { w.Write(big); w.Write(big); w.Write(big); }
            }
            bomb = ms.ToArray();
        }
        Check(bomb.Length < Protocol.ChunkSize * Protocol.MaxChunks, $"bomb fits the transfer limit ({bomb.Length} bytes)");
        Check(Throws(() => Protocol.Deserialize(bomb)), "decompression bomb rejected");

        // Huge declared string length.
        byte[] longString;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
            using (var w = new BinaryWriter(gz))
            {
                w.Write(Protocol.Version);
                w.Write(new string('x', Protocol.MaxStringLength + 1));
            }
            longString = ms.ToArray();
        }
        Check(Throws(() => Protocol.Deserialize(longString)), "too long string rejected");

        // Too many plugins.
        byte[] many;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
            using (var w = new BinaryWriter(gz))
            {
                w.Write(Protocol.Version);
                w.Write("v");
                w.Write(int.MaxValue);
            }
            many = ms.ToArray();
        }
        Check(Throws(() => Protocol.Deserialize(many)), "plugin count rejected");

        Check(Throws(() => Protocol.Deserialize(new byte[] { 1, 2, 3 })), "garbage rejected");
    }

    private static void ChunksReassembleOutOfOrder()
    {
        var rnd = new Random(1);
        var data = new byte[Protocol.ChunkSize * 2 + 123];
        rnd.NextBytes(data);
        var chunks = Protocol.Split(data);
        Check(chunks.Count == 3, "three chunks");
        var assembler = new ChunkAssembler();
        Check(assembler.Add(7, 2, 3, chunks[2]) == null, "incomplete after 1");
        Check(assembler.Add(7, 0, 3, chunks[0]) == null, "incomplete after 2");
        var result = assembler.Add(7, 1, 3, chunks[1]);
        Check(result != null && result.SequenceEqual(data), "reassembled");
        Check(Protocol.Split(new byte[0]).Count == 1, "empty payload is one chunk");
        Check(Throws(() => assembler.Add(1, 5, 3, new byte[1])), "index out of range rejected");
        Check(Throws(() => assembler.Add(1, 0, 1000, new byte[1])), "too many chunks rejected");
    }

    // ---- Filters -----------------------------------------------------------

    private static void Filters()
    {
        var file = new ConfigFile(Path.Combine(_dir, "filters.cfg"), false);
        var webhook = file.Bind("Webhook", "URL", "https://discord.com/api/webhooks/1/abc", "");
        var avatar = file.Bind("Player Capture Webhook", "Username", "Ratatoskr", "");
        var token = file.Bind("General", "Bot Token", "", "");
        var plainUrlValue = file.Bind("General", "Link", "http://example.com", "");
        var normal = file.Bind("Arrow Improvements", "Set Arrow Velocity", 70f, "");
        var keyCode = file.Bind("Bow Zoom", "Zoom Key", UnityEngine.KeyCode.Mouse1, "");
        var shortcut = file.Bind("Screenshot", "Capture", new KeyboardShortcut(UnityEngine.KeyCode.F12), "");
        var hotkeyName = file.Bind("Screenshot", "Hotkey", "F12", "");

        Check(EntryFilter.LooksSecret(webhook, webhook.GetSerializedValue()), "webhook URL is secret");
        Check(EntryFilter.LooksSecret(avatar, avatar.GetSerializedValue()), "entry in a webhook section is secret");
        Check(EntryFilter.LooksSecret(token, ""), "token is secret");
        Check(EntryFilter.LooksSecret(plainUrlValue, plainUrlValue.GetSerializedValue()), "URL value is secret");
        Check(!EntryFilter.LooksSecret(normal, "70"), "normal entry is not secret");

        Check(EntryFilter.IsKeybind(keyCode), "KeyCode is keybind");
        Check(EntryFilter.IsKeybind(shortcut), "KeyboardShortcut is keybind");
        Check(EntryFilter.IsKeybind(hotkeyName), "'Hotkey' name is keybind");
        Check(!EntryFilter.IsKeybind(normal), "normal entry is not keybind");

        var patterns = EntryPatterns.Get("ishid4.mods.betterarchery:Bow Zoom:*, torokal.afkmanager, a.b:General:Speed");
        Check(patterns.Matches("ishid4.mods.betterarchery", "Bow Zoom", "Zoom Factor"), "section wildcard");
        Check(!patterns.Matches("ishid4.mods.betterarchery", "Arrow Improvements", "Set Arrow Velocity"), "other section not matched");
        Check(patterns.Matches("torokal.afkmanager", "Any", "Thing"), "whole plugin");
        Check(patterns.Matches("A.B", "general", "speed"), "case-insensitive");
        Check(!patterns.Matches("a.b", "General", "Speedy"), "exact key");
        Check(!EntryPatterns.Get("").Matches("x", "y", "z"), "empty list matches nothing");
    }

    // ---- Client apply / save / reload / restore ------------------------------

    private static void ApplySaveReloadRestore()
    {
        string path = Path.Combine(_dir, "mod.cfg");
        var file = new ConfigFile(path, true);
        var velocity = file.Bind("Arrows", "Velocity", 70f, "");
        var enabled = file.Bind("General", "Enabled", true, "");
        var name = file.Bind("General", "Name", "local", "");
        var clamped = file.Bind("General", "Percent", 50, new ConfigDescription("", new AcceptableValueRange<int>(0, 100)));
        file.Save();
        string original = File.ReadAllText(path);

        int events = 0, reloadedVelocity = -1;
        file.SettingChanged += (s, e) => events++;
        file.ConfigReloaded += (s, e) => reloadedVelocity = (int)velocity.Value;
        ClientSide.ResolveConfig = guid => guid == "mod" ? file : null;

        ClientSide.Apply(MakeSnapshot("mod",
            ("Arrows", "Velocity", "100"), ("General", "Enabled", "false"), ("General", "Percent", "500"),
            ("General", "DoesNotExist", "1"), ("Nope", "Nope", "1")));

        Check(velocity.Value == 100f && enabled.Value == false, "server values applied");
        Check(clamped.Value == 100, "server value clamped to the acceptable range");
        Check(events == 3, $"mods notified of 3 changes (got {events})");
        Check(File.ReadAllText(path) == original, "file untouched by apply");

        file.Save();
        Check(File.ReadAllText(path) == original, "explicit Save writes only the player's values");
        Check(velocity.Value == 100f, "server value still active after Save");

        name.Value = "changed"; // untracked entry, SaveOnConfigSet = true
        string text = File.ReadAllText(path);
        Check(text.Contains("Name = changed") && text.Contains("Velocity = 70") && text.Contains("Enabled = true"),
            "player's change to an unsynced entry is saved, synced entries keep the player's values on disk");

        velocity.Value = 80f; // player edits a synced entry while connected
        Check(velocity.Value == 100f, "server value stays active after a local edit");
        Check(File.ReadAllText(path).Contains("Velocity = 80"), "local edit saved as the player's value");

        // A mod's file watcher reloads after the player edited the file by hand.
        File.WriteAllText(path, File.ReadAllText(path).Replace("Velocity = 80", "Velocity = 90"));
        file.Reload();
        Check(velocity.Value == 100f, "server value active after Reload");
        Check(reloadedVelocity == 100, $"ConfigReloaded handlers already see the server value (saw {reloadedVelocity})");

        events = 0;
        ClientSide.RestoreAll("test");
        Check(velocity.Value == 90f, $"restored to the player's latest value (got {velocity.Value})");
        Check(enabled.Value == true && clamped.Value == 50 && name.Value == "changed", "other values restored");
        Check(events == 3, $"mods notified of the restore (got {events})");

        string before = File.ReadAllText(path);
        velocity.Value = 91f;
        Check(File.ReadAllText(path).Contains("Velocity = 91"), "normal saving works again after restore");
        Check(before.Contains("Velocity = 90"), "file had the player's value:\n" + before);
    }

    private static void NewSnapshotRestoresDroppedEntries()
    {
        var file = new ConfigFile(Path.Combine(_dir, "mod2.cfg"), true);
        var a = file.Bind("S", "A", 1, "");
        var b = file.Bind("S", "B", 1, "");
        ClientSide.ResolveConfig = guid => guid == "mod2" ? file : null;

        ClientSide.Apply(MakeSnapshot("mod2", ("S", "A", "5"), ("S", "B", "5")));
        Check(a.Value == 5 && b.Value == 5, "first snapshot applied");
        ClientSide.Apply(MakeSnapshot("mod2", ("S", "A", "6")));
        Check(a.Value == 6 && b.Value == 1, "second snapshot: A updated, B back to the player's value");
        ClientSide.RestoreAll("test");
        Check(a.Value == 1, "A restored to the player's original value, not the first server value");
    }

    // A mod's own sync system applied the server value (in memory only) before ServerConfigSync did.
    private static void PlayerValueComesFromDisk()
    {
        string path = Path.Combine(_dir, "mod3.cfg");
        var file = new ConfigFile(path, true);
        var speed = file.Bind("S", "Speed", 1, "");
        speed.Value = 3; // the player's value, saved
        file.SaveOnConfigSet = false;
        speed.Value = 9; // the other sync system's server value, not saved
        file.SaveOnConfigSet = true;
        ClientSide.ResolveConfig = guid => guid == "mod3" ? file : null;

        ClientSide.Apply(MakeSnapshot("mod3", ("S", "Speed", "9")));
        Check(speed.Value == 9, "server value active");
        file.Save();
        Check(File.ReadAllText(path).Contains("Speed = 3"), "the player's value stays on disk");
        ClientSide.RestoreAll("test");
        Check(speed.Value == 3, $"restored to the value on disk (got {speed.Value})");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch { return true; }
    }
}
