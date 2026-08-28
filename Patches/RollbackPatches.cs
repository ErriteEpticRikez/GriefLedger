using System;
using System.Collections.Generic;
using GriefLedger.Rollback;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.Server;

namespace GriefLedger.Patches;

internal sealed class KeyedPendingTracker<TKey, TValue> where TKey : notnull where TValue : class {
    private readonly Dictionary<TKey, List<TValue>> pending = new();

    internal int Count { get; private set; }

    internal void Add(TKey key, TValue value) {
        if (!pending.TryGetValue(key, out List<TValue>? values)) {
            values = new List<TValue>();
            pending.Add(key, values);
        }
        values.Add(value);
        Count++;
    }

    internal bool TryTake(TKey key, out TValue? value) {
        value = null;
        if (!pending.TryGetValue(key, out List<TValue>? values) || values.Count == 0) return false;
        int index = values.Count - 1;
        value = values[index];
        values.RemoveAt(index);
        if (values.Count == 0) pending.Remove(key);
        Count--;
        return true;
    }

    internal bool Remove(TKey key, TValue value) {
        if (!pending.TryGetValue(key, out List<TValue>? values)) return false;
        for (int index = values.Count - 1; index >= 0; index--) {
            if (!ReferenceEquals(values[index], value)) continue;
            values.RemoveAt(index);
            if (values.Count == 0) pending.Remove(key);
            Count--;
            return true;
        }
        return false;
    }

    internal IReadOnlyList<TValue> Drain() {
        var values = new List<TValue>(Count);
        foreach (List<TValue> group in pending.Values) values.AddRange(group);
        pending.Clear();
        Count = 0;
        return values;
    }
}

internal readonly record struct BreakConfirmationKey(string PlayerUid, int Dimension, int X, int Y, int Z);

internal sealed class PendingBreakConfirmation {
    internal PendingBreakConfirmation(PlayerBreakSeamContext context, RollbackMutationOutcome outcome) {
        Context = context;
        Outcome = outcome;
    }

    internal PlayerBreakSeamContext Context { get; }
    internal RollbackMutationOutcome Outcome { get; }
}

internal sealed class PlayerBlockMutationPatchState {
    public PlayerPlacementSeamContext? Placement { get; init; }
    public PlayerBreakSeamContext? Break { get; init; }
}

internal static class PlayerBlockMutationPatch {
    [ThreadStatic]
    private static KeyedPendingTracker<BreakConfirmationKey, PendingBreakConfirmation>? pendingBreakConfirmations;

    private static KeyedPendingTracker<BreakConfirmationKey, PendingBreakConfirmation> PendingBreakConfirmations =>
        pendingBreakConfirmations ??= new KeyedPendingTracker<BreakConfirmationKey, PendingBreakConfirmation>();

    public static void Prefix(ServerPlayer player, global::Packet_ClientBlockPlaceOrBreak cmd, out PlayerBlockMutationPatchState? __state) {
        __state = null;
        if (!Main.ExactRollbackAvailable) return;

        try {
            int mode = cmd.Mode;
            if (mode is not 0 and not 1) return;

            IPlayer byPlayer = player;
            // Packet Y is BlockPos.InternalY; this constructor derives local Y and dimension.
            var position = new BlockPos(cmd.X, cmd.Y, cmd.Z);
            IWorldAccessor world = byPlayer.Entity.World;
            if (mode == 1) {
                Block? placingBlock = byPlayer.InventoryManager.ActiveHotbarSlot?.Itemstack?.Block;
                int layer = placingBlock?.ForFluidsLayer == true ? 2 : 1;
                var context = new PlayerPlacementSeamContext(byPlayer, world, position, layer, null);
                __state = new PlayerBlockMutationPatchState { Placement = context };
                RollbackSeams.EmitPlacementStarting(context);
                try {
                    context.BeforeBlock = world.BlockAccessor.GetBlock(position, layer);
                }
                catch {
                    RollbackSeams.EmitPlacementCompleted(context, RollbackMutationOutcome.Failed, "before-read");
                }
                return;
            }

            var breakContext = new PlayerBreakSeamContext(byPlayer, world, position, null, null);
            __state = new PlayerBlockMutationPatchState { Break = breakContext };
            RollbackSeams.EmitBreakStarting(breakContext);
            try {
                Block fluidLayerBlock = world.BlockAccessor.GetBlock(position, 2);
                breakContext.BlockLayer = fluidLayerBlock.SideSolid.Any ? 2 : 1;
                breakContext.BeforeBlock = world.BlockAccessor.GetBlock(position, breakContext.BlockLayer.Value);
            }
            catch {
                RollbackSeams.EmitBreakCompleted(breakContext, RollbackMutationOutcome.Failed, "before-read");
            }
        }
        catch {
            // A failure before enough authoritative values exist to create a seam must not affect gameplay.
        }
    }

    public static void Postfix(bool __result, PlayerBlockMutationPatchState? __state) {
        if (__state?.Placement is PlayerPlacementSeamContext placement) {
            CompletePlacement(placement, __result);
            return;
        }

        if (__state?.Break is not PlayerBreakSeamContext blockBreak || blockBreak.Outcome != RollbackMutationOutcome.Pending) return;
        if (!__result) {
            RollbackSeams.EmitBreakCompleted(blockBreak, RollbackMutationOutcome.Cancelled);
            return;
        }

        try {
            if (blockBreak.BlockLayer == null || blockBreak.BeforeBlock == null) {
                RollbackSeams.EmitBreakCompleted(blockBreak, RollbackMutationOutcome.Failed, "capture-incomplete");
                return;
            }
            blockBreak.AfterBlock = blockBreak.World.BlockAccessor.GetBlock(blockBreak.Position, blockBreak.BlockLayer.Value);
            RollbackMutationOutcome outcome = blockBreak.AfterBlock.Id == blockBreak.BeforeBlock.Id
                ? RollbackMutationOutcome.NoChange
                : RollbackMutationOutcome.Changed;
            var confirmation = new PendingBreakConfirmation(blockBreak, outcome);
            BreakConfirmationKey key = Key(blockBreak.Player, blockBreak.Position);
            KeyedPendingTracker<BreakConfirmationKey, PendingBreakConfirmation> tracker = PendingBreakConfirmations;
            tracker.Add(key, confirmation);
            try {
                Main.API.Event.EnqueueMainThreadTask(
                    () => FailIfStillPending(tracker, key, confirmation),
                    "griefledger-break-confirmation"
                );
            }
            catch {
                if (tracker.Remove(key, confirmation)) {
                    RollbackSeams.RecordBreakConfirmationMiss();
                    RollbackSeams.EmitBreakCompleted(blockBreak, RollbackMutationOutcome.Failed, "confirmation-scheduling-failed");
                }
            }
        }
        catch {
            RollbackSeams.RecordBreakConfirmationMiss();
            RollbackSeams.EmitBreakCompleted(blockBreak, RollbackMutationOutcome.Failed, "after-read");
        }
    }

    public static Exception? Finalizer(Exception? __exception, PlayerBlockMutationPatchState? __state) {
        if (__exception != null) {
            if (__state?.Placement is PlayerPlacementSeamContext placement) {
                RollbackSeams.EmitPlacementCompleted(placement, RollbackMutationOutcome.Failed, "mutation-threw");
            }
            if (__state?.Break is PlayerBreakSeamContext blockBreak) {
                RollbackSeams.EmitBreakCompleted(blockBreak, RollbackMutationOutcome.Failed, "mutation-threw");
            }
        }
        return __exception;
    }

    internal static void OnDidBreakBlock(IServerPlayer player, int oldBlockId, BlockSelection blockSel) {
        if (player == null || blockSel?.Position == null) {
            RollbackSeams.RecordBreakConfirmationMiss();
            return;
        }

        BreakConfirmationKey key = Key(player, blockSel.Position);
        if (!PendingBreakConfirmations.TryTake(key, out PendingBreakConfirmation? confirmation) || confirmation == null) {
            RollbackSeams.RecordBreakConfirmationMiss();
            return;
        }

        PlayerBreakSeamContext context = confirmation.Context;
        context.ConfirmedByDidBreak = true;
        context.ConfirmedOldBlockId = oldBlockId;
        context.ConfirmationMatchesCapture = context.BeforeBlock != null && oldBlockId == context.BeforeBlock.Id;
        if (!context.ConfirmationMatchesCapture) {
            RollbackSeams.RecordBreakConfirmationMiss();
            RollbackSeams.EmitBreakCompleted(context, RollbackMutationOutcome.Failed, "confirmation-mismatch");
            return;
        }
        RollbackSeams.EmitBreakCompleted(context, confirmation.Outcome);
    }

    internal static void FlushPendingBreaks() {
        if (pendingBreakConfirmations == null) return;
        foreach (PendingBreakConfirmation confirmation in pendingBreakConfirmations.Drain()) {
            RollbackSeams.RecordBreakConfirmationMiss();
            RollbackSeams.EmitBreakCompleted(confirmation.Context, RollbackMutationOutcome.Failed, "confirmation-abandoned");
        }
    }

    private static void FailIfStillPending(
        KeyedPendingTracker<BreakConfirmationKey, PendingBreakConfirmation> tracker,
        BreakConfirmationKey key,
        PendingBreakConfirmation confirmation
    ) {
        if (!tracker.Remove(key, confirmation)) return;
        RollbackSeams.RecordBreakConfirmationMiss();
        RollbackSeams.EmitBreakCompleted(confirmation.Context, RollbackMutationOutcome.Failed, "confirmation-missing");
    }

    private static BreakConfirmationKey Key(IPlayer player, BlockPos position) {
        return Key(player.PlayerUID ?? string.Empty, position);
    }

    internal static BreakConfirmationKey Key(string playerUid, BlockPos position) =>
        new(playerUid, position.dimension, position.X, position.Y, position.Z);

    private static void CompletePlacement(PlayerPlacementSeamContext context, bool result) {
        if (context.Outcome != RollbackMutationOutcome.Pending) return;
        if (!result) {
            RollbackSeams.EmitPlacementCompleted(context, RollbackMutationOutcome.Cancelled);
            return;
        }

        try {
            if (context.BeforeBlock == null) {
                RollbackSeams.EmitPlacementCompleted(context, RollbackMutationOutcome.Failed, "capture-incomplete");
                return;
            }
            context.AfterBlock = context.World.BlockAccessor.GetBlock(context.Position, context.BlockLayer);
            RollbackSeams.EmitPlacementCompleted(
                context,
                context.AfterBlock.Id == context.BeforeBlock.Id ? RollbackMutationOutcome.NoChange : RollbackMutationOutcome.Changed
            );
        }
        catch {
            RollbackSeams.EmitPlacementCompleted(context, RollbackMutationOutcome.Failed, "after-read");
        }
    }
}

internal sealed class ItemChiselPatchState {
    public ChiselConversionSeamContext? Context { get; init; }
    public bool ExactStarted { get; init; }
    public int BeforeBlockId { get; init; }
    public string? BeforeBlockName { get; init; }
}

internal static class ItemChiselConversionPatch {
    public static void Prefix(EntityAgent byEntity, BlockSelection blockSel, out ItemChiselPatchState? __state) {
        __state = null;
        if (byEntity?.World?.Side != EnumAppSide.Server || blockSel?.Position == null) return;

        IWorldAccessor world = byEntity.World;
        IPlayer? player = (byEntity as EntityPlayer)?.Player;
        if (Main.ExactRollbackAvailable) {
            var context = new ChiselConversionSeamContext(player, world, blockSel.Position, null);
            __state = new ItemChiselPatchState { Context = context, ExactStarted = true };
            RollbackSeams.EmitChiselConversionStarting(context);
            try {
                context.BeforeBlock = world.BlockAccessor.GetBlock(blockSel.Position);
            }
            catch {
                RollbackSeams.EmitChiselConversionCompleted(context, RollbackMutationOutcome.Failed, "before-read");
                return;
            }
            if (IsChiseledBlock(context.BeforeBlock)) {
                // This is not a conversion seam. Close the speculative start explicitly.
                RollbackSeams.EmitChiselConversionCompleted(context, RollbackMutationOutcome.NoChange);
                __state = CreateAuditState(context, false);
                return;
            }
            __state = CreateAuditState(context, true);
            return;
        }

        try {
            Block beforeBlock = world.BlockAccessor.GetBlock(blockSel.Position);
            var context = new ChiselConversionSeamContext(player, world, blockSel.Position, beforeBlock);
            __state = CreateAuditState(context, false);
        }
        catch {
            // Legacy auditing remains fail-open when its own pre-read is unavailable.
        }
    }

    public static void Postfix(EntityAgent byEntity, BlockSelection blockSel, ItemChiselPatchState? __state) {
        if (__state?.Context is not ChiselConversionSeamContext context || blockSel?.Position == null) return;

        Block afterBlock;
        BlockEntityChisel? chiselEntity;
        try {
            afterBlock = byEntity.World.BlockAccessor.GetBlock(blockSel.Position);
            chiselEntity = byEntity.World.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntityChisel;
        }
        catch {
            if (__state.ExactStarted) RollbackSeams.EmitChiselConversionCompleted(context, RollbackMutationOutcome.Failed, "after-read");
            return;
        }

        bool converted = !IsChiseledBlock(context.BeforeBlock) && IsChiseledBlock(afterBlock) && chiselEntity != null;
        context.AfterBlock = afterBlock;
        context.ChiselEntity = chiselEntity;

        if (converted && context.BeforeBlock != null) {
            try {
                IPlayer? player = context.Player;
                Vec3i blockPosition = blockSel.Position.ToLocalPosition(Main.API);
                Main.Database.AddBlockLog(player?.PlayerName, player?.PlayerUID, "USED", __state.BeforeBlockName ?? context.BeforeBlock.ToString(), "CHISEL", blockPosition.X, blockPosition.Y, blockPosition.Z, __state.BeforeBlockId);
            }
            catch {
                // Legacy auditing is observational and must never fail the authoritative chisel action.
            }
        }

        if (__state.ExactStarted) {
            RollbackSeams.EmitChiselConversionCompleted(context, converted ? RollbackMutationOutcome.Changed : RollbackMutationOutcome.NoChange);
        }
    }

    public static Exception? Finalizer(Exception? __exception, ItemChiselPatchState? __state) {
        if (__exception != null && __state?.ExactStarted == true && __state.Context != null) {
            RollbackSeams.EmitChiselConversionCompleted(__state.Context, RollbackMutationOutcome.Failed, "mutation-threw");
        }
        return __exception;
    }

    private static ItemChiselPatchState CreateAuditState(ChiselConversionSeamContext context, bool exactStarted) {
        return new ItemChiselPatchState {
            Context = context,
            ExactStarted = exactStarted,
            BeforeBlockId = context.BeforeBlock?.Id ?? 0,
            BeforeBlockName = context.BeforeBlock?.ToString()
        };
    }

    private static bool IsChiseledBlock(Block? block) {
        if (block is BlockChisel) return true;
        AssetLocation? code = block?.Code;
        return code != null && code.Domain == "game" && code.Path == "chiseledblock";
    }
}

internal sealed class AuthoritativeChiselPacketScope {
    public AuthoritativeChiselPacketScope? Previous { get; init; }
    public BlockEntityChisel? ChiselEntity { get; set; }
    public IPlayer? Player { get; set; }
    public bool Restored { get; set; }
}

internal static class AuthoritativeChiselPacketPatch {
    [ThreadStatic]
    private static AuthoritativeChiselPacketScope? current;

    internal static AuthoritativeChiselPacketScope? Current => current;

    public static void Prefix(BlockEntityChisel __instance, IPlayer player, int packetid, out AuthoritativeChiselPacketScope __state) {
        __state = new AuthoritativeChiselPacketScope { Previous = current };
        if (Main.ExactRollbackAvailable && packetid == 1010 && __instance.Api?.Side == EnumAppSide.Server) {
            __state.ChiselEntity = __instance;
            __state.Player = player;
            current = __state;
        }
    }

    public static void Postfix(AuthoritativeChiselPacketScope __state) {
        Restore(__state);
    }

    public static Exception? Finalizer(Exception? __exception, AuthoritativeChiselPacketScope __state) {
        Restore(__state);
        return __exception;
    }

    private static void Restore(AuthoritativeChiselPacketScope state) {
        if (state.Restored) return;
        state.Restored = true;
        current = state.Previous;
    }
}

internal static class BlockEntityChiselUpdateVoxelPatch {
    public static void Prefix(BlockEntityChisel __instance, IPlayer byPlayer, Vec3i voxelPos, BlockFacing facing, bool isBreak, out ChiselVoxelSeamContext? __state) {
        __state = null;
        AuthoritativeChiselPacketScope? scope = AuthoritativeChiselPacketPatch.Current;
        if (!Main.ExactRollbackAvailable || scope == null || !ReferenceEquals(scope.ChiselEntity, __instance) || !SamePlayer(scope.Player, byPlayer)) return;

        try {
            __state = new ChiselVoxelSeamContext(__instance, byPlayer, voxelPos, facing, isBreak);
            RollbackSeams.EmitChiselVoxelStarting(__state);
            try {
                __state.BeforeFingerprint = Fingerprint(__instance);
            }
            catch {
                RollbackSeams.EmitChiselVoxelCompleted(__state, RollbackMutationOutcome.Failed, "before-fingerprint");
            }
        }
        catch {
            // Failure before the typed context exists must not affect the authoritative mutation.
        }
    }

    public static void Postfix(BlockEntityChisel __instance, ChiselVoxelSeamContext? __state) {
        if (__state == null || __state.Outcome != RollbackMutationOutcome.Pending) return;

        try {
            __state.AfterFingerprint = Fingerprint(__instance);
            if (__state.BeforeFingerprint == null) {
                RollbackSeams.EmitChiselVoxelCompleted(__state, RollbackMutationOutcome.Failed, "capture-incomplete");
                return;
            }
            RollbackSeams.EmitChiselVoxelCompleted(
                __state,
                __state.AfterFingerprint == __state.BeforeFingerprint ? RollbackMutationOutcome.NoChange : RollbackMutationOutcome.Changed
            );
        }
        catch {
            RollbackSeams.EmitChiselVoxelCompleted(__state, RollbackMutationOutcome.Failed, "after-fingerprint");
        }
    }

    public static Exception? Finalizer(Exception? __exception, ChiselVoxelSeamContext? __state) {
        if (__exception != null && __state != null) {
            RollbackSeams.EmitChiselVoxelCompleted(__state, RollbackMutationOutcome.Failed, "mutation-threw");
        }
        return __exception;
    }

    private static bool SamePlayer(IPlayer? left, IPlayer? right) {
        return ReferenceEquals(left, right) || (left != null && right != null && left.PlayerUID == right.PlayerUID);
    }

    private static ulong Fingerprint(BlockEntityChisel chisel) {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;

        unchecked {
            hash = (hash ^ (uint)(chisel.Api?.World.BlockAccessor.GetBlock(chisel.Pos).Id ?? -1)) * prime;
            if (chisel.BlockIds != null) {
                hash = (hash ^ (uint)chisel.BlockIds.Length) * prime;
                foreach (int blockId in chisel.BlockIds) hash = (hash ^ (uint)blockId) * prime;
            }
            if (chisel.VoxelCuboids != null) {
                hash = (hash ^ (uint)chisel.VoxelCuboids.Count) * prime;
                foreach (uint cuboid in chisel.VoxelCuboids) hash = (hash ^ cuboid) * prime;
            }
            if (chisel.AvailMaterialQuantities != null) {
                hash = (hash ^ (uint)chisel.AvailMaterialQuantities.Length) * prime;
                foreach (ushort quantity in chisel.AvailMaterialQuantities) hash = (hash ^ quantity) * prime;
            }
        }
        return hash;
    }
}
