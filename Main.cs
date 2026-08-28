using GriefLedger.Hooks;
using HarmonyLib;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace GriefLedger;

public class Main : ModSystem {
    private Harmony harmony = null;
    public static ICoreServerAPI API { get; private set; }
    public static Database Database { get; private set; }
    public static Dictionary<string, string> CachedPlayerUsernames { get; private set; } = new();

    public override bool ShouldLoad(EnumAppSide forSide) {
        return forSide == EnumAppSide.Server;
    }

    public override void StartServerSide(ICoreServerAPI api) {
        API = api;
        Database = new Database();

        new BlockHooks();
        new EntityHooks();

        new Commands();

        API.Event.PlayerJoin += OnPlayerJoin;

        harmony = new Harmony(Mod.Info.ModID);
        harmony.PatchAll();
    }

    public override void Dispose() {
        Database.Dispose();

        harmony?.UnpatchAll(Mod.Info.ModID);
    }

    private void OnPlayerJoin(IServerPlayer player) {
        CachedPlayerUsernames[player.PlayerUID] = player.PlayerName;
    }
}
