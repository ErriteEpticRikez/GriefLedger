using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace GriefWarden.Patches;

// Harmony can't find the method without types specified for god knows what reason
[HarmonyPatch(typeof(Block), nameof(Block.OnBlockExploded), new Type[] { typeof(IWorldAccessor), typeof(BlockPos), typeof(BlockPos), typeof(EnumBlastType), typeof(string) })]
public class BlockOnBlockExplodedPatch {
    // Save block in prefix to handle default too
    [HarmonyPrefix]
    public static void Prefix(Block __instance, IWorldAccessor world, BlockPos pos, BlockPos explosionCenter, EnumBlastType blastType, string ignitedByPlayerUid, out PatchState __state) {
        __state = new PatchState();
        Block block = Main.API.World.BlockAccessor.GetBlock(pos);
        __state.oldBlockStr = block.ToString();
    }
    
    [HarmonyPostfix]
    public static void Postfix(Block __instance, IWorldAccessor world, BlockPos pos, BlockPos explosionCenter, EnumBlastType blastType, string ignitedByPlayerUid, PatchState __state) {
        string? playername = null;
        if (ignitedByPlayerUid != null)
            playername = Main.CachedPlayerUsernames[ignitedByPlayerUid];

        Vec3i blockPosition = pos.ToLocalPosition(Main.API);

        Main.Database.AddBlockLog(playername, ignitedByPlayerUid, "BROKE", __state.oldBlockStr, blastType + "Bomb", blockPosition.X, blockPosition.Y, blockPosition.Z, null);
    }

    public class PatchState {
        public string oldBlockStr;
    }
}
