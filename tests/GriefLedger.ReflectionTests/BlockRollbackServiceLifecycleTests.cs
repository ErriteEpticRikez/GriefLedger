using GriefLedger.Rollback;
using Xunit;

namespace GriefLedger.ReflectionTests;

public sealed class BlockRollbackServiceLifecycleTests {
    [Fact]
    public async Task Dispose_cancels_never_run_main_task_and_later_callback_is_inert() {
        var queued = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        int generationReads = 0;
        BlockMutationLogRow source = Mutation();
        var service = CreateService(
            resolve: (_, _) => Task.FromResult<BlockMutationPlayer?>(new BlockMutationPlayer(7, "target-uid", "Target")),
            readTargets: (_, _) => Task.FromResult<IReadOnlyList<BlockMutationLogRow>>([source]),
            generation: _ => Interlocked.Increment(ref generationReads),
            enqueueMain: (action, _) => queued.TrySetResult(action));

        Task<BlockRollbackResult> operation = service.RollbackAsync(Request());
        Action lateCallback = await queued.Task.WaitAsync(TimeSpan.FromSeconds(2));

        BlockRollbackService waitingService = CreateService(
            resolve: (_, _) => Task.FromResult<BlockMutationPlayer?>(null));
        Task<BlockRollbackResult> waitingOperation = waitingService.RollbackAsync(Request());
        await Task.Yield();
        waitingService.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await waitingOperation.WaitAsync(TimeSpan.FromSeconds(2)));

        service.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await operation.WaitAsync(TimeSpan.FromSeconds(2)));
        lateCallback();
        Assert.Equal(0, Volatile.Read(ref generationReads));

        using BlockRollbackService reloaded = CreateService(
            resolve: (_, _) => Task.FromResult<BlockMutationPlayer?>(null));
        BlockRollbackResult result = await reloaded.RollbackAsync(Request()).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(BlockRollbackFailureCodes.TargetPlayerNotFound, result.OperationFailureCode);
    }

    [Fact]
    public void Request_and_query_bounds_are_conservative_and_public() {
        BlockRollbackRequest valid = Request() with { Radius = BlockRollbackLimits.MaximumRadius };
        valid.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (valid with { Radius = BlockRollbackLimits.MaximumRadius + 1 }).Validate());

        new BlockMutationTargetQuery {
            PlayerId = 1,
            Radius = BlockRollbackLimits.MaximumRadius,
            CutoffId = 1
        }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockMutationTargetQuery {
            PlayerId = 1,
            Radius = BlockRollbackLimits.MaximumRadius + 1,
            CutoffId = 1
        }.Validate());

        IReadOnlyList<BlockMutationCoordinate> tooManyCoordinates = Enumerable.Range(0,
            BlockRollbackLimits.MaximumUniqueCoordinates + 1)
            .Select(value => new BlockMutationCoordinate(0, value, 0, 0)).ToArray();
        Assert.Throws<BlockRollbackLimitExceededException>(() => new BlockMutationHistoryQuery {
            Coordinates = tooManyCoordinates,
            MaximumId = 1
        }.Validate());
    }

    [Fact]
    public void Planner_rejects_oversized_lists_before_enumeration() {
        Assert.Throws<BlockRollbackLimitExceededException>(() => BlockRollbackPlanner.Build(PlanningRequest(),
            new OversizedList<BlockMutationLogRow>(BlockRollbackLimits.MaximumCandidates + 1),
            Array.Empty<BlockMutationLogRow>()));
        Assert.Throws<BlockRollbackLimitExceededException>(() => BlockRollbackPlanner.Build(PlanningRequest(),
            Array.Empty<BlockMutationLogRow>(),
            new OversizedList<BlockMutationLogRow>(BlockRollbackLimits.MaximumHistoryRows + 1)));
    }

    private static BlockRollbackService CreateService(
        Func<string, CancellationToken, Task<BlockMutationPlayer?>>? resolve = null,
        Func<BlockMutationTargetQuery, CancellationToken, Task<IReadOnlyList<BlockMutationLogRow>>>? readTargets = null,
        Func<BlockMutationCoordinate, long>? generation = null,
        Action<Action, string>? enqueueMain = null) {
        return new BlockRollbackService(
            () => Task.FromResult(1L),
            resolve ?? ((_, _) => Task.FromResult<BlockMutationPlayer?>(null)),
            readTargets ?? ((_, _) => Task.FromResult<IReadOnlyList<BlockMutationLogRow>>(Array.Empty<BlockMutationLogRow>())),
            (_, _) => Task.FromResult<IReadOnlyList<BlockMutationLogRow>>(Array.Empty<BlockMutationLogRow>()),
            _ => Task.FromResult(1L),
            generation ?? (_ => 0),
            enqueueMain ?? ((_, _) => throw new InvalidOperationException("No main-thread callback was expected.")));
    }

    private static BlockRollbackRequest Request() => new() {
        OperatorPlayerUid = "operator-uid",
        TargetPlayerUid = "target-uid",
        Dimension = 0,
        CenterX = 0,
        CenterY = 0,
        CenterZ = 0,
        Radius = 0,
        BreakOnly = false
    };

    private static BlockRollbackPlanningRequest PlanningRequest() => new() {
        TargetPlayerUid = "target-uid",
        Radius = 0,
        CutoffId = 1,
        HistoryThroughId = 1
    };

    private static BlockMutationLogRow Mutation() {
        var envelope = new BlockStateEnvelope(EnvelopeBlockState.Asset("game:stone"), EnvelopeBlockState.Air());
        return new BlockMutationLogRow(1, 1, 7, BlockMutationEntryKind.Mutation,
            BlockMutationActionKind.Break, 0, 0, 0, 0, envelope.Encode(), 1,
            null, null, null, null, "target-uid", "Target");
    }

    private sealed class OversizedList<T>(int count) : IReadOnlyList<T> {
        public int Count { get; } = count;
        public T this[int index] => throw new InvalidOperationException("The oversized list must not be enumerated.");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("The oversized list must not be enumerated.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
