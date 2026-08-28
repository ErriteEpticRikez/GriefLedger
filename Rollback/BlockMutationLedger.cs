using System;

namespace GriefLedger.Rollback;

public enum BlockMutationEntryKind {
    Mutation = 0,
    Rollback = 1
}

public enum BlockMutationActionKind {
    Unknown = 0,
    Break = 1,
    Place = 2,
    ChiselConversion = 3,
    ChiselVoxel = 4,
    Rollback = 5
}

public enum BlockMutationRollbackOutcome {
    Succeeded = 1,
    Failed = 2,
    Skipped = 3
}

/// <summary>
/// Immutable input for the append-only block mutation ledger. Player identities are resolved
/// to the shared players table by the FIFO database writer.
/// </summary>
public sealed class BlockMutationAppend {
    private readonly byte[] envelopeData;

    public BlockMutationAppend(
        long timestampUtc,
        string? actorPlayerName,
        string? actorPlayerUid,
        BlockMutationEntryKind entryKind,
        BlockMutationActionKind actionKind,
        int dimension,
        int x,
        int y,
        int z,
        BlockStateEnvelope envelope,
        long? sourceMutationId = null,
        BlockMutationRollbackOutcome? rollbackOutcome = null,
        string? failureCode = null,
        string? operatorPlayerName = null,
        string? operatorPlayerUid = null
    ) {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!Enum.IsDefined(entryKind)) throw new ArgumentOutOfRangeException(nameof(entryKind));
        if (!Enum.IsDefined(actionKind) || actionKind == BlockMutationActionKind.Unknown) throw new ArgumentOutOfRangeException(nameof(actionKind));
        if (rollbackOutcome.HasValue && !Enum.IsDefined(rollbackOutcome.Value)) throw new ArgumentOutOfRangeException(nameof(rollbackOutcome));
        if (timestampUtc < 0) throw new ArgumentOutOfRangeException(nameof(timestampUtc), "The ledger timestamp must be a nonnegative Unix epoch second.");
        if (sourceMutationId <= 0) throw new ArgumentOutOfRangeException(nameof(sourceMutationId));
        if (failureCode?.Length > 128) throw new ArgumentOutOfRangeException(nameof(failureCode));
        if (failureCode != null && !IsStableFailureCode(failureCode)) {
            throw new ArgumentException("A failure code must use lowercase ASCII letters, digits, dots, underscores, or hyphens.", nameof(failureCode));
        }
        if (entryKind == BlockMutationEntryKind.Mutation && (sourceMutationId != null || rollbackOutcome != null || failureCode != null || operatorPlayerName != null || operatorPlayerUid != null)) {
            throw new ArgumentException("Mutation entries cannot carry rollback result fields.");
        }
        if (entryKind == BlockMutationEntryKind.Mutation && actionKind == BlockMutationActionKind.Rollback) {
            throw new ArgumentException("Mutation entries cannot use the rollback action.", nameof(actionKind));
        }
        if (entryKind == BlockMutationEntryKind.Rollback && (actionKind != BlockMutationActionKind.Rollback || sourceMutationId == null || rollbackOutcome == null || string.IsNullOrWhiteSpace(operatorPlayerUid))) {
            throw new ArgumentException("Rollback entries require the rollback action, a source mutation, an outcome, and a nonempty operator UID.");
        }
        if (rollbackOutcome == BlockMutationRollbackOutcome.Succeeded && failureCode != null) {
            throw new ArgumentException("A successful rollback cannot carry a failure code.", nameof(failureCode));
        }
        if (rollbackOutcome is BlockMutationRollbackOutcome.Failed or BlockMutationRollbackOutcome.Skipped && string.IsNullOrWhiteSpace(failureCode)) {
            throw new ArgumentException("A failed or skipped rollback requires a stable nonempty failure code.", nameof(failureCode));
        }

        TimestampUtc = timestampUtc;
        ActorPlayerName = actorPlayerName;
        ActorPlayerUid = actorPlayerUid;
        EntryKind = entryKind;
        ActionKind = actionKind;
        Dimension = dimension;
        X = x;
        Y = y;
        Z = z;
        envelopeData = envelope.Encode();
        SourceMutationId = sourceMutationId;
        RollbackOutcome = rollbackOutcome;
        FailureCode = failureCode;
        OperatorPlayerName = operatorPlayerName;
        OperatorPlayerUid = operatorPlayerUid;
    }

    private static bool IsStableFailureCode(string value) {
        if (value.Length == 0 || !IsLowerLetterOrDigit(value[0])) return false;
        for (int index = 1; index < value.Length; index++) {
            char character = value[index];
            if (!IsLowerLetterOrDigit(character) && character is not '.' and not '_' and not '-') return false;
        }
        return true;
    }

    private static bool IsLowerLetterOrDigit(char value) => value is >= 'a' and <= 'z' or >= '0' and <= '9';

    public long TimestampUtc { get; }
    public string? ActorPlayerName { get; }
    public string? ActorPlayerUid { get; }
    public BlockMutationEntryKind EntryKind { get; }
    public BlockMutationActionKind ActionKind { get; }
    public int Dimension { get; }
    public int X { get; }
    public int Y { get; }
    public int Z { get; }
    public int EnvelopeEncoding => BlockStateEnvelope.BinaryEncoding;
    public byte[] EnvelopeData => (byte[])envelopeData.Clone();
    public long? SourceMutationId { get; }
    public BlockMutationRollbackOutcome? RollbackOutcome { get; }
    public string? FailureCode { get; }
    public string? OperatorPlayerName { get; }
    public string? OperatorPlayerUid { get; }

    public BlockStateEnvelope DecodeEnvelope() => BlockStateEnvelope.Decode(envelopeData);
    internal byte[] CopyEnvelopeData() => (byte[])envelopeData.Clone();
}

/// <summary>A typed immutable row returned by future ledger readers.</summary>
public sealed class BlockMutationLogRow {
    private readonly byte[] envelopeData;

    internal BlockMutationLogRow(long id, long timestampUtc, long? actorPlayerId, BlockMutationEntryKind entryKind,
        BlockMutationActionKind actionKind, int dimension, int x, int y, int z, byte[] envelopeData,
        int envelopeEncoding, long? sourceMutationId, BlockMutationRollbackOutcome? rollbackOutcome,
        string? failureCode, long? operatorPlayerId) {
        Id = id;
        TimestampUtc = timestampUtc;
        ActorPlayerId = actorPlayerId;
        EntryKind = entryKind;
        ActionKind = actionKind;
        Dimension = dimension;
        X = x;
        Y = y;
        Z = z;
        this.envelopeData = (byte[])envelopeData.Clone();
        EnvelopeEncoding = envelopeEncoding;
        SourceMutationId = sourceMutationId;
        RollbackOutcome = rollbackOutcome;
        FailureCode = failureCode;
        OperatorPlayerId = operatorPlayerId;
    }

    public long Id { get; }
    public long TimestampUtc { get; }
    public long? ActorPlayerId { get; }
    public BlockMutationEntryKind EntryKind { get; }
    public BlockMutationActionKind ActionKind { get; }
    public int Dimension { get; }
    public int X { get; }
    public int Y { get; }
    public int Z { get; }
    public byte[] EnvelopeData => (byte[])envelopeData.Clone();
    public int EnvelopeEncoding { get; }
    public long? SourceMutationId { get; }
    public BlockMutationRollbackOutcome? RollbackOutcome { get; }
    public string? FailureCode { get; }
    public long? OperatorPlayerId { get; }

    public BlockStateEnvelope DecodeEnvelope() {
        if (EnvelopeEncoding != BlockStateEnvelope.BinaryEncoding) throw new InvalidOperationException("The ledger row uses an unsupported envelope encoding.");
        return BlockStateEnvelope.Decode(envelopeData);
    }
}
