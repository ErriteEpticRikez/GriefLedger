using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace GriefLedger.Rollback;

/// <summary>An absolute world coordinate used to detect mutations after a rollback plan is made.</summary>
public readonly record struct BlockMutationCoordinate(int Dimension, int X, int Y, int Z);

/// <summary>
/// Captures exact, allowlisted block mutations on the server main thread. The service owns its
/// seam subscriptions and releases them on dispose so a mod reload cannot retain an old Main.
/// </summary>
public sealed class BlockMutationCapture : IDisposable {
    private const string SurvivalAssemblyName = "VSSurvivalMod";
    private readonly System.Func<BlockMutationAppend, Task<long>> append;
    private readonly System.Func<long> utcTimestamp;
    private readonly Action<string, Exception?> logFailure;
    private readonly ConcurrentDictionary<BlockMutationCoordinate, long> generations = new();
    private readonly ConditionalWeakTable<object, CapturedBefore> pending = new();
    private int subscribed;
    private int disposed;
    private int suppressionCount;

    internal BlockMutationCapture(
        System.Func<BlockMutationAppend, Task<long>> append,
        System.Func<long> utcTimestamp,
        Action<string, Exception?> logFailure
    ) {
        this.append = append ?? throw new ArgumentNullException(nameof(append));
        this.utcTimestamp = utcTimestamp ?? throw new ArgumentNullException(nameof(utcTimestamp));
        this.logFailure = logFailure ?? throw new ArgumentNullException(nameof(logFailure));
    }

    internal static BlockMutationCapture Attach(ICoreServerAPI api, Database database) {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(database);
        ILogger logger = api.Logger;
        var capture = new BlockMutationCapture(
            database.EnqueueBlockMutationAppend,
            () => DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            (message, exception) => {
                if (exception == null) logger.Error("GriefLedger: {0}", message);
                else logger.Error("GriefLedger: {0}: {1}", message, exception);
            }
        );
        capture.Subscribe();
        return capture;
    }

    /// <summary>Returns the current observed player-mutation generation for an absolute coordinate.</summary>
    public long GetGeneration(int dimension, int x, int y, int z) {
        return GetGeneration(new BlockMutationCoordinate(dimension, x, y, z));
    }

    /// <summary>Returns the current observed player-mutation generation for an absolute coordinate.</summary>
    public long GetGeneration(BlockMutationCoordinate coordinate) {
        return generations.TryGetValue(coordinate, out long generation) ? generation : 0;
    }

    /// <summary>
    /// Suppresses new seam paths for the lifetime of the returned token. Tokens are nestable and
    /// thread-safe; future replay code should hold one only around its main-thread world writes.
    /// </summary>
    public IDisposable BeginReplaySuppression() {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Interlocked.Increment(ref suppressionCount);
        return new SuppressionToken(this);
    }

    /// <summary>Alias for <see cref="BeginReplaySuppression"/>.</summary>
    public IDisposable Suppress() => BeginReplaySuppression();

    internal bool IsSuppressed => Volatile.Read(ref suppressionCount) != 0;

    internal void Subscribe() {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.CompareExchange(ref subscribed, 1, 0) != 0) return;

        RollbackSeams.PlayerPlacementStarting += OnPlacementStarting;
        RollbackSeams.PlayerPlacementCompleted += OnPlacementCompleted;
        RollbackSeams.PlayerBreakStarting += OnBreakStarting;
        RollbackSeams.PlayerBreakCompleted += OnBreakCompleted;
        RollbackSeams.ChiselConversionStarting += OnChiselConversionStarting;
        RollbackSeams.ChiselConversionCompleted += OnChiselConversionCompleted;
        RollbackSeams.ChiselVoxelStarting += OnChiselVoxelStarting;
        RollbackSeams.ChiselVoxelCompleted += OnChiselVoxelCompleted;
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (Interlocked.Exchange(ref subscribed, 0) == 0) return;

        RollbackSeams.PlayerPlacementStarting -= OnPlacementStarting;
        RollbackSeams.PlayerPlacementCompleted -= OnPlacementCompleted;
        RollbackSeams.PlayerBreakStarting -= OnBreakStarting;
        RollbackSeams.PlayerBreakCompleted -= OnBreakCompleted;
        RollbackSeams.ChiselConversionStarting -= OnChiselConversionStarting;
        RollbackSeams.ChiselConversionCompleted -= OnChiselConversionCompleted;
        RollbackSeams.ChiselVoxelStarting -= OnChiselVoxelStarting;
        RollbackSeams.ChiselVoxelCompleted -= OnChiselVoxelCompleted;
    }

    private void OnPlacementStarting(PlayerPlacementSeamContext context) {
        CaptureBefore(context, context.World, context.Position);
    }

    private void OnPlacementCompleted(PlayerPlacementSeamContext context) {
        Complete(context, context.World, context.Position, context.Player, context.Outcome, BlockMutationActionKind.Place);
    }

    private void OnBreakStarting(PlayerBreakSeamContext context) {
        CaptureBefore(context, context.World, context.Position);
    }

    private void OnBreakCompleted(PlayerBreakSeamContext context) {
        if (context.Outcome == RollbackMutationOutcome.Changed
            && (!context.ConfirmedByDidBreak || !context.ConfirmationMatchesCapture)) {
            pending.Remove(context);
            return;
        }
        Complete(context, context.World, context.Position, context.Player, context.Outcome, BlockMutationActionKind.Break);
    }

    private void OnChiselConversionStarting(ChiselConversionSeamContext context) {
        CaptureBefore(context, context.World, context.Position);
    }

    private void OnChiselConversionCompleted(ChiselConversionSeamContext context) {
        Complete(context, context.World, context.Position, context.Player, context.Outcome, BlockMutationActionKind.ChiselConversion);
    }

    private void OnChiselVoxelStarting(ChiselVoxelSeamContext context) {
        IWorldAccessor? world = context.ChiselEntity.Api?.World;
        if (world != null) CaptureBefore(context, world, context.Position);
    }

    private void OnChiselVoxelCompleted(ChiselVoxelSeamContext context) {
        IWorldAccessor? world = context.ChiselEntity.Api?.World;
        if (world == null) {
            pending.Remove(context);
            return;
        }
        Complete(context, world, context.Position, context.Player, context.Outcome, BlockMutationActionKind.ChiselVoxel);
    }

    private void CaptureBefore(object context, IWorldAccessor world, BlockPos position) {
        if (IsSuppressed || Volatile.Read(ref disposed) != 0) return;
        if (!TryCaptureState(world, position, out EnvelopeBlockState state)) return;
        try {
            pending.Add(context, new CapturedBefore(state));
        }
        catch (ArgumentException) {
            // Duplicate speculative starts fail closed and must not affect the mutation.
        }
    }

    private void Complete(
        object context,
        IWorldAccessor world,
        BlockPos position,
        IPlayer? player,
        RollbackMutationOutcome outcome,
        BlockMutationActionKind actionKind
    ) {
        if (IsSuppressed || Volatile.Read(ref disposed) != 0) {
            pending.Remove(context);
            return;
        }
        if (outcome != RollbackMutationOutcome.Changed) {
            pending.Remove(context);
            return;
        }

        BlockMutationCoordinate coordinate = Coordinate(position);
        if (player != null) generations.AddOrUpdate(coordinate, 1, static (_, generation) => checked(generation + 1));

        if (!pending.TryGetValue(context, out CapturedBefore? capturedBefore) || capturedBefore == null) return;
        pending.Remove(context);
        if (!TryCaptureState(world, position, out EnvelopeBlockState after)) return;

        try {
            (string? playerName, string? playerUid) = ReadIdentity(player);
            var envelope = new BlockStateEnvelope(capturedBefore.State, after);
            var request = new BlockMutationAppend(
                utcTimestamp(),
                playerName,
                playerUid,
                BlockMutationEntryKind.Mutation,
                actionKind,
                coordinate.Dimension,
                coordinate.X,
                coordinate.Y,
                coordinate.Z,
                envelope
            );
            ObserveAppend(append(request), actionKind, coordinate);
        }
        catch (Exception exception) {
            LogOnce("Exact block mutation append could not be queued", exception);
        }
    }

    private void ObserveAppend(Task<long> task, BlockMutationActionKind actionKind, BlockMutationCoordinate coordinate) {
        if (task == null) {
            LogOnce("Exact block mutation append returned no task", null);
            return;
        }

        var observer = new AppendTaskObserver(
            logFailure,
            $"Exact {actionKind} append failed at dimension {coordinate.Dimension}, {coordinate.X}, {coordinate.Y}, {coordinate.Z}",
            $"Exact {actionKind} append was cancelled at dimension {coordinate.Dimension}, {coordinate.X}, {coordinate.Y}, {coordinate.Z}"
        );
        _ = task.ContinueWith(
            static (completed, state) => ((AppendTaskObserver)state!).Observe(completed),
            observer,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private void LogOnce(string message, Exception? exception) {
        try {
            logFailure(message, exception);
        }
        catch {
            // Logging must not create an unobserved continuation or affect gameplay.
        }
    }

    private static BlockMutationCoordinate Coordinate(BlockPos position) {
        return new BlockMutationCoordinate(position.dimension, position.X, position.Y, position.Z);
    }

    private static (string? Name, string? Uid) ReadIdentity(IPlayer? player) {
        if (player == null) return (null, null);
        string? name = null;
        string? uid = null;
        try { name = player.PlayerName; } catch { }
        try { uid = player.PlayerUID; } catch { }
        return (name, uid);
    }

    internal static bool TryCaptureState(IWorldAccessor world, BlockPos position, out EnvelopeBlockState state) {
        state = null!;
        try {
            IBlockAccessor accessor = world.BlockAccessor;
            Block block = accessor.GetBlock(position, 1);
            Block fluid = accessor.GetBlock(position, 2);
            if (!IsExplicitAir(fluid)) return false;
            // Empty is the API's other representation of no decor; only actual entries are state.
            if (accessor.GetSubDecors(position)?.Count > 0) return false;

            BlockEntity? blockEntity = accessor.GetBlockEntity(position);
            if (IsExplicitAir(block)) {
                if (blockEntity != null) return false;
                state = EnvelopeBlockState.Air();
                return true;
            }

            if (!TryGetAssetCode(block, out string assetCode)) return false;
            if (IsPlainSolidBlock(block)) {
                if (blockEntity != null) return false;
                state = EnvelopeBlockState.Asset(assetCode);
                return true;
            }

            if (!IsRecognizedMicroblockPair(block, blockEntity, position)) return false;
            if (blockEntity is not BlockEntityMicroBlock microblockEntity
                || !MicroblockTreeCodec.TryCapture(microblockEntity, world, position, assetCode, out byte[] treeBytes)) return false;
            state = EnvelopeBlockState.Asset(assetCode, treeBytes);
            return true;
        }
        catch {
            state = null!;
            return false;
        }
    }

    internal static bool IsExplicitAir(Block? block) {
        return block?.GetType() == typeof(Block)
            && block.Code?.Domain == "game"
            && block.Code.Path == "air";
    }

    internal static bool IsPlainSolidBlock(Block? block) {
        return block?.GetType() == typeof(Block)
            && TryGetAssetCode(block, out _)
            && block.SideSolid.All
            && !block.ForFluidsLayer
            && block.MatterState != EnumMatterState.Liquid
            && string.IsNullOrEmpty(block.LiquidCode)
            && string.IsNullOrEmpty(block.EntityClass)
            && (block.BlockEntityBehaviors == null || block.BlockEntityBehaviors.Length == 0);
    }

    internal static bool IsRecognizedMicroblockPair(Block? block, BlockEntity? blockEntity, BlockPos position) {
        if (block == null || blockEntity == null || block.GetType().Assembly.GetName().Name != SurvivalAssemblyName
            || blockEntity.GetType().Assembly.GetName().Name != SurvivalAssemblyName) return false;
        if (block.Code?.Domain != "game" || blockEntity.Pos == null
            || blockEntity.Pos.X != position.X || blockEntity.Pos.Y != position.Y
            || blockEntity.Pos.Z != position.Z || blockEntity.Pos.dimension != position.dimension) return false;

        Type blockType = block.GetType();
        Type entityType = blockEntity.GetType();
        return blockType == typeof(BlockChisel)
            && entityType == typeof(BlockEntityChisel)
            && block.Code.Path is "chiseledblock" or "chiseledblock-snow"
            || blockType == typeof(BlockMicroBlock)
            && entityType == typeof(BlockEntityMicroBlock)
            && block.Code.Path is "microblock" or "microblock-snow";
    }

    private static bool TryGetAssetCode(Block? block, out string assetCode) {
        assetCode = null!;
        AssetLocation? code = block?.Code;
        if (code == null || string.IsNullOrWhiteSpace(code.Domain) || string.IsNullOrWhiteSpace(code.Path)
            || code.Domain.Contains(':') || code.Path.Contains(':')) return false;
        assetCode = code.Domain + ":" + code.Path;
        return true;
    }

    private void ReleaseSuppression() {
        int value = Interlocked.Decrement(ref suppressionCount);
        if (value < 0) {
            Interlocked.Exchange(ref suppressionCount, 0);
            throw new InvalidOperationException("The block mutation capture suppression counter underflowed.");
        }
    }

    private sealed class CapturedBefore {
        internal CapturedBefore(EnvelopeBlockState state) {
            State = state;
        }

        internal EnvelopeBlockState State { get; }
    }

    private sealed class AppendTaskObserver {
        private readonly Action<string, Exception?> log;
        private readonly string failedMessage;
        private readonly string cancelledMessage;

        internal AppendTaskObserver(Action<string, Exception?> log, string failedMessage, string cancelledMessage) {
            this.log = log;
            this.failedMessage = failedMessage;
            this.cancelledMessage = cancelledMessage;
        }

        internal void Observe(Task<long> completed) {
            try {
                if (completed.IsFaulted) {
                    Exception exception = completed.Exception?.GetBaseException()
                        ?? new InvalidOperationException("The database append task faulted without an exception.");
                    log(failedMessage, exception);
                }
                else if (completed.IsCanceled) {
                    log(cancelledMessage, null);
                }
            }
            catch {
                // The task exception was already observed; logging is best-effort.
            }
        }
    }

    private sealed class SuppressionToken : IDisposable {
        private BlockMutationCapture? owner;

        internal SuppressionToken(BlockMutationCapture owner) {
            this.owner = owner;
        }

        public void Dispose() {
            Interlocked.Exchange(ref owner, null)?.ReleaseSuppression();
        }
    }
}
