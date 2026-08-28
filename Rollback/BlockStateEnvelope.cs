using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace GriefLedger.Rollback;

/// <summary>Identifies whether an envelope state is air or a particular block asset.</summary>
public enum EnvelopeBlockStateKind : byte {
    Air = 0,
    Asset = 1
}

/// <summary>
/// An immutable block state stored in a rollback envelope. Tree-attribute bytes are opaque to
/// this type; capture/replay code may use them only for block entities it recognizes and owns.
/// </summary>
public sealed class EnvelopeBlockState : IEquatable<EnvelopeBlockState> {
    internal const int MaximumAssetCodeBytes = 1024;
    internal const int MaximumTreeAttributeBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[]? treeAttributeBytes;

    private EnvelopeBlockState(EnvelopeBlockStateKind kind, string? assetCode, byte[]? blockEntityTreeAttributeBytes) {
        Kind = kind;
        AssetCode = assetCode;
        treeAttributeBytes = blockEntityTreeAttributeBytes == null ? null : (byte[])blockEntityTreeAttributeBytes.Clone();
    }

    public EnvelopeBlockStateKind Kind { get; }
    public bool IsAir => Kind == EnvelopeBlockStateKind.Air;
    public string? AssetCode { get; }

    /// <summary>Returns a defensive copy of the opaque, owned TreeAttribute payload.</summary>
    public byte[]? BlockEntityTreeAttributeBytes => treeAttributeBytes == null ? null : (byte[])treeAttributeBytes.Clone();

    public static EnvelopeBlockState Air() => new(EnvelopeBlockStateKind.Air, null, null);

    public static EnvelopeBlockState Asset(string assetCode, byte[]? blockEntityTreeAttributeBytes = null) {
        if (string.IsNullOrWhiteSpace(assetCode)) throw new ArgumentException("An asset state requires a non-empty asset code.", nameof(assetCode));
        int assetByteCount;
        try {
            assetByteCount = StrictUtf8.GetByteCount(assetCode);
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException("The asset code is not valid UTF-8 text.", nameof(assetCode), exception);
        }
        if (assetByteCount > MaximumAssetCodeBytes) throw new ArgumentOutOfRangeException(nameof(assetCode), "The encoded asset code is too large.");
        if (blockEntityTreeAttributeBytes?.Length > MaximumTreeAttributeBytes) {
            throw new ArgumentOutOfRangeException(nameof(blockEntityTreeAttributeBytes), "The TreeAttribute payload is too large.");
        }
        return new EnvelopeBlockState(EnvelopeBlockStateKind.Asset, assetCode, blockEntityTreeAttributeBytes);
    }

    internal byte[]? CopyTreeAttributeBytes() => treeAttributeBytes == null ? null : (byte[])treeAttributeBytes.Clone();

    public bool Equals(EnvelopeBlockState? other) {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || Kind != other.Kind || !string.Equals(AssetCode, other.AssetCode, StringComparison.Ordinal)) return false;
        if ((treeAttributeBytes == null) != (other.treeAttributeBytes == null)) return false;
        return treeAttributeBytes.AsSpan().SequenceEqual(other.treeAttributeBytes);
    }

    public override bool Equals(object? obj) => Equals(obj as EnvelopeBlockState);

    public override int GetHashCode() {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(AssetCode, StringComparer.Ordinal);
        hash.Add(treeAttributeBytes != null);
        if (treeAttributeBytes != null) foreach (byte value in treeAttributeBytes) hash.Add(value);
        return hash.ToHashCode();
    }
}

/// <summary>A deterministic, immutable and versioned before/after block-state envelope.</summary>
public sealed class BlockStateEnvelope : IEquatable<BlockStateEnvelope> {
    private static ReadOnlySpan<byte> Magic => "GLBE"u8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public const ushort CurrentVersion = 1;
    public const int BinaryEncoding = 1;
    public const int StateCount = 2;
    public const int MaximumEncodedBytes = 2 * EnvelopeBlockState.MaximumTreeAttributeBytes + 4096;

    public BlockStateEnvelope(EnvelopeBlockState before, EnvelopeBlockState after) {
        Before = before ?? throw new ArgumentNullException(nameof(before));
        After = after ?? throw new ArgumentNullException(nameof(after));
    }

    public ushort Version => CurrentVersion;
    public EnvelopeBlockState Before { get; }
    public EnvelopeBlockState After { get; }

    public byte[] Encode() {
        byte[] beforeAsset = EncodeAssetCode(Before);
        byte[] afterAsset = EncodeAssetCode(After);
        byte[]? beforeTree = Before.CopyTreeAttributeBytes();
        byte[]? afterTree = After.CopyTreeAttributeBytes();
        int length = checked(8 + StateEncodedLength(beforeAsset, beforeTree) + StateEncodedLength(afterAsset, afterTree));
        if (length > MaximumEncodedBytes) throw new InvalidOperationException("The block-state envelope is too large.");

        byte[] output = new byte[length];
        Magic.CopyTo(output);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4, 2), CurrentVersion);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6, 2), StateCount);
        int offset = 8;
        WriteState(output, ref offset, Before, beforeAsset, beforeTree);
        WriteState(output, ref offset, After, afterAsset, afterTree);
        return output;
    }

    public static BlockStateEnvelope Decode(byte[] data) {
        ArgumentNullException.ThrowIfNull(data);
        return Decode(data.AsSpan());
    }

    public static BlockStateEnvelope Decode(ReadOnlySpan<byte> data) {
        if (data.Length > MaximumEncodedBytes) throw Malformed("The envelope exceeds the maximum encoded size.");
        if (data.Length < 8 || !data[..4].SequenceEqual(Magic)) throw Malformed("The envelope header is invalid.");
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));
        if (version != CurrentVersion) throw new InvalidDataException("Unsupported block-state envelope version " + version + ".");
        ushort count = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(6, 2));
        if (count != StateCount) throw Malformed("A block-state envelope must contain exactly two states.");

        int offset = 8;
        EnvelopeBlockState before = ReadState(data, ref offset);
        EnvelopeBlockState after = ReadState(data, ref offset);
        if (offset != data.Length) throw Malformed("The envelope contains trailing data.");
        return new BlockStateEnvelope(before, after);
    }

    private static byte[] EncodeAssetCode(EnvelopeBlockState state) {
        if (state.Kind == EnvelopeBlockStateKind.Air) return Array.Empty<byte>();
        if (state.Kind != EnvelopeBlockStateKind.Asset || state.AssetCode == null) throw new InvalidOperationException("The envelope contains an invalid state kind.");
        try {
            return StrictUtf8.GetBytes(state.AssetCode);
        }
        catch (EncoderFallbackException exception) {
            throw new InvalidOperationException("The envelope contains an invalid asset code.", exception);
        }
    }

    private static int StateEncodedLength(byte[] asset, byte[]? tree) => checked(12 + asset.Length + (tree?.Length ?? 0));

    private static void WriteState(byte[] output, ref int offset, EnvelopeBlockState state, byte[] asset, byte[]? tree) {
        output[offset] = (byte)state.Kind;
        output[offset + 1] = tree == null ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(offset + 2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset + 4, 4), checked((uint)asset.Length));
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset + 8, 4), checked((uint)(tree?.Length ?? 0)));
        offset += 12;
        asset.CopyTo(output, offset);
        offset += asset.Length;
        if (tree != null) {
            tree.CopyTo(output, offset);
            offset += tree.Length;
        }
    }

    private static EnvelopeBlockState ReadState(ReadOnlySpan<byte> data, ref int offset) {
        EnsureRemaining(data, offset, 12);
        byte rawKind = data[offset];
        byte flags = data[offset + 1];
        ushort reserved = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
        uint assetLength = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));
        uint treeLength = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8, 4));
        offset += 12;
        if ((flags & ~1) != 0 || reserved != 0) throw Malformed("The envelope state contains unsupported flags.");
        if (assetLength > EnvelopeBlockState.MaximumAssetCodeBytes || treeLength > EnvelopeBlockState.MaximumTreeAttributeBytes) {
            throw Malformed("The envelope state exceeds a size bound.");
        }
        int assetCount = checked((int)assetLength);
        int treeCount = checked((int)treeLength);
        EnsureRemaining(data, offset, checked(assetCount + treeCount));

        if (rawKind == (byte)EnvelopeBlockStateKind.Air) {
            if (flags != 0 || assetCount != 0 || treeCount != 0) throw Malformed("Air cannot carry an asset code or TreeAttribute payload.");
            return EnvelopeBlockState.Air();
        }
        if (rawKind != (byte)EnvelopeBlockStateKind.Asset) throw Malformed("The envelope contains an unknown state kind.");
        if (assetCount == 0) throw Malformed("An asset state requires an asset code.");
        if ((flags & 1) == 0 && treeCount != 0) throw Malformed("The TreeAttribute length is inconsistent with its presence flag.");

        string assetCode;
        try {
            assetCode = StrictUtf8.GetString(data.Slice(offset, assetCount));
        }
        catch (DecoderFallbackException exception) {
            throw new InvalidDataException("The envelope asset code is not valid UTF-8.", exception);
        }
        offset += assetCount;
        byte[]? tree = (flags & 1) == 0 ? null : data.Slice(offset, treeCount).ToArray();
        offset += treeCount;
        try {
            return EnvelopeBlockState.Asset(assetCode, tree);
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException("The envelope contains an invalid asset state.", exception);
        }
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> data, int offset, int required) {
        if (offset < 0 || required < 0 || offset > data.Length - required) throw Malformed("The envelope is truncated.");
    }

    private static InvalidDataException Malformed(string message) => new(message);

    public bool Equals(BlockStateEnvelope? other) => other != null && Before.Equals(other.Before) && After.Equals(other.After);
    public override bool Equals(object? obj) => Equals(obj as BlockStateEnvelope);
    public override int GetHashCode() => HashCode.Combine(Before, After);
}
