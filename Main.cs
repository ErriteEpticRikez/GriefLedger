using GriefWarden.Hooks;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace GriefWarden;

public class Main : ModSystem {
    private Harmony harmony = null;
    public static ICoreServerAPI API { get; private set; }
    public static Database Database { get; private set; }

    public override bool ShouldLoad(EnumAppSide forSide) {
        return forSide == EnumAppSide.Server;
    }

    public override void StartServerSide(ICoreServerAPI api) {
        API = api;
        Database = new Database();

        new BlockHooks();
        new EntityHooks();

        new Commands();

        harmony = new Harmony(Mod.Info.ModID);
        harmony.PatchAll();
    }

    public override void Dispose() {
        Database.Dispose();

        harmony?.UnpatchAll(Mod.Info.ModID);
    }
}
