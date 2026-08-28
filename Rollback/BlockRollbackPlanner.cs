using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GriefLedger.Rollback;

/// <summary>Conservative public bounds for one exact-ledger replay request.</summary>
public static class BlockRollbackLimits {
    public const int MaximumRadius = 256;
    public const int MaximumCandidates = 10_000;
    public const int MaximumUniqueCoordinates = 10_000;
    public const int MaximumHistoryRows = 200_000;
}

public sealed class BlockRollbackLimitExceededException : InvalidOperationException {
    public BlockRollbackLimitExceededException(string limitName, int maximum)
        : base($"The rollback request exceeded {limitName} ({maximum}).") {
        LimitName = limitName;
        Maximum = maximum;
    }

    public string LimitName { get; }
    public int Maximum { get; }
}

public enum BlockRollbackPlanDisposition {
    Apply = 0,
    Skip = 1
}

public static class BlockRollbackFailureCodes {
    public const string TargetPlayerNotFound = "target-player-not-found";
    public const string OutcomeAppendFailed = "outcome-append-failed";
    public const string AlreadySucceeded = "already-succeeded";
    public const string LaterOtherPlayer = "later-other-player";
    public const string LaterNonselectedMutation = "later-nonselected-mutation";
    public const string PriorSuccessfulRollback = "prior-successful-rollback";
    public const string FailedLaterUnwind = "failed-later-unwind";
    public const string StateChainMismatch = "state-chain-mismatch";
    public const string CaptureGenerationChanged = "capture-generation-changed";
    public const string CurrentStateMismatch = "current-state-mismatch";
    public const string UnsupportedState = "unsupported-state";
    public const string MissingBlockAsset = "missing-block-asset";
    public const string RestoreFailed = "restore-failed";
    public const string AuditMissing = "audit-missing-manual-reconciliation";
}

public sealed record BlockRollbackPlanningRequest {
    public required string TargetPlayerUid { get; init; }
    public int Dimension { get; init; }
    public int CenterX { get; init; }
    public int CenterY { get; init; }
    public int CenterZ { get; init; }
    public int Radius { get; init; }
    public bool BreakOnly { get; init; }
    public long CutoffId { get; init; }
    public long HistoryThroughId { get; init; }

    internal void Validate() {
        if (string.IsNullOrWhiteSpace(TargetPlayerUid)) throw new ArgumentException("A target immutable UID is required.", nameof(TargetPlayerUid));
        if (Radius is < 0 or > BlockRollbackLimits.MaximumRadius) throw new ArgumentOutOfRangeException(nameof(Radius));
        if (CutoffId < 0) throw new ArgumentOutOfRangeException(nameof(CutoffId));
        if (HistoryThroughId < CutoffId) throw new ArgumentOutOfRangeException(nameof(HistoryThroughId));
    }

    internal bool Contains(BlockMutationLogRow row) {
        return row.Dimension == Dimension && row.Id <= CutoffId
            && Math.Abs((long)row.X - CenterX) <= Radius
            && Math.Abs((long)row.Y - CenterY) <= Radius
            && Math.Abs((long)row.Z - CenterZ) <= Radius;
    }

    internal bool Selects(BlockMutationActionKind action) => action == BlockMutationActionKind.Break
        || !BreakOnly && action is BlockMutationActionKind.Place
            or BlockMutationActionKind.ChiselConversion or BlockMutationActionKind.ChiselVoxel;
}

public sealed record BlockRollbackPlanEntry(
    BlockMutationLogRow Source,
    BlockRollbackPlanDisposition Disposition,
    string? FailureCode
);

public sealed class BlockRollbackPlan {
    internal BlockRollbackPlan(long cutoffId, long historyThroughId, IReadOnlyList<BlockRollbackPlanEntry> entries) {
        CutoffId = cutoffId;
        HistoryThroughId = historyThroughId;
        Entries = entries;
    }

    public long CutoffId { get; }
    public long HistoryThroughId { get; }
    public IReadOnlyList<BlockRollbackPlanEntry> Entries { get; }
}

/// <summary>Pure deterministic planner over immutable exact-ledger rows.</summary>
public static class BlockRollbackPlanner {
    public static BlockRollbackPlan Build(BlockRollbackPlanningRequest request,
        IReadOnlyList<BlockMutationLogRow> candidates, IReadOnlyList<BlockMutationLogRow> history) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(history);
        request.Validate();
        if (candidates.Count > BlockRollbackLimits.MaximumCandidates) {
            throw new BlockRollbackLimitExceededException("candidate count", BlockRollbackLimits.MaximumCandidates);
        }
        if (history.Count > BlockRollbackLimits.MaximumHistoryRows) {
            throw new BlockRollbackLimitExceededException("history row count", BlockRollbackLimits.MaximumHistoryRows);
        }

        BlockMutationLogRow[] orderedCandidates = candidates.OrderByDescending(row => row.Id).ToArray();
        if (orderedCandidates.Select(row => row.Id).Distinct().Count() != orderedCandidates.Length) {
            throw new InvalidDataException("The rollback candidate set contains duplicate ledger ids.");
        }
        var selectedIds = orderedCandidates.Select(row => row.Id).ToHashSet();
        if (orderedCandidates.Select(row => row.Coordinate).Distinct().Count() > BlockRollbackLimits.MaximumUniqueCoordinates) {
            throw new BlockRollbackLimitExceededException("unique coordinate count", BlockRollbackLimits.MaximumUniqueCoordinates);
        }
        var historyById = new Dictionary<long, BlockMutationLogRow>();
        foreach (BlockMutationLogRow row in history) {
            if (row.Id > request.HistoryThroughId) throw new InvalidDataException("Coordinate history exceeds the requested durable history boundary.");
            if (!historyById.TryAdd(row.Id, row)) throw new InvalidDataException("Coordinate history contains a duplicate ledger id.");
        }

        foreach (BlockMutationLogRow candidate in orderedCandidates) {
            ValidateCandidate(request, candidate);
            if (!historyById.TryGetValue(candidate.Id, out BlockMutationLogRow? historySource)
                || historySource.EntryKind != BlockMutationEntryKind.Mutation
                || historySource.Coordinate != candidate.Coordinate
                || historySource.ActorPlayerId != candidate.ActorPlayerId
                || !string.Equals(historySource.ActorPlayerUid, candidate.ActorPlayerUid, StringComparison.Ordinal)
                || historySource.ActionKind != candidate.ActionKind
                || !historySource.Envelope.Equals(candidate.Envelope)) {
                throw new InvalidDataException("A selected rollback source is absent from or inconsistent with coordinate history.");
            }
        }

        foreach (BlockMutationLogRow row in history.Where(value => value.EntryKind == BlockMutationEntryKind.Rollback)) {
            if (!row.SourceMutationId.HasValue
                || !historyById.TryGetValue(row.SourceMutationId.Value, out BlockMutationLogRow? source)
                || source.EntryKind != BlockMutationEntryKind.Mutation || source.Coordinate != row.Coordinate) {
                throw new InvalidDataException("A rollback history row does not link to its exact source mutation.");
            }
            var exactInverse = new BlockStateEnvelope(source.Envelope.After, source.Envelope.Before);
            if (!row.Envelope.Equals(exactInverse)) {
                throw new InvalidDataException("A rollback history row is not the exact inverse of its source mutation.");
            }
        }

        var dispositions = new Dictionary<long, BlockRollbackPlanEntry>();
        var historyGroups = history.GroupBy(row => row.Coordinate)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.Id).ToArray());
        foreach (BlockMutationCoordinate coordinate in orderedCandidates.Select(row => row.Coordinate).Distinct()) {
            if (!historyGroups.TryGetValue(coordinate, out BlockMutationLogRow[]? coordinateHistory)) {
                throw new InvalidDataException("A candidate coordinate is absent from ledger history.");
            }
            PlanCoordinate(request, coordinateHistory, selectedIds, dispositions, historyById);
        }

        var entries = new List<BlockRollbackPlanEntry>(orderedCandidates.Length);
        foreach (BlockMutationLogRow source in orderedCandidates) {
            if (!dispositions.TryGetValue(source.Id, out BlockRollbackPlanEntry? entry)) {
                throw new InvalidDataException("A candidate source was not visited in its coordinate history.");
            }
            entries.Add(entry);
        }
        return new BlockRollbackPlan(request.CutoffId, request.HistoryThroughId, entries.AsReadOnly());
    }

    private static void ValidateCandidate(BlockRollbackPlanningRequest request, BlockMutationLogRow row) {
        if (row.EntryKind != BlockMutationEntryKind.Mutation || !request.Contains(row)
            || !request.Selects(row.ActionKind)
            || !string.Equals(row.ActorPlayerUid, request.TargetPlayerUid, StringComparison.Ordinal)) {
            throw new InvalidDataException("A rollback candidate is outside the requested immutable-player, action, cutoff, dimension, or radius scope.");
        }
    }

    private static void PlanCoordinate(BlockRollbackPlanningRequest request,
        IReadOnlyList<BlockMutationLogRow> rowsNewestFirst, HashSet<long> selectedIds,
        IDictionary<long, BlockRollbackPlanEntry> dispositions,
        IReadOnlyDictionary<long, BlockMutationLogRow> historyById) {
        var latestSuccessfulOutcomeIds = rowsNewestFirst
            .Where(row => row.EntryKind == BlockMutationEntryKind.Rollback
                && row.RollbackOutcome == BlockMutationRollbackOutcome.Succeeded)
            .GroupBy(row => row.SourceMutationId!.Value)
            .ToDictionary(group => group.Key, group => group.Max(row => row.Id));
        EnvelopeBlockState? earliestTransitionBefore = null;
        string? blocker = null;
        long greatestFailedSourceId = 0;

        foreach (BlockMutationLogRow row in rowsNewestFirst) {
            if (row.EntryKind == BlockMutationEntryKind.Mutation && selectedIds.Contains(row.Id)) {
                string? candidateBlocker = latestSuccessfulOutcomeIds.ContainsKey(row.Id)
                    ? BlockRollbackFailureCodes.AlreadySucceeded
                    : blocker ?? (greatestFailedSourceId > row.Id
                        ? BlockRollbackFailureCodes.FailedLaterUnwind
                        : earliestTransitionBefore != null && !row.Envelope.After.Equals(earliestTransitionBefore)
                            ? BlockRollbackFailureCodes.StateChainMismatch
                            : null);
                dispositions.Add(row.Id, new BlockRollbackPlanEntry(row,
                    candidateBlocker == null ? BlockRollbackPlanDisposition.Apply : BlockRollbackPlanDisposition.Skip,
                    candidateBlocker));
            }

            if (row.EntryKind == BlockMutationEntryKind.Mutation) {
                if (!string.Equals(row.ActorPlayerUid, request.TargetPlayerUid, StringComparison.Ordinal)) {
                    blocker ??= BlockRollbackFailureCodes.LaterOtherPlayer;
                }
                else if (!request.Selects(row.ActionKind) || !selectedIds.Contains(row.Id)) {
                    blocker ??= BlockRollbackFailureCodes.LaterNonselectedMutation;
                }
                else {
                    PrependTransition(row.Envelope, ref earliestTransitionBefore, ref blocker);
                }
                continue;
            }

            if (row.RollbackOutcome == BlockMutationRollbackOutcome.Succeeded) {
                long sourceId = row.SourceMutationId!.Value;
                if (!selectedIds.Contains(sourceId)
                    || !historyById.TryGetValue(sourceId, out BlockMutationLogRow? source)
                    || !string.Equals(source.ActorPlayerUid, request.TargetPlayerUid, StringComparison.Ordinal)
                    || !request.Selects(source.ActionKind)) {
                    blocker ??= BlockRollbackFailureCodes.PriorSuccessfulRollback;
                }
                else {
                    PrependTransition(row.Envelope, ref earliestTransitionBefore, ref blocker);
                }
            }
            else if (row.RollbackOutcome == BlockMutationRollbackOutcome.Failed) {
                long sourceId = row.SourceMutationId!.Value;
                if (!latestSuccessfulOutcomeIds.TryGetValue(sourceId, out long successId)
                    || successId <= row.Id) {
                    greatestFailedSourceId = Math.Max(greatestFailedSourceId, sourceId);
                }
            }
            // Skipped outcomes did not mutate the world.
        }
    }

    private static void PrependTransition(BlockStateEnvelope transition,
        ref EnvelopeBlockState? earliestTransitionBefore, ref string? blocker) {
        if (earliestTransitionBefore != null && !transition.After.Equals(earliestTransitionBefore)) {
            blocker ??= BlockRollbackFailureCodes.StateChainMismatch;
        }
        earliestTransitionBefore = transition.Before;
    }
}
