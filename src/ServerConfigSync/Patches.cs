using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace ServerConfigSync
{
    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    internal static class ZNetOnNewConnectionPatch
    {
        private static void Postfix(ZNet __instance, ZNetPeer __0)
        {
            try
            {
                if (__instance.IsServer()) ServerSide.RegisterPeer(__0);
                else ClientSide.RegisterPeer(__0);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnNewConnection: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    internal static class ZNetRpcPeerInfoPatch
    {
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(ZNet __instance, ZRpc __0)
        {
            return !__instance.IsServer() || ServerSide.AllowPeer(__0);
        }

        private static void Postfix(ZNet __instance, ZRpc __0)
        {
            try
            {
                if (__instance.IsServer()) ServerSide.OnPeerInfo(__0);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Sending settings failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "Awake")]
    internal static class ZNetAwakePatch
    {
        // Lets the admin see in the server log what will be synced, before anyone connects.
        private static void Postfix(ZNet __instance)
        {
            try
            {
                if (__instance.IsDedicated() && Plugin.ServerEnabled.Value) ServerSide.Rebuild();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Preparing settings failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnDestroy")]
    internal static class ZNetOnDestroyPatch
    {
        private static void Prefix(ZNet __instance)
        {
            if (__instance.IsServer()) ServerSide.Reset();
            else ClientSide.RestoreAll("disconnected");
        }
    }

    [HarmonyPatch(typeof(ConfigFile), nameof(ConfigFile.Save))]
    internal static class ConfigFileSavePatch
    {
        private static bool Prefix(ConfigFile __instance, out List<KeyValuePair<ConfigEntryBase, string>> __state)
        {
            return ClientSide.BeforeSave(__instance, out __state);
        }

        private static Exception Finalizer(Exception __exception, List<KeyValuePair<ConfigEntryBase, string>> __state)
        {
            ClientSide.AfterSave(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ConfigFile), nameof(ConfigFile.Reload))]
    internal static class ConfigFileReloadPatch
    {
        private static void Prefix(ConfigFile __instance)
        {
            ClientSide.BeforeReload(__instance);
        }

        // Normally AfterReload already ran from OnConfigReloaded; this covers a Reload that threw.
        private static Exception Finalizer(ConfigFile __instance, Exception __exception)
        {
            ClientSide.AfterReload(__instance);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ConfigFile), "OnConfigReloaded")]
    internal static class ConfigFileOnConfigReloadedPatch
    {
        private static void Prefix(ConfigFile __instance)
        {
            ClientSide.AfterReload(__instance);
        }
    }
}
