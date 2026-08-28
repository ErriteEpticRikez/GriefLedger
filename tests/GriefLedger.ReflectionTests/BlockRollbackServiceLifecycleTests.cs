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
        Assert.Equal(64L * 1024 * 1024, BlockRollbackLimits.MaximumEncodedBytesPerRead);
        var byteLimit = new BlockRollbackLimitExceededException("candidate encoded-byte total",
            BlockRollbackLimits.MaximumEncodedBytesPerRead);
        Assert.Equal(BlockRollbackLimits.MaximumEncodedBytesPerRead, byteLimit.Maximum);
        Assert.Equal("candidate encoded-byte total", byteLimit.LimitName);

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

    [Fact]
    public void Stopped_result_exposes_selected_and_unprocessed_sources() {
        var attempted = new BlockRollbackAttemptResult(9, BlockMutationRollbackOutcome.Failed,
            BlockRollbackFailureCodes.RestoreFailed, 21);
        var result = new BlockRollbackResult(10, 12, BlockRollbackFailureCodes.BatchStopped,
            [attempted], totalSelectedSourceCount: 3);

        Assert.Equal(BlockRollbackFailureCodes.BatchStopped, result.OperationFailureCode);
        Assert.Equal(3, result.TotalSelectedSourceCount);
        Assert.Equal(2, result.UnprocessedSourceCount);
        Assert.Single(result.Attempts);
        Assert.Empty(result.SkippedSourceIds);
        Assert.Equal(BlockRollbackFailureCodes.RestoreFailed, result.Attempts[0].FailureCode);
    }

    [Fact]
    public void Completed_page_reports_older_candidates_without_claiming_an_operation_failure() {
        var result = new BlockRollbackResult(10, 12, null,
            [new BlockRollbackAttemptResult(9, BlockMutationRollbackOutcome.Succeeded, null, 13)],
            totalSelectedSourceCount: 1, hasMoreCandidates: true, continuationBeforeSourceId: 9);

        Assert.True(result.HasMoreCandidates);
        Assert.Null(result.OperationFailureCode);
        Assert.Equal(0, result.UnprocessedSourceCount);
        Assert.Equal(9, result.ContinuationBeforeSourceId);
    }

    [Fact]
    public async Task History_limit_adaptively_shrinks_newest_page_and_exposes_continuation() {
        BlockMutationLogRow[] sources = Enumerable.Range(1, 4)
            .Select(id => Mutation(id, "target-uid")).OrderByDescending(row => row.Id).ToArray();
        BlockMutationLogRow laterOther = Mutation(5, "other-uid");
        var historyQueries = new List<BlockMutationHistoryQuery>();
        int cutoffReads = 0;
        int historyReads = 0;
        int watchDisposals = 0;
        using var service = new BlockRollbackService(
            () => Task.FromResult(Interlocked.Increment(ref cutoffReads) == 1 ? 4L : 5L),
            (_, _) => Task.FromResult<BlockMutationPlayer?>(new BlockMutationPlayer(7, "target-uid", "Target")),
            (_, _) => Task.FromResult(new BlockMutationCandidatePage(sources, false)),
            (query, _) => {
                historyQueries.Add(query);
                if (Interlocked.Increment(ref historyReads) == 1) {
                    throw new BlockRollbackLimitExceededException("history encoded-byte total", 1024);
                }
                return Task.FromResult<IReadOnlyList<BlockMutationLogRow>>([sources[0], sources[1], laterOther]);
            },
            _ => Task.FromResult(100L),
            _ => new TestWatch(_ => 0, () => Interlocked.Increment(ref watchDisposals)),
            (action, _) => action());

        BlockRollbackResult result = await service.RollbackAsync(Request());

        Assert.Equal([1L, 3L], historyQueries.Select(query => query.MinimumId));
        Assert.Equal(2, result.TotalSelectedSourceCount);
        Assert.Equal(2, result.Attempts.Count);
        Assert.True(result.HasMoreCandidates);
        Assert.Equal(3, result.ContinuationBeforeSourceId);
        Assert.All(result.Attempts, attempt => Assert.Equal(BlockMutationRollbackOutcome.Skipped, attempt.Outcome));
        Assert.Equal(1, watchDisposals);
    }

    private static BlockRollbackService CreateService(
        Func<string, CancellationToken, Task<BlockMutationPlayer?>>? resolve = null,
        Func<BlockMutationTargetQuery, CancellationToken, Task<IReadOnlyList<BlockMutationLogRow>>>? readTargets = null,
        Func<BlockMutationCoordinate, long>? generation = null,
        Action<Action, string>? enqueueMain = null) {
        return new BlockRollbackService(
            () => Task.FromResult(1L),
            resolve ?? ((_, _) => Task.FromResult<BlockMutationPlayer?>(null)),
            async (query, token) => new BlockMutationCandidatePage(
                await (readTargets ?? ((_, _) => Task.FromResult<IReadOnlyList<BlockMutationLogRow>>(Array.Empty<BlockMutationLogRow>())))(query, token), false),
            (_, _) => Task.FromResult<IReadOnlyList<BlockMutationLogRow>>(Array.Empty<BlockMutationLogRow>()),
            _ => Task.FromResult(1L),
            _ => new TestWatch(generation ?? (_ => 0)),
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
        return Mutation(1, "target-uid");
    }

    private static BlockMutationLogRow Mutation(long id, string actorUid) {
        var envelope = new BlockStateEnvelope(EnvelopeBlockState.Asset("game:stone"), EnvelopeBlockState.Air());
        return new BlockMutationLogRow(id, 1, 7, BlockMutationEntryKind.Mutation,
            BlockMutationActionKind.Break, 0, 0, 0, 0, envelope.Encode(), 1,
            null, null, null, null, actorUid, "Target");
    }

    private sealed class OversizedList<T>(int count) : IReadOnlyList<T> {
        public int Count { get; } = count;
        public T this[int index] => throw new InvalidOperationException("The oversized list must not be enumerated.");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("The oversized list must not be enumerated.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TestWatch(Func<BlockMutationCoordinate, long> generation, Action? dispose = null) : IBlockMutationWatch {
        public long GetGeneration(BlockMutationCoordinate coordinate) => generation(coordinate);
        public void Dispose() => dispose?.Invoke();
    }
}
