using System.IO;
using GriefLedger.Rollback;
using Xunit;

namespace GriefLedger.ReflectionTests;

public sealed class BlockRollbackPlannerTests {
    private const string TargetUid = "target-uid";
    private static readonly EnvelopeBlockState Air = EnvelopeBlockState.Air();
    private static readonly EnvelopeBlockState A = EnvelopeBlockState.Asset("game:stone-a");
    private static readonly EnvelopeBlockState B = EnvelopeBlockState.Asset("game:stone-b");
    private static readonly EnvelopeBlockState C = EnvelopeBlockState.Asset("game:stone-c");

    [Fact]
    public void Same_player_selected_chain_is_strictly_newest_first_for_break_and_place() {
        BlockMutationLogRow first = Mutation(1, BlockMutationActionKind.Place, Air, A);
        BlockMutationLogRow second = Mutation(2, BlockMutationActionKind.Break, A, Air);
        BlockMutationLogRow third = Mutation(3, BlockMutationActionKind.Place, Air, B);

        BlockRollbackPlan plan = Build([first, third, second], [second, first, third]);

        Assert.Equal([3L, 2L, 1L], plan.Entries.Select(value => value.Source.Id));
        Assert.All(plan.Entries, value => Assert.Equal(BlockRollbackPlanDisposition.Apply, value.Disposition));
    }

    [Fact]
    public void Same_player_abc_chain_unwinds_and_discontinuous_chain_fails_closed() {
        BlockMutationLogRow first = Mutation(1, BlockMutationActionKind.Place, A, B);
        BlockMutationLogRow second = Mutation(2, BlockMutationActionKind.ChiselConversion, B, C);
        BlockRollbackPlan valid = Build([second, first], [first, second]);
        Assert.All(valid.Entries, value => Assert.Equal(BlockRollbackPlanDisposition.Apply, value.Disposition));

        BlockMutationLogRow discontinuous = Mutation(2, BlockMutationActionKind.Place, Air, C);
        BlockRollbackPlan invalid = Build([first, discontinuous], [first, discontinuous]);
        Assert.Equal(BlockRollbackFailureCodes.StateChainMismatch,
            Assert.Single(invalid.Entries, value => value.Source.Id == 1).FailureCode);
    }

    [Fact]
    public void Other_player_and_nonselected_later_mutations_block_older_sources() {
        BlockMutationLogRow source = Mutation(1, BlockMutationActionKind.Break, A, Air);
        BlockMutationLogRow other = Mutation(2, BlockMutationActionKind.Place, Air, B, "other-uid");
        BlockRollbackPlan otherPlan = Build([source], [source, other]);
        Assert.Equal(BlockRollbackFailureCodes.LaterOtherPlayer, otherPlan.Entries[0].FailureCode);

        BlockMutationLogRow samePlayerPlace = Mutation(2, BlockMutationActionKind.Place, Air, B);
        BlockRollbackPlan breakOnly = Build([source], [source, samePlayerPlace], breakOnly: true);
        Assert.Equal(BlockRollbackFailureCodes.LaterNonselectedMutation, breakOnly.Entries[0].FailureCode);
    }

    [Fact]
    public void Failed_later_unwind_blocks_older_but_skipped_outcome_does_not_change_history() {
        BlockMutationLogRow first = Mutation(1, BlockMutationActionKind.Place, A, B);
        BlockMutationLogRow second = Mutation(2, BlockMutationActionKind.Place, B, C);
        BlockMutationLogRow failed = Rollback(3, second, BlockMutationRollbackOutcome.Failed, "restore-failed");
        BlockRollbackPlan failedPlan = Build([first, second], [first, second, failed], cutoff: 2, historyThrough: 3);
        Assert.Equal(BlockRollbackPlanDisposition.Apply, failedPlan.Entries[0].Disposition);
        Assert.Equal(BlockRollbackFailureCodes.FailedLaterUnwind, failedPlan.Entries[1].FailureCode);

        BlockMutationLogRow skipped = Rollback(3, second, BlockMutationRollbackOutcome.Skipped, "earlier-conflict");
        BlockRollbackPlan skippedPlan = Build([first, second], [first, second, skipped], cutoff: 2, historyThrough: 3);
        Assert.All(skippedPlan.Entries, value => Assert.Equal(BlockRollbackPlanDisposition.Apply, value.Disposition));
    }

    [Fact]
    public void Prior_success_is_idempotent_and_partial_newest_success_allows_older_retry() {
        BlockMutationLogRow first = Mutation(1, BlockMutationActionKind.Place, A, B);
        BlockMutationLogRow second = Mutation(2, BlockMutationActionKind.Place, B, C);
        BlockMutationLogRow success = Rollback(3, second, BlockMutationRollbackOutcome.Succeeded);

        BlockRollbackPlan plan = Build([first, second], [first, second, success], cutoff: 2, historyThrough: 3);

        Assert.Equal(BlockRollbackFailureCodes.AlreadySucceeded, plan.Entries[0].FailureCode);
        Assert.Equal(BlockRollbackPlanDisposition.Apply, plan.Entries[1].Disposition);
    }

    [Fact]
    public void Successful_retry_resolves_earlier_failure_and_allows_older_chain_source() {
        BlockMutationLogRow first = Mutation(1, BlockMutationActionKind.Place, A, B);
        BlockMutationLogRow second = Mutation(2, BlockMutationActionKind.Place, B, C);
        BlockMutationLogRow failed = Rollback(3, second, BlockMutationRollbackOutcome.Failed, "restore-failed");
        BlockMutationLogRow success = Rollback(4, second, BlockMutationRollbackOutcome.Succeeded);

        BlockRollbackPlan plan = Build([first, second], [first, second, failed, success],
            cutoff: 2, historyThrough: 4);

        Assert.Equal(BlockRollbackFailureCodes.AlreadySucceeded, plan.Entries[0].FailureCode);
        Assert.Equal(BlockRollbackPlanDisposition.Apply, plan.Entries[1].Disposition);
    }

    [Fact]
    public void Failure_after_success_remains_unresolved_and_blocks_older_chain_source() {
        BlockMutationLogRow first = Mutation(1, BlockMutationActionKind.Place, A, B);
        BlockMutationLogRow second = Mutation(2, BlockMutationActionKind.Place, B, C);
        BlockMutationLogRow success = Rollback(3, second, BlockMutationRollbackOutcome.Succeeded);
        BlockMutationLogRow laterFailure = Rollback(4, second, BlockMutationRollbackOutcome.Failed, "restore-failed");

        BlockRollbackPlan plan = Build([first, second], [first, second, success, laterFailure],
            cutoff: 2, historyThrough: 4);

        Assert.Equal(BlockRollbackFailureCodes.AlreadySucceeded, plan.Entries[0].FailureCode);
        Assert.Equal(BlockRollbackFailureCodes.FailedLaterUnwind, plan.Entries[1].FailureCode);
    }

    [Fact]
    public void Unrelated_successful_rollback_blocks_and_post_selection_activity_is_not_a_candidate() {
        BlockMutationLogRow unrelated = Mutation(1, BlockMutationActionKind.Place, Air, A, "other-uid");
        BlockMutationLogRow source = Mutation(2, BlockMutationActionKind.Place, A, B);
        BlockMutationLogRow unrelatedSuccess = Rollback(3, unrelated, BlockMutationRollbackOutcome.Succeeded);
        BlockRollbackPlan successPlan = Build([source], [unrelated, source, unrelatedSuccess], cutoff: 2, historyThrough: 3);
        Assert.Equal(BlockRollbackFailureCodes.PriorSuccessfulRollback, successPlan.Entries[0].FailureCode);

        BlockMutationLogRow afterCutoff = Mutation(3, BlockMutationActionKind.Place, B, C);
        BlockRollbackPlan racePlan = Build([source], [source, afterCutoff], cutoff: 2, historyThrough: 3);
        Assert.Equal(BlockRollbackFailureCodes.LaterNonselectedMutation, racePlan.Entries[0].FailureCode);
    }

    [Fact]
    public void Planner_enforces_dimension_radius_cutoff_and_malformed_envelopes() {
        BlockMutationLogRow outside = Mutation(1, BlockMutationActionKind.Place, A, B, x: 20);
        Assert.Throws<InvalidDataException>(() => Build([outside], [outside], radius: 5));

        byte[] malformed = new BlockStateEnvelope(A, B).Encode();
        malformed[0] = 0;
        Assert.Throws<InvalidDataException>(() => new BlockMutationLogRow(
            1, 1, 1, BlockMutationEntryKind.Mutation, BlockMutationActionKind.Place,
            0, 0, 0, 0, malformed, 1, null, null, null, null, TargetUid));
    }

    [Fact]
    public void Planner_rejects_a_success_row_before_treating_its_source_as_idempotent() {
        BlockMutationLogRow source = Mutation(1, BlockMutationActionKind.Place, A, B);
        BlockMutationLogRow bogusSuccess = new(2, 2, null, BlockMutationEntryKind.Rollback,
            BlockMutationActionKind.Rollback, source.Dimension, source.X, source.Y, source.Z,
            source.Envelope.Encode(), 1, source.Id, BlockMutationRollbackOutcome.Succeeded,
            null, 999, null, null, "operator-uid", "Operator");

        Assert.Throws<InvalidDataException>(() =>
            Build([source], [source, bogusSuccess], cutoff: 1, historyThrough: 2));
    }

    private static BlockRollbackPlan Build(IReadOnlyList<BlockMutationLogRow> candidates,
        IReadOnlyList<BlockMutationLogRow> history, bool breakOnly = false, long cutoff = 10,
        long? historyThrough = null, int radius = 10) {
        return BlockRollbackPlanner.Build(new BlockRollbackPlanningRequest {
            TargetPlayerUid = TargetUid,
            Dimension = 0,
            CenterX = 0,
            CenterY = 0,
            CenterZ = 0,
            Radius = radius,
            BreakOnly = breakOnly,
            CutoffId = cutoff,
            HistoryThroughId = historyThrough ?? cutoff
        }, candidates, history);
    }

    private static BlockMutationLogRow Mutation(long id, BlockMutationActionKind action,
        EnvelopeBlockState before, EnvelopeBlockState after, string actorUid = TargetUid, int x = 0) {
        return new BlockMutationLogRow(id, id, id + 100, BlockMutationEntryKind.Mutation, action,
            0, x, 0, 0, new BlockStateEnvelope(before, after).Encode(), 1,
            null, null, null, null, actorUid, "Actor");
    }

    private static BlockMutationLogRow Rollback(long id, BlockMutationLogRow source,
        BlockMutationRollbackOutcome outcome, string? failureCode = null) {
        return new BlockMutationLogRow(id, id, null, BlockMutationEntryKind.Rollback,
            BlockMutationActionKind.Rollback, source.Dimension, source.X, source.Y, source.Z,
            new BlockStateEnvelope(source.Envelope.After, source.Envelope.Before).Encode(), 1,
            source.Id, outcome, failureCode, 999, null, null, "operator-uid", "Operator");
    }
}
