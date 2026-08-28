using GriefLedger.Hooks;
using GriefLedger.Rollback;
using HarmonyLib;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace GriefLedger;

public class Main : ModSystem {
    private Harmony harmony = null;
    private Commands? commands;
    public static ICoreServerAPI API { get; private set; }
    public static Database Database { get; private set; }
    public static Dictionary<string, string> CachedPlayerUsernames { get; private set; } = new();
    public static RollbackCapability? ExactRollbackCapability { get; private set; }
    public static BlockMutationCapture? ExactBlockMutationCapture { get; private set; }
    public static BlockRollbackService? ExactBlockRollbackService { get; private set; }
    public static bool ExactRollbackAvailable => ExactRollbackCapability?.IsAvailable == true;

    public override bool ShouldLoad(EnumAppSide forSide) {
        return forSide == EnumAppSide.Server;
    }

    public override void StartServerSide(ICoreServerAPI api) {
        commands?.Dispose();
        commands = null;
        ExactBlockRollbackService?.Dispose();
        ExactBlockRollbackService = null;
        ExactBlockMutationCapture?.Dispose();
        ExactBlockMutationCapture = null;
        API = api;
        Database = new Database();

        new BlockHooks();
        new EntityHooks();

        commands = new Commands();

        API.Event.PlayerJoin += OnPlayerJoin;

        harmony = new Harmony(Mod.Info.ModID);
        harmony.PatchAll();
        RollbackTargetStatus legacyChiselAudit = RollbackCapability.EnsureLegacyChiselAuditPatch(harmony);
        if (!legacyChiselAudit.Patched) {
            API.Logger.Error(
                "GriefLedger: Legacy chisel audit patch is unavailable; other audit hooks, database, and commands remain enabled. {0}",
                legacyChiselAudit.Error ?? "exact ItemChisel target was not resolved"
            );
        }
        ExactRollbackCapability = RollbackCapability.Initialize(api, harmony);
        if (ExactRollbackCapability.IsAvailable) {
            try {
                ExactBlockMutationCapture = BlockMutationCapture.Attach(api, Database);
                ExactBlockRollbackService = new BlockRollbackService(api, Database, ExactBlockMutationCapture);
            }
            catch (System.Exception exception) {
                ExactBlockMutationCapture?.Dispose();
                ExactBlockMutationCapture = null;
                ExactBlockRollbackService = null;
                API.Logger.Error("GriefLedger: Exact rollback capture could not start; legacy auditing remains enabled. {0}", exception);
            }
        }
    }

    public override void Dispose() {
        commands?.Dispose();
        commands = null;
        ExactBlockRollbackService?.Dispose();
        ExactBlockRollbackService = null;
        ExactBlockMutationCapture?.Dispose();
        ExactBlockMutationCapture = null;
        ExactRollbackCapability?.Dispose();
        ExactRollbackCapability = null;
        Database.Dispose();

        harmony?.UnpatchAll(Mod.Info.ModID);
    }

    private void OnPlayerJoin(IServerPlayer player) {
        CachedPlayerUsernames[player.PlayerUID] = player.PlayerName;
    }
}
