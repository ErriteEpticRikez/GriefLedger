using System;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace GriefLedger.Rollback;

/// <summary>
/// Canonical Vintage Story 1.22.7 microblock TreeAttribute capture. The 1.22.7 reader accepts
/// materials as StringArrayAttribute and resolves each domain:path through the world registry.
/// </summary>
internal static class MicroblockTreeCodec {
    internal const int MaximumMaterials = 256;
    internal const int MaximumCuboids = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryCapture(
        BlockEntityMicroBlock blockEntity,
        IWorldAccessor world,
        BlockPos position,
        string blockAssetCode,
        out byte[] bytes
    ) {
        bytes = null!;
        try {
            var tree = new TreeAttribute();
            blockEntity.ToTreeAttributes(tree);
            if (!TryCanonicalize(tree, blockEntity, world, position, blockAssetCode, out string[] materialCodes)) return false;
            if (!ValidateCanonicalTree(tree, position, blockAssetCode, materialCodes)) return false;

            bytes = tree.ToBytes();
            if (bytes.Length > EnvelopeBlockState.MaximumTreeAttributeBytes) return false;
            TreeAttribute decoded = TreeAttribute.CreateFromBytes(bytes);
            if (!ValidateCanonicalTree(decoded, position, blockAssetCode, materialCodes)) return false;
            if (!decoded.ToBytes().AsSpan().SequenceEqual(bytes)) return false;
            return true;
        }
        catch {
            bytes = null!;
            return false;
        }
    }

    private static bool TryCanonicalize(
        TreeAttribute tree,
        BlockEntityMicroBlock blockEntity,
        IWorldAccessor world,
        BlockPos position,
        string blockAssetCode,
        out string[] materialCodes
    ) {
        materialCodes = null!;
        if (tree["materials"] is not IntArrayAttribute rawMaterials) return false;
        int[] materialIds = rawMaterials.value;
        if (materialIds == null || materialIds.Length is < 1 or > MaximumMaterials) return false;

        materialCodes = new string[materialIds.Length];
        for (int index = 0; index < materialIds.Length; index++) {
            Block? material = world.GetBlock(materialIds[index]);
            if (!TryAssetCode(material, out string code) || material!.IsMissing || BlockMutationCapture.IsExplicitAir(material)) return false;
            Block? resolvedByCode = world.GetBlock(material.Code);
            if (!TryAssetCode(resolvedByCode, out string resolvedCode) || resolvedByCode!.IsMissing
                || BlockMutationCapture.IsExplicitAir(resolvedByCode)
                || !string.Equals(code, resolvedCode, StringComparison.Ordinal)) return false;
            materialCodes[index] = code;
        }

        if (blockEntity.DecorIds is int[] entityDecorIds
            && (entityDecorIds.Length != 6 || entityDecorIds.Any(id => id != 0))) return false;
        if (blockEntity.DecorRotations != 0) return false;
        if (tree["decorIds"] is IAttribute decorAttribute) {
            if (decorAttribute is not IntArrayAttribute decorIds || decorIds.value is not int[] decorValues
                || decorValues.Length != 6 || decorValues.Any(id => id != 0)) return false;
            tree.RemoveAttribute("decorIds");
        }
        if (tree["decorRot"] is not IntAttribute || tree.GetInt("decorRot") != 0) return false;
        tree.RemoveAttribute("decorRot");
        // Support-beam behavior bytes contain PlacedBeam.BlockId registry references in 1.22.7.
        if (tree.HasAttribute("beams")) return false;

        tree["materials"] = new StringArrayAttribute(materialCodes);
        tree.SetString("blockCode", blockAssetCode);
        // Base FromTreeAttributes restores Pos from these values, so retain the required absolute
        // coordinates and InternalY rather than stripping or zeroing them.
        tree.SetInt("posx", position.X);
        tree.SetInt("posy", position.InternalY);
        tree.SetInt("posz", position.Z);
        return true;
    }

    private static bool ValidateCanonicalTree(
        TreeAttribute tree,
        BlockPos position,
        string blockAssetCode,
        string[] materialCodes
    ) {
        if (tree.Count > 16 || tree.GetInt("posx") != position.X || tree.GetInt("posy") != position.InternalY
            || tree.GetInt("posz") != position.Z || tree.GetString("blockCode") != blockAssetCode) return false;
        if (tree["materials"] is not StringArrayAttribute materials || materials.value == null
            || !materials.value.SequenceEqual(materialCodes, StringComparer.Ordinal)) return false;
        if (tree.HasAttribute("decorIds") || tree.HasAttribute("decorRot") || tree.HasAttribute("beams")) return false;

        foreach (string key in tree.Keys) {
            IAttribute attribute = tree[key];
            switch (key) {
                case "posx":
                case "posy":
                case "posz":
                case "rotation":
                    if (attribute is not IntAttribute) return false;
                    break;
                case "materials":
                    if (attribute is not StringArrayAttribute) return false;
                    break;
                case "cuboids":
                    if (!BoundedCuboids(attribute, 1, MaximumCuboids, materialCodes.Length)) return false;
                    break;
                case "originalCuboids":
                case "snowcuboids":
                case "groundSnowCuboids":
                    if (!BoundedCuboids(attribute, 0, MaximumCuboids, materialCodes.Length)) return false;
                    break;
                case "availMaterialQuantities":
                    if (attribute is not IntArrayAttribute quantities || quantities.value.Length != materialCodes.Length
                        || quantities.value.Any(quantity => quantity is < 0 or > ushort.MaxValue)) return false;
                    break;
                case "emitSideAo":
                case "sideSolid":
                case "sideAlmostSolid":
                    if (attribute is not ByteArrayAttribute oneByte || oneByte.value?.Length != 1) return false;
                    break;
                case "blockCode":
                    if (attribute is not StringAttribute blockCode || blockCode.value != blockAssetCode) return false;
                    break;
                case "blockName":
                    if (attribute is not StringAttribute blockName || blockName.value == null
                        || StrictUtf8.GetByteCount(blockName.value) > 4096) return false;
                    break;
                default:
                    // Exact 1.22.7 chisel/microblock state only. Unknown behavior state fails closed.
                    return false;
            }
        }

        return tree["posx"] is IntAttribute && tree["posy"] is IntAttribute && tree["posz"] is IntAttribute
            && tree["rotation"] is IntAttribute && tree["cuboids"] is IntArrayAttribute
            && tree["emitSideAo"] is ByteArrayAttribute && tree["sideSolid"] is ByteArrayAttribute
            && tree["sideAlmostSolid"] is ByteArrayAttribute && tree["blockCode"] is StringAttribute
            && tree["blockName"] is StringAttribute;
    }

    private static bool BoundedCuboids(IAttribute attribute, int minimum, int maximum, int materialCount) {
        if (attribute is not IntArrayAttribute values || values.value == null
            || values.value.Length < minimum || values.value.Length > maximum) return false;
        foreach (int signed in values.value) {
            uint cuboid = unchecked((uint)signed);
            int minX = (int)(cuboid & 0xF);
            int minY = (int)((cuboid >> 4) & 0xF);
            int minZ = (int)((cuboid >> 8) & 0xF);
            int maxXInclusive = (int)((cuboid >> 12) & 0xF);
            int maxYInclusive = (int)((cuboid >> 16) & 0xF);
            int maxZInclusive = (int)((cuboid >> 20) & 0xF);
            int materialIndex = (int)(cuboid >> 24);
            if (minX > maxXInclusive || minY > maxYInclusive || minZ > maxZInclusive
                || materialIndex >= materialCount) return false;
        }
        return true;
    }

    private static bool TryAssetCode(Block? block, out string code) {
        code = null!;
        AssetLocation? location = block?.Code;
        if (location == null || string.IsNullOrWhiteSpace(location.Domain) || string.IsNullOrWhiteSpace(location.Path)
            || location.Domain.Contains(':') || location.Path.Contains(':')) return false;
        code = location.Domain + ":" + location.Path;
        return StrictUtf8.GetByteCount(code) <= EnvelopeBlockState.MaximumAssetCodeBytes;
    }
}
