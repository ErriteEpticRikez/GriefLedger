using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

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

[HarmonyPatch(typeof(ItemChisel), nameof(ItemChisel.OnHeldInteractStart))]
public class ItemChiselInteractPatch {
    [HarmonyPrefix]
    public static void Prefix(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, out PatchState __state) {
        BlockPos pos = blockSel.Position;
        Block block = byEntity.World.BlockAccessor.GetBlock(pos);

        __state = new PatchState();
        __state.oldBlockID = block.Id;
        __state.oldBlock = block.ToString();
    }

    [HarmonyPostfix]
    public static void Postfix(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, PatchState __state) {
        if (__state.oldBlockID == 648)
            return;

        BlockPos pos = blockSel.Position;
        Block block = byEntity.World.BlockAccessor.GetBlock(pos);

        if (block.Id != 648)
            return;

        EntityPlayer? entityPlayer = byEntity as EntityPlayer;
        IPlayer? byPlayer = entityPlayer?.Player;

        string? playername = byPlayer?.PlayerName;
        string? playeruid = byPlayer?.PlayerUID;
        Vec3i blockPosition = blockSel.Position.ToLocalPosition(Main.API);

        Main.Database.AddBlockLog(playername, playeruid, "USED", __state.oldBlock, "CHISEL", blockPosition.X, blockPosition.Y, blockPosition.Z, __state.oldBlockID);
    }

    public class PatchState {
        public int oldBlockID;
        public string oldBlock;
    }
}

[HarmonyPatch(typeof(BlockBehaviorRightClickPickup), nameof(BlockBehaviorRightClickPickup.OnBlockInteractStart))]
public class BlockBehaviorRightClickPickUpPatch {
    [HarmonyPrefix]
    public static void Prefix(BlockBehaviorRightClickPickup __instance, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, out PatchState __state) {
        __state = new();
        __state.oldBlockID = __instance.block.Id;
        __state.oldBlock = __instance.block.ToString();
    }

    [HarmonyPostfix]
    public static void Postfix(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, PatchState __state) {
        Block newBlock = Main.API.World.BlockAccessor.GetBlock(blockSel.Position);

        if (newBlock.Id == __state.oldBlockID)
            return;

        string? itemstack = Util.GetPlayerCurrentItemstackName(byPlayer);
        Vec3i blockPosition = blockSel.Position.ToLocalPosition(Main.API);

        Main.Database.AddBlockLog(byPlayer.PlayerName, byPlayer.PlayerUID, "BROKE", __state.oldBlock, itemstack, blockPosition.X, blockPosition.Y, blockPosition.Z, __state.oldBlockID);
    }

    public class PatchState {
        public int oldBlockID;
        public string oldBlock;
    }
}