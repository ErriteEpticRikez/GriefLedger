using System;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace GriefLedger.Rollback;

public enum RollbackMutationOutcome {
    Pending,
    Cancelled,
    NoChange,
    Changed,
    Failed
}

internal sealed class SeamCompletionGate {
    private int completed;

    public bool TryComplete() {
        return Interlocked.CompareExchange(ref completed, 1, 0) == 0;
    }
}

public sealed class PlayerPlacementSeamContext {
    private readonly SeamCompletionGate completionGate = new();

    internal PlayerPlacementSeamContext(IPlayer player, IWorldAccessor world, BlockPos position, int blockLayer, Block? beforeBlock) {
        Player = player;
        World = world;
        Position = position.Copy();
        BlockLayer = blockLayer;
        BeforeBlock = beforeBlock;
    }

    public IPlayer Player { get; }
    public IWorldAccessor World { get; }
    public BlockPos Position { get; }
    public int BlockLayer { get; }
    public Block? BeforeBlock { get; internal set; }
    public Block? AfterBlock { get; internal set; }
    public RollbackMutationOutcome Outcome { get; private set; } = RollbackMutationOutcome.Pending;
    public string? FailureCode { get; private set; }

    internal bool TryComplete(RollbackMutationOutcome outcome, string? failureCode = null) {
        if (outcome == RollbackMutationOutcome.Pending || !completionGate.TryComplete()) return false;
        Outcome = outcome;
        FailureCode = failureCode;
        return true;
    }
}

public sealed class PlayerBreakSeamContext {
    private readonly SeamCompletionGate completionGate = new();

    internal PlayerBreakSeamContext(IPlayer player, IWorldAccessor world, BlockPos position, int? blockLayer, Block? beforeBlock) {
        Player = player;
        World = world;
        Position = position.Copy();
        BlockLayer = blockLayer;
        BeforeBlock = beforeBlock;
    }

    public IPlayer Player { get; }
    public IWorldAccessor World { get; }
    public BlockPos Position { get; }
    public int? BlockLayer { get; internal set; }
    public Block? BeforeBlock { get; internal set; }
    public Block? AfterBlock { get; internal set; }
    public RollbackMutationOutcome Outcome { get; private set; } = RollbackMutationOutcome.Pending;
    public string? FailureCode { get; private set; }
    public bool ConfirmedByDidBreak { get; internal set; }
    public bool ConfirmationMatchesCapture { get; internal set; }
    public int? ConfirmedOldBlockId { get; internal set; }

    internal bool TryComplete(RollbackMutationOutcome outcome, string? failureCode = null) {
        if (outcome == RollbackMutationOutcome.Pending || !completionGate.TryComplete()) return false;
        Outcome = outcome;
        FailureCode = failureCode;
        return true;
    }
}

public sealed class ChiselConversionSeamContext {
    private readonly SeamCompletionGate completionGate = new();

    internal ChiselConversionSeamContext(IPlayer? player, IWorldAccessor world, BlockPos position, Block? beforeBlock) {
        Player = player;
        World = world;
        Position = position.Copy();
        BeforeBlock = beforeBlock;
    }

    public IPlayer? Player { get; }
    public IWorldAccessor World { get; }
    public BlockPos Position { get; }
    public Block? BeforeBlock { get; internal set; }
    public Block? AfterBlock { get; internal set; }
    public BlockEntityChisel? ChiselEntity { get; internal set; }
    public RollbackMutationOutcome Outcome { get; private set; } = RollbackMutationOutcome.Pending;
    public string? FailureCode { get; private set; }

    internal bool TryComplete(RollbackMutationOutcome outcome, string? failureCode = null) {
        if (outcome == RollbackMutationOutcome.Pending || !completionGate.TryComplete()) return false;
        Outcome = outcome;
        FailureCode = failureCode;
        return true;
    }
}

public sealed class ChiselVoxelSeamContext {
    private readonly SeamCompletionGate completionGate = new();

    internal ChiselVoxelSeamContext(BlockEntityChisel chiselEntity, IPlayer player, Vec3i voxelPosition, BlockFacing facing, bool isBreak) {
        ChiselEntity = chiselEntity;
        Player = player;
        Position = chiselEntity.Pos.Copy();
        VoxelPosition = voxelPosition.Clone();
        Facing = facing;
        IsBreak = isBreak;
    }

    public BlockEntityChisel ChiselEntity { get; }
    public IPlayer Player { get; }
    public BlockPos Position { get; }
    public Vec3i VoxelPosition { get; }
    public BlockFacing Facing { get; }
    public bool IsBreak { get; }
    public ulong? BeforeFingerprint { get; internal set; }
    public ulong? AfterFingerprint { get; internal set; }
    public RollbackMutationOutcome Outcome { get; private set; } = RollbackMutationOutcome.Pending;
    public string? FailureCode { get; private set; }

    internal bool TryComplete(RollbackMutationOutcome outcome, string? failureCode = null) {
        if (outcome == RollbackMutationOutcome.Pending || !completionGate.TryComplete()) return false;
        Outcome = outcome;
        FailureCode = failureCode;
        return true;
    }
}

public readonly record struct RollbackSeamDiagnostics(
    long PlacementStarts,
    long PlacementCompletions,
    long BreakStarts,
    long BreakCompletions,
    long BreakConfirmationMisses,
    long ChiselConversionStarts,
    long ChiselConversionCompletions,
    long ChiselVoxelStarts,
    long ChiselVoxelCompletions
);

public static class RollbackSeams {
    private static long placementStarts;
    private static long placementCompletions;
    private static long breakStarts;
    private static long breakCompletions;
    private static long breakConfirmationMisses;
    private static long chiselConversionStarts;
    private static long chiselConversionCompletions;
    private static long chiselVoxelStarts;
    private static long chiselVoxelCompletions;

    public static event Action<PlayerPlacementSeamContext>? PlayerPlacementStarting;
    public static event Action<PlayerPlacementSeamContext>? PlayerPlacementCompleted;
    public static event Action<PlayerBreakSeamContext>? PlayerBreakStarting;
    public static event Action<PlayerBreakSeamContext>? PlayerBreakCompleted;
    public static event Action<ChiselConversionSeamContext>? ChiselConversionStarting;
    public static event Action<ChiselConversionSeamContext>? ChiselConversionCompleted;
    public static event Action<ChiselVoxelSeamContext>? ChiselVoxelStarting;
    public static event Action<ChiselVoxelSeamContext>? ChiselVoxelCompleted;

    public static RollbackSeamDiagnostics GetDiagnostics() {
        return new RollbackSeamDiagnostics(
            Interlocked.Read(ref placementStarts),
            Interlocked.Read(ref placementCompletions),
            Interlocked.Read(ref breakStarts),
            Interlocked.Read(ref breakCompletions),
            Interlocked.Read(ref breakConfirmationMisses),
            Interlocked.Read(ref chiselConversionStarts),
            Interlocked.Read(ref chiselConversionCompletions),
            Interlocked.Read(ref chiselVoxelStarts),
            Interlocked.Read(ref chiselVoxelCompletions)
        );
    }

    internal static void EmitPlacementStarting(PlayerPlacementSeamContext context) {
        Interlocked.Increment(ref placementStarts);
        Dispatch(PlayerPlacementStarting, context, nameof(PlayerPlacementStarting));
    }

    internal static bool EmitPlacementCompleted(PlayerPlacementSeamContext context, RollbackMutationOutcome outcome, string? failureCode = null) {
        if (!context.TryComplete(outcome, failureCode)) return false;
        Interlocked.Increment(ref placementCompletions);
        Dispatch(PlayerPlacementCompleted, context, nameof(PlayerPlacementCompleted));
        return true;
    }

    internal static void EmitBreakStarting(PlayerBreakSeamContext context) {
        Interlocked.Increment(ref breakStarts);
        Dispatch(PlayerBreakStarting, context, nameof(PlayerBreakStarting));
    }

    internal static bool EmitBreakCompleted(PlayerBreakSeamContext context, RollbackMutationOutcome outcome, string? failureCode = null) {
        if (!context.TryComplete(outcome, failureCode)) return false;
        Interlocked.Increment(ref breakCompletions);
        Dispatch(PlayerBreakCompleted, context, nameof(PlayerBreakCompleted));
        return true;
    }

    internal static void RecordBreakConfirmationMiss() {
        Interlocked.Increment(ref breakConfirmationMisses);
    }

    internal static void EmitChiselConversionStarting(ChiselConversionSeamContext context) {
        Interlocked.Increment(ref chiselConversionStarts);
        Dispatch(ChiselConversionStarting, context, nameof(ChiselConversionStarting));
    }

    internal static bool EmitChiselConversionCompleted(ChiselConversionSeamContext context, RollbackMutationOutcome outcome, string? failureCode = null) {
        if (!context.TryComplete(outcome, failureCode)) return false;
        Interlocked.Increment(ref chiselConversionCompletions);
        Dispatch(ChiselConversionCompleted, context, nameof(ChiselConversionCompleted));
        return true;
    }

    internal static void EmitChiselVoxelStarting(ChiselVoxelSeamContext context) {
        Interlocked.Increment(ref chiselVoxelStarts);
        Dispatch(ChiselVoxelStarting, context, nameof(ChiselVoxelStarting));
    }

    internal static bool EmitChiselVoxelCompleted(ChiselVoxelSeamContext context, RollbackMutationOutcome outcome, string? failureCode = null) {
        if (!context.TryComplete(outcome, failureCode)) return false;
        Interlocked.Increment(ref chiselVoxelCompletions);
        Dispatch(ChiselVoxelCompleted, context, nameof(ChiselVoxelCompleted));
        return true;
    }

    private static void Dispatch<T>(Action<T>? handlers, T context, string seamName) {
        if (handlers == null) return;

        foreach (Action<T> handler in handlers.GetInvocationList()) {
            try {
                handler(context);
            }
            catch (Exception exception) {
                Main.API?.Logger.Error("GriefLedger: Exact rollback seam subscriber failed in {0}: {1}", seamName, exception);
            }
        }
    }
}
