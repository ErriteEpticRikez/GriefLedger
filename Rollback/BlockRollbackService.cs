using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace GriefLedger.Rollback;

public sealed record BlockRollbackRequest {
    public required string OperatorPlayerUid { get; init; }
    public string? OperatorPlayerName { get; init; }
    public required string TargetPlayerUid { get; init; }
    public int Dimension { get; init; }
    public int CenterX { get; init; }
    public int CenterY { get; init; }
    public int CenterZ { get; init; }
    public int Radius { get; init; }
    public bool BreakOnly { get; init; }

    internal void Validate() {
        if (string.IsNullOrWhiteSpace(OperatorPlayerUid)) throw new ArgumentException("A nonempty immutable operator UID is required.", nameof(OperatorPlayerUid));
        if (string.IsNullOrWhiteSpace(TargetPlayerUid)) throw new ArgumentException("A nonempty immutable target UID is required.", nameof(TargetPlayerUid));
        if (Radius is < 0 or > BlockRollbackLimits.MaximumRadius) throw new ArgumentOutOfRangeException(nameof(Radius));
    }
}

public sealed record BlockRollbackAttemptResult(
    long SourceMutationId,
    BlockMutationRollbackOutcome Outcome,
    string? FailureCode,
    long? RollbackLedgerId
);

public sealed class BlockRollbackResult {
    internal BlockRollbackResult(long cutoffId, long historyThroughId, string? operationFailureCode,
        IReadOnlyList<BlockRollbackAttemptResult> attempts) {
        CutoffId = cutoffId;
        HistoryThroughId = historyThroughId;
        OperationFailureCode = operationFailureCode;
        Attempts = attempts;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (BlockRollbackAttemptResult attempt in attempts) {
            string key = attempt.FailureCode ?? "succeeded";
            counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
        }
        ReasonCounts = new ReadOnlyDictionary<string, int>(counts);
        SucceededSourceIds = attempts.Where(value => value.Outcome == BlockMutationRollbackOutcome.Succeeded)
            .Select(value => value.SourceMutationId).ToArray();
        FailedSourceIds = attempts.Where(value => value.Outcome == BlockMutationRollbackOutcome.Failed)
            .Select(value => value.SourceMutationId).ToArray();
        SkippedSourceIds = attempts.Where(value => value.Outcome == BlockMutationRollbackOutcome.Skipped)
            .Select(value => value.SourceMutationId).ToArray();
    }

    public long CutoffId { get; }
    public long HistoryThroughId { get; }
    public string? OperationFailureCode { get; }
    public IReadOnlyList<BlockRollbackAttemptResult> Attempts { get; }
    public IReadOnlyDictionary<string, int> ReasonCounts { get; }
    public IReadOnlyList<long> SucceededSourceIds { get; }
    public IReadOnlyList<long> FailedSourceIds { get; }
    public IReadOnlyList<long> SkippedSourceIds { get; }
}

public sealed class BlockRollbackOperationalException : Exception {
    internal BlockRollbackOperationalException(string message, BlockRollbackResult partialResult, Exception innerException)
        : base(message, innerException) {
        PartialResult = partialResult;
    }

    public BlockRollbackResult PartialResult { get; }
}

/// <summary>
/// Guarded reverse replay for the exact ledger. No command is registered here; callers provide
/// immutable UIDs and await a complete durable result.
/// </summary>
public sealed class BlockRollbackService : IDisposable {
    private static readonly SemaphoreSlim OperationGate = new(1, 1);
    private readonly ICoreServerAPI? api;
    private readonly BlockMutationCapture? capture;
    private readonly System.Func<Task<long>> getDurableCutoffAsync;
    private readonly System.Func<string, CancellationToken, Task<BlockMutationPlayer?>> resolvePlayerByUidAsync;
    private readonly System.Func<BlockMutationTargetQuery, CancellationToken,
        Task<IReadOnlyList<BlockMutationLogRow>>> readTargetMutationsAsync;
    private readonly System.Func<BlockMutationHistoryQuery, CancellationToken,
        Task<IReadOnlyList<BlockMutationLogRow>>> readHistoryAsync;
    private readonly System.Func<BlockMutationAppend, Task<long>> enqueueAppendAsync;
    private readonly System.Func<BlockMutationCoordinate, long> getCaptureGeneration;
    private readonly System.Action<Action, string> enqueueMainThreadTask;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly object lifecycleLock = new();
    private int disposed;

    public BlockRollbackService(ICoreServerAPI api, Database database, BlockMutationCapture capture) {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        ArgumentNullException.ThrowIfNull(database);
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        getDurableCutoffAsync = database.GetDurableBlockMutationCutoffAsync;
        resolvePlayerByUidAsync = database.ResolveBlockMutationPlayerByUidAsync;
        readTargetMutationsAsync = database.ReadTargetBlockMutationsAsync;
        readHistoryAsync = database.ReadBlockMutationHistoryAsync;
        enqueueAppendAsync = database.EnqueueBlockMutationAppendAsync;
        getCaptureGeneration = capture.GetGeneration;
        enqueueMainThreadTask = (action, code) => api.Event.EnqueueMainThreadTask(action, code);
    }

    internal BlockRollbackService(
        System.Func<Task<long>> getDurableCutoffAsync,
        System.Func<string, CancellationToken, Task<BlockMutationPlayer?>> resolvePlayerByUidAsync,
        System.Func<BlockMutationTargetQuery, CancellationToken, Task<IReadOnlyList<BlockMutationLogRow>>> readTargetMutationsAsync,
        System.Func<BlockMutationHistoryQuery, CancellationToken, Task<IReadOnlyList<BlockMutationLogRow>>> readHistoryAsync,
        System.Func<BlockMutationAppend, Task<long>> enqueueAppendAsync,
        System.Func<BlockMutationCoordinate, long> getCaptureGeneration,
        System.Action<Action, string> enqueueMainThreadTask) {
        this.getDurableCutoffAsync = getDurableCutoffAsync ?? throw new ArgumentNullException(nameof(getDurableCutoffAsync));
        this.resolvePlayerByUidAsync = resolvePlayerByUidAsync ?? throw new ArgumentNullException(nameof(resolvePlayerByUidAsync));
        this.readTargetMutationsAsync = readTargetMutationsAsync ?? throw new ArgumentNullException(nameof(readTargetMutationsAsync));
        this.readHistoryAsync = readHistoryAsync ?? throw new ArgumentNullException(nameof(readHistoryAsync));
        this.enqueueAppendAsync = enqueueAppendAsync ?? throw new ArgumentNullException(nameof(enqueueAppendAsync));
        this.getCaptureGeneration = getCaptureGeneration ?? throw new ArgumentNullException(nameof(getCaptureGeneration));
        this.enqueueMainThreadTask = enqueueMainThreadTask ?? throw new ArgumentNullException(nameof(enqueueMainThreadTask));
    }

    public async Task<BlockRollbackResult> RollbackAsync(BlockRollbackRequest request,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, lifetimeCancellation.Token);
        bool acquired = false;
        try {
            await OperationGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
            acquired = true;
            operationCancellation.Token.ThrowIfCancellationRequested();
            return await ExecuteAsync(request, operationCancellation.Token).ConfigureAwait(false);
        }
        finally {
            if (acquired) OperationGate.Release();
        }
    }

    public Task<BlockRollbackResult> RequestAsync(BlockRollbackRequest request,
        CancellationToken cancellationToken = default) => RollbackAsync(request, cancellationToken);

    private async Task<BlockRollbackResult> ExecuteAsync(BlockRollbackRequest request,
        CancellationToken cancellationToken) {
        // This FIFO barrier is deliberately the first operation of every request.
        long cutoff = await getDurableCutoffAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        BlockMutationPlayer? target = await resolvePlayerByUidAsync(
            request.TargetPlayerUid, cancellationToken).ConfigureAwait(false);
        if (target == null) return new BlockRollbackResult(cutoff, cutoff,
            BlockRollbackFailureCodes.TargetPlayerNotFound, Array.Empty<BlockRollbackAttemptResult>());

        IReadOnlyList<BlockMutationLogRow> candidates = await readTargetMutationsAsync(
            new BlockMutationTargetQuery {
                PlayerId = target.Id,
                Dimension = request.Dimension,
                CenterX = request.CenterX,
                CenterY = request.CenterY,
                CenterZ = request.CenterZ,
                Radius = request.Radius,
                BreakOnly = request.BreakOnly,
                CutoffId = cutoff
            }, cancellationToken).ConfigureAwait(false);
        if (candidates.Count > BlockRollbackLimits.MaximumCandidates) {
            throw new BlockRollbackLimitExceededException("candidate count", BlockRollbackLimits.MaximumCandidates);
        }
        if (candidates.Count == 0) return new BlockRollbackResult(cutoff, cutoff, null, Array.Empty<BlockRollbackAttemptResult>());

        BlockMutationCoordinate[] coordinates = candidates.Select(row => row.Coordinate).Distinct().ToArray();
        if (coordinates.Length > BlockRollbackLimits.MaximumUniqueCoordinates) {
            throw new BlockRollbackLimitExceededException("unique coordinate count", BlockRollbackLimits.MaximumUniqueCoordinates);
        }
        IReadOnlyDictionary<BlockMutationCoordinate, long> plannedGenerations = await RunOnMainThreadAsync(() => {
            var values = new Dictionary<BlockMutationCoordinate, long>();
            foreach (BlockMutationCoordinate coordinate in coordinates) values[coordinate] = getCaptureGeneration(coordinate);
            return (IReadOnlyDictionary<BlockMutationCoordinate, long>)new ReadOnlyDictionary<BlockMutationCoordinate, long>(values);
        }, "griefledger-rollback-generation-snapshot", cancellationToken).ConfigureAwait(false);

        // Any capture completed before the main-thread snapshot queued its append first. This
        // second FIFO barrier therefore closes the post-selection/pre-snapshot ledger race.
        long historyThrough = await getDurableCutoffAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<BlockMutationLogRow> history = await readHistoryAsync(
            new BlockMutationHistoryQuery { Coordinates = coordinates, MaximumId = historyThrough }, cancellationToken).ConfigureAwait(false);
        if (history.Count > BlockRollbackLimits.MaximumHistoryRows) {
            throw new BlockRollbackLimitExceededException("history row count", BlockRollbackLimits.MaximumHistoryRows);
        }
        BlockRollbackPlan plan = BlockRollbackPlanner.Build(new BlockRollbackPlanningRequest {
            TargetPlayerUid = request.TargetPlayerUid,
            Dimension = request.Dimension,
            CenterX = request.CenterX,
            CenterY = request.CenterY,
            CenterZ = request.CenterZ,
            Radius = request.Radius,
            BreakOnly = request.BreakOnly,
            CutoffId = cutoff,
            HistoryThroughId = historyThrough
        }, candidates, history);

        var attempts = new List<BlockRollbackAttemptResult>(plan.Entries.Count);
        var blockedCoordinates = new HashSet<BlockMutationCoordinate>();
        foreach (BlockRollbackPlanEntry entry in plan.Entries) {
            cancellationToken.ThrowIfCancellationRequested();
            BlockMutationLogRow source = entry.Source;
            if (entry.Disposition == BlockRollbackPlanDisposition.Skip) {
                await AppendOutcomeAsync(request, source, BlockMutationRollbackOutcome.Skipped,
                    entry.FailureCode!, attempts, cutoff, historyThrough).ConfigureAwait(false);
                continue;
            }
            if (blockedCoordinates.Contains(source.Coordinate)) {
                await AppendOutcomeAsync(request, source, BlockMutationRollbackOutcome.Skipped,
                    BlockRollbackFailureCodes.FailedLaterUnwind, attempts, cutoff, historyThrough).ConfigureAwait(false);
                continue;
            }

            QueuedWorldAttempt attempt = await RunOnMainThreadAsync(() => {
                MainThreadApplyResult applied = TryApply(source, plannedGenerations[source.Coordinate]);
                BlockMutationRollbackOutcome outcome = applied.Succeeded
                    ? BlockMutationRollbackOutcome.Succeeded : BlockMutationRollbackOutcome.Failed;
                Task<long> auditTask;
                try {
                    // Queue the durable outcome before relinquishing the lifecycle lock. Main can
                    // then dispose the database after disposing this service without stranding a
                    // successful world write between the two lifetimes.
                    auditTask = QueueOutcomeAppend(request, source, outcome, applied.FailureCode);
                }
                catch (Exception exception) {
                    auditTask = Task.FromException<long>(exception);
                }
                return new QueuedWorldAttempt(applied, outcome, applied.FailureCode, auditTask);
            }, "griefledger-rollback-apply", cancellationToken).ConfigureAwait(false);
            await CompleteOutcomeAsync(source, attempt.Outcome, attempt.FailureCode, attempt.AuditTask,
                attempts, cutoff, historyThrough).ConfigureAwait(false);
            if (attempt.ApplyResult.Succeeded) continue;

            blockedCoordinates.Add(source.Coordinate);
            if (attempt.ApplyResult.StopBatch) break;
        }
        return new BlockRollbackResult(cutoff, historyThrough, null, attempts.AsReadOnly());
    }

    private Task<long> QueueOutcomeAppend(BlockRollbackRequest request, BlockMutationLogRow source,
        BlockMutationRollbackOutcome outcome, string? failureCode) {
        var inverse = new BlockStateEnvelope(source.Envelope.After, source.Envelope.Before);
        var append = new BlockMutationAppend(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, null,
            BlockMutationEntryKind.Rollback, BlockMutationActionKind.Rollback,
            source.Dimension, source.X, source.Y, source.Z, inverse,
            source.Id, outcome, failureCode, request.OperatorPlayerName, request.OperatorPlayerUid
        );
        return enqueueAppendAsync(append);
    }

    private async Task AppendOutcomeAsync(BlockRollbackRequest request, BlockMutationLogRow source,
        BlockMutationRollbackOutcome outcome, string? failureCode, List<BlockRollbackAttemptResult> attempts,
        long cutoff, long historyThrough) {
        Task<long> auditTask;
        try {
            auditTask = QueueOutcomeAppend(request, source, outcome, failureCode);
        }
        catch (Exception exception) {
            auditTask = Task.FromException<long>(exception);
        }
        await CompleteOutcomeAsync(source, outcome, failureCode, auditTask,
            attempts, cutoff, historyThrough).ConfigureAwait(false);
    }

    private static async Task CompleteOutcomeAsync(BlockMutationLogRow source,
        BlockMutationRollbackOutcome outcome, string? failureCode, Task<long> auditTask,
        List<BlockRollbackAttemptResult> attempts, long cutoff, long historyThrough) {
        try {
            // Deliberately no cancellation wait here. Once an outcome append is accepted—most
            // importantly after a successful world write—its durable result has priority.
            long rollbackId = await auditTask.ConfigureAwait(false);
            attempts.Add(new BlockRollbackAttemptResult(source.Id, outcome, failureCode, rollbackId));
        }
        catch (Exception exception) {
            BlockRollbackAttemptResult[] partialAttempts = attempts
                .Append(new BlockRollbackAttemptResult(source.Id, outcome, failureCode, null)).ToArray();
            var partial = new BlockRollbackResult(cutoff, historyThrough,
                BlockRollbackFailureCodes.OutcomeAppendFailed, partialAttempts);
            throw new BlockRollbackOperationalException(
                "A rollback world decision could not be recorded durably; replay stopped immediately.", partial, exception);
        }
    }

    private MainThreadApplyResult TryApply(BlockMutationLogRow source, long plannedGeneration) {
        ICoreServerAPI serverApi = api ?? throw new InvalidOperationException("World replay is unavailable in this service instance.");
        BlockMutationCapture mutationCapture = capture
            ?? throw new InvalidOperationException("World replay is unavailable in this service instance.");
        BlockMutationCoordinate coordinate = source.Coordinate;
        if (getCaptureGeneration(coordinate) != plannedGeneration) {
            return MainThreadApplyResult.Failed(BlockRollbackFailureCodes.CaptureGenerationChanged);
        }

        var position = new BlockPos(source.X, source.Y, source.Z, source.Dimension);
        if (!TryPrepareState(source.Envelope.After, position, out PreparedState after, out string afterFailure)) {
            return MainThreadApplyResult.Failed(afterFailure);
        }
        if (!TryPrepareState(source.Envelope.Before, position, out PreparedState before, out string beforeFailure)) {
            return MainThreadApplyResult.Failed(beforeFailure);
        }
        if (!BlockMutationCapture.TryCaptureState(serverApi.World, position, out EnvelopeBlockState current)) {
            return MainThreadApplyResult.Failed(BlockRollbackFailureCodes.UnsupportedState);
        }
        if (!current.Equals(source.Envelope.After)) {
            if (current.Equals(source.Envelope.Before)) {
                return MainThreadApplyResult.Failed(BlockRollbackFailureCodes.AuditMissing, stopBatch: true);
            }
            return MainThreadApplyResult.Failed(BlockRollbackFailureCodes.CurrentStateMismatch);
        }

        bool writeStarted = false;
        try {
            using (mutationCapture.Suppress()) {
                writeStarted = true;
                RestorePreparedState(before, position);
            }
            if (!BlockMutationCapture.TryCaptureState(serverApi.World, position, out EnvelopeBlockState restored)
                || !restored.Equals(source.Envelope.Before)) {
                TryRepairSourceState(after, position);
                return MainThreadApplyResult.Failed(BlockRollbackFailureCodes.RestoreFailed, stopBatch: true);
            }
            return MainThreadApplyResult.Success();
        }
        catch {
            if (writeStarted) TryRepairSourceState(after, position);
            return MainThreadApplyResult.Failed(BlockRollbackFailureCodes.RestoreFailed, stopBatch: true);
        }
    }

    private bool TryPrepareState(EnvelopeBlockState state, BlockPos position, out PreparedState prepared,
        out string failureCode) {
        ICoreServerAPI serverApi = api ?? throw new InvalidOperationException("World replay is unavailable in this service instance.");
        prepared = null!;
        failureCode = BlockRollbackFailureCodes.UnsupportedState;
        if (state.IsAir) {
            if (state.AssetCode != null || state.BlockEntityTreeAttributeBytes != null) return false;
            Block? air = serverApi.World.GetBlock(new AssetLocation("game", "air"));
            if (!BlockMutationCapture.IsExplicitAir(air) || air!.IsMissing) {
                failureCode = BlockRollbackFailureCodes.MissingBlockAsset;
                return false;
            }
            prepared = new PreparedState(state, air!, null);
            return true;
        }

        if (!TryParseAssetCode(state.AssetCode, out AssetLocation location)) return false;
        Block? block = serverApi.World.GetBlock(location);
        if (block == null || block.IsMissing || block.Code == null
            || !string.Equals(block.Code.Domain + ":" + block.Code.Path, state.AssetCode, StringComparison.Ordinal)) {
            failureCode = BlockRollbackFailureCodes.MissingBlockAsset;
            return false;
        }

        byte[]? treeBytes = state.BlockEntityTreeAttributeBytes;
        if (treeBytes == null) {
            if (!BlockMutationCapture.IsPlainSolidBlock(block)) return false;
            prepared = new PreparedState(state, block, null);
            return true;
        }
        if (!BlockMutationCapture.IsRecognizedMicroblockBlock(block)
            || !MicroblockTreeCodec.TryPrepareRestore(treeBytes, serverApi.World, position, state.AssetCode!, out TreeAttribute tree)) return false;
        prepared = new PreparedState(state, block, tree);
        return true;
    }

    private void TryRepairSourceState(PreparedState sourceAfter, BlockPos position) {
        ICoreServerAPI serverApi = api ?? throw new InvalidOperationException("World replay is unavailable in this service instance.");
        BlockMutationCapture mutationCapture = capture
            ?? throw new InvalidOperationException("World replay is unavailable in this service instance.");
        try {
            using (mutationCapture.Suppress()) {
                RestorePreparedState(sourceAfter, position);
            }
            // Best effort only. The failed audited attempt and stopped batch remain authoritative
            // even if this check confirms the source state was repaired.
            _ = BlockMutationCapture.TryCaptureState(serverApi.World, position, out EnvelopeBlockState repaired)
                && repaired.Equals(sourceAfter.Envelope);
        }
        catch {
            // Never conceal the original restore failure with a repair exception.
        }
    }

    private void RestorePreparedState(PreparedState target, BlockPos position) {
        ICoreServerAPI serverApi = api ?? throw new InvalidOperationException("World replay is unavailable in this service instance.");
        IBlockAccessor accessor = serverApi.World.BlockAccessor;
        accessor.SetBlock(target.Block.Id, position, 1);
        if (target.RestoreTree != null) {
            BlockEntity? blockEntity = accessor.GetBlockEntity(position);
            if (blockEntity == null && !string.IsNullOrWhiteSpace(target.Block.EntityClass)) {
                accessor.SpawnBlockEntity(target.Block.EntityClass, position, null);
                blockEntity = accessor.GetBlockEntity(position);
            }
            if (!BlockMutationCapture.IsRecognizedMicroblockPair(target.Block, blockEntity, position)) {
                throw new InvalidOperationException("The exact expected microblock block entity was not created.");
            }
            blockEntity!.FromTreeAttributes(target.RestoreTree, serverApi.World);
            blockEntity.HistoryStateRestore();
            blockEntity.MarkDirty(true, null);
            accessor.MarkBlockEntityDirty(position);
        }
        accessor.MarkBlockDirty(position, (IPlayer?)null);
        accessor.MarkBlockModified(position);
        accessor.TriggerNeighbourBlockUpdate(position);
    }

    private static bool TryParseAssetCode(string? code, out AssetLocation location) {
        location = null!;
        if (string.IsNullOrWhiteSpace(code)) return false;
        int separator = code.IndexOf(':');
        if (separator <= 0 || separator != code.LastIndexOf(':') || separator == code.Length - 1) return false;
        string domain = code[..separator];
        string path = code[(separator + 1)..];
        if (domain.Any(char.IsWhiteSpace) || path.Any(char.IsWhiteSpace)) return false;
        location = new AssetLocation(domain, path);
        return true;
    }

    private async Task<T> RunOnMainThreadAsync<T>(System.Func<T> action, string code,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackStarted = 0;
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() => {
            lock (lifecycleLock) {
                if (callbackStarted == 0) completion.TrySetCanceled(cancellationToken);
            }
        });
        try {
            lock (lifecycleLock) {
                if (disposed != 0 || cancellationToken.IsCancellationRequested) {
                    completion.TrySetCanceled(cancellationToken);
                }
                else {
                    enqueueMainThreadTask(() => {
                        lock (lifecycleLock) {
                            if (disposed != 0 || cancellationToken.IsCancellationRequested) {
                                completion.TrySetCanceled(cancellationToken);
                                return;
                            }
                            callbackStarted = 1;
                            try {
                                completion.TrySetResult(action());
                            }
                            catch (Exception exception) {
                                completion.TrySetException(exception);
                            }
                        }
                    }, code);
                }
            }
        }
        catch (Exception exception) {
            completion.TrySetException(exception);
        }
        return await completion.Task.ConfigureAwait(false);
    }

    public void Dispose() {
        lock (lifecycleLock) {
            if (disposed != 0) return;
            disposed = 1;
            lifetimeCancellation.Cancel();
        }
    }

    private sealed record PreparedState(EnvelopeBlockState Envelope, Block Block, TreeAttribute? RestoreTree);

    private sealed record MainThreadApplyResult(bool Succeeded, string? FailureCode, bool StopBatch) {
        internal static MainThreadApplyResult Success() => new(true, null, false);
        internal static MainThreadApplyResult Failed(string failureCode, bool stopBatch = false) => new(false, failureCode, stopBatch);
    }

    private sealed record QueuedWorldAttempt(MainThreadApplyResult ApplyResult,
        BlockMutationRollbackOutcome Outcome, string? FailureCode, Task<long> AuditTask);
}
