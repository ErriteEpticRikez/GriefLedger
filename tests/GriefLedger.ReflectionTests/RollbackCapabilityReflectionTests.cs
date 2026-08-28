using GriefLedger.Rollback;
using GriefLedger.Patches;
using HarmonyLib;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Vintagestory.Server;
using Xunit;

namespace GriefLedger.ReflectionTests;

public sealed class RollbackCapabilityReflectionTests {
    [Fact]
    public void Vintage_story_1_22_7_exact_targets_resolve() {
        RollbackCapabilityStatus probe = RollbackCapability.Probe();

        Assert.Equal("1.22.7", probe.ExpectedGameVersion);
        Assert.Equal(probe.ExpectedGameVersion, probe.ActualGameVersion);
        Assert.False(probe.IsAvailable);
        Assert.Empty(probe.Errors);
        Assert.Equal(4, probe.Targets.Count);
        Assert.All(probe.Targets, target => {
            Assert.True(target.Resolved, target.Error ?? target.ExpectedSignature);
            Assert.False(target.Patched);
            Assert.Contains(target.Name, new[] {
                "player-block-mutation",
                "item-chisel-conversion",
                "blockentity-chisel-server-packet",
                "blockentity-chisel-update-voxel"
            });
        });
    }

    [Fact]
    public void Exact_patch_methods_apply_with_owned_prefixes_and_postfixes() {
        string ownerId = "griefledger-reflection-test-" + Guid.NewGuid().ToString("N");
        var harmony = new Harmony(ownerId);
        Assembly griefLedger = typeof(RollbackCapability).Assembly;
        try {
            PatchAndAssertOwned(
                harmony,
                AccessTools.DeclaredMethod(typeof(ServerSystemBlockSimulation), "TryModifyBlockInWorld", [typeof(ServerPlayer), typeof(global::Packet_ClientBlockPlaceOrBreak)]),
                PatchMethod(griefLedger, "GriefLedger.Patches.PlayerBlockMutationPatch", "Prefix"),
                PatchMethod(griefLedger, "GriefLedger.Patches.PlayerBlockMutationPatch", "Postfix"),
                PatchMethod(griefLedger, "GriefLedger.Patches.PlayerBlockMutationPatch", "Finalizer")
            );
            PatchAndAssertOwned(
                harmony,
                AccessTools.DeclaredMethod(typeof(ItemChisel), nameof(ItemChisel.OnHeldInteractStart), [typeof(ItemSlot), typeof(EntityAgent), typeof(BlockSelection), typeof(EntitySelection), typeof(bool), typeof(EnumHandHandling).MakeByRefType()]),
                PatchMethod(griefLedger, "GriefLedger.Patches.ItemChiselConversionPatch", "Prefix"),
                PatchMethod(griefLedger, "GriefLedger.Patches.ItemChiselConversionPatch", "Postfix"),
                PatchMethod(griefLedger, "GriefLedger.Patches.ItemChiselConversionPatch", "Finalizer")
            );
            PatchAndAssertOwned(
                harmony,
                AccessTools.DeclaredMethod(typeof(BlockEntityChisel), nameof(BlockEntityChisel.OnReceivedClientPacket), [typeof(IPlayer), typeof(int), typeof(byte[])]),
                PatchMethod(griefLedger, "GriefLedger.Patches.AuthoritativeChiselPacketPatch", "Prefix"),
                PatchMethod(griefLedger, "GriefLedger.Patches.AuthoritativeChiselPacketPatch", "Postfix"),
                PatchMethod(griefLedger, "GriefLedger.Patches.AuthoritativeChiselPacketPatch", "Finalizer")
            );
            PatchAndAssertOwned(
                harmony,
                AccessTools.DeclaredMethod(typeof(BlockEntityChisel), "UpdateVoxel", [typeof(IPlayer), typeof(ItemSlot), typeof(Vec3i), typeof(BlockFacing), typeof(bool)]),
                PatchMethod(griefLedger, "GriefLedger.Patches.BlockEntityChiselUpdateVoxelPatch", "Prefix"),
                PatchMethod(griefLedger, "GriefLedger.Patches.BlockEntityChiselUpdateVoxelPatch", "Postfix"),
                PatchMethod(griefLedger, "GriefLedger.Patches.BlockEntityChiselUpdateVoxelPatch", "Finalizer")
            );
        }
        finally {
            harmony.UnpatchAll(ownerId);
        }
    }

    [Fact]
    public void Failed_completion_is_terminal_exactly_once_and_subscribers_are_isolated() {
        var context = new PlayerPlacementSeamContext(null!, null!, new BlockPos(1, 2, 3), 1, null);
        int observed = 0;
        Action<PlayerPlacementSeamContext> throwing = _ => throw new InvalidOperationException("test subscriber");
        Action<PlayerPlacementSeamContext> counting = completed => {
            observed++;
            Assert.Same(context, completed);
        };

        RollbackSeams.PlayerPlacementCompleted += throwing;
        RollbackSeams.PlayerPlacementCompleted += counting;
        try {
            Assert.True(RollbackSeams.EmitPlacementCompleted(context, RollbackMutationOutcome.Failed, "before-read"));
            Assert.False(RollbackSeams.EmitPlacementCompleted(context, RollbackMutationOutcome.NoChange));
        }
        finally {
            RollbackSeams.PlayerPlacementCompleted -= throwing;
            RollbackSeams.PlayerPlacementCompleted -= counting;
        }

        Assert.Equal(1, observed);
        Assert.Equal(RollbackMutationOutcome.Failed, context.Outcome);
        Assert.Equal("before-read", context.FailureCode);
    }

    [Fact]
    public void Pending_tracker_pairs_nested_keys_without_clearing_unrelated_entries() {
        var tracker = new KeyedPendingTracker<string, object>();
        var outer = new object();
        var nested = new object();
        var unrelated = new object();
        tracker.Add("same", outer);
        tracker.Add("same", nested);
        tracker.Add("other", unrelated);

        Assert.True(tracker.TryTake("same", out object? first));
        Assert.Same(nested, first);
        Assert.False(tracker.TryTake("missing", out _));
        Assert.Equal(2, tracker.Count);
        Assert.True(tracker.Remove("same", outer));
        Assert.False(tracker.Remove("same", outer));
        Assert.Equal(1, tracker.Count);
        Assert.True(tracker.TryTake("other", out object? last));
        Assert.Same(unrelated, last);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void Pending_tracker_stale_removal_targets_only_the_original_context() {
        var tracker = new KeyedPendingTracker<string, object>();
        var stale = new object();
        var newer = new object();
        tracker.Add("key", stale);
        tracker.Add("key", newer);

        Assert.True(tracker.Remove("key", stale));
        Assert.True(tracker.TryTake("key", out object? remaining));
        Assert.Same(newer, remaining);
        Assert.Empty(tracker.Drain());
    }

    [Fact]
    public void Packet_internal_y_reconstructs_dimension_and_matches_break_confirmation_key() {
        var original = new BlockPos(321, 72, -654, 7);
        var packet = new global::Packet_ClientBlockPlaceOrBreak {
            X = original.X,
            Y = original.InternalY,
            Z = original.Z
        };

        var reconstructed = new BlockPos(packet.X, packet.Y, packet.Z);

        Assert.Equal(original.X, reconstructed.X);
        Assert.Equal(original.Y, reconstructed.Y);
        Assert.Equal(original.Z, reconstructed.Z);
        Assert.Equal(original.dimension, reconstructed.dimension);
        Assert.Equal(original.InternalY, reconstructed.InternalY);
        Assert.Equal(
            PlayerBlockMutationPatch.Key("player-uid", original),
            PlayerBlockMutationPatch.Key("player-uid", reconstructed)
        );
    }

    [Fact]
    public void Legacy_chisel_audit_registration_is_independent_and_idempotent() {
        string ownerId = "griefledger-legacy-audit-test-" + Guid.NewGuid().ToString("N");
        var harmony = new Harmony(ownerId);
        try {
            RollbackTargetStatus first = RollbackCapability.EnsureLegacyChiselAuditPatch(harmony);
            RollbackTargetStatus second = RollbackCapability.EnsureLegacyChiselAuditPatch(harmony);
            Assert.True(first.Resolved, first.Error);
            Assert.True(first.Patched, first.Error);
            Assert.True(second.Patched, second.Error);

            MethodInfo target = Assert.IsAssignableFrom<MethodInfo>(AccessTools.DeclaredMethod(
                typeof(ItemChisel),
                nameof(ItemChisel.OnHeldInteractStart),
                [typeof(ItemSlot), typeof(EntityAgent), typeof(BlockSelection), typeof(EntitySelection), typeof(bool), typeof(EnumHandHandling).MakeByRefType()]
            ));
            HarmonyLib.Patches info = Assert.IsAssignableFrom<HarmonyLib.Patches>(Harmony.GetPatchInfo(target));
            Assert.Single(info.Prefixes, patch => patch.owner == ownerId);
            Assert.Single(info.Postfixes, patch => patch.owner == ownerId);
            Assert.Single(info.Finalizers, patch => patch.owner == ownerId);
        }
        finally {
            harmony.UnpatchAll(ownerId);
        }
    }

    [Fact]
    public void Capture_owns_exactly_one_of_each_seam_subscription_and_disposes_them() {
        string[] eventNames = [
            nameof(RollbackSeams.PlayerPlacementStarting), nameof(RollbackSeams.PlayerPlacementCompleted),
            nameof(RollbackSeams.PlayerBreakStarting), nameof(RollbackSeams.PlayerBreakCompleted),
            nameof(RollbackSeams.ChiselConversionStarting), nameof(RollbackSeams.ChiselConversionCompleted),
            nameof(RollbackSeams.ChiselVoxelStarting), nameof(RollbackSeams.ChiselVoxelCompleted)
        ];
        Dictionary<string, int> before = eventNames.ToDictionary(name => name, SeamSubscriberCount);
        var capture = new BlockMutationCapture(_ => Task.FromResult(1L), () => 1, (_, _) => { });
        try {
            capture.Subscribe();
            capture.Subscribe();
            Assert.All(eventNames, name => Assert.Equal(before[name] + 1, SeamSubscriberCount(name)));
        }
        finally {
            capture.Dispose();
            capture.Dispose();
        }
        Assert.All(eventNames, name => Assert.Equal(before[name], SeamSubscriberCount(name)));
    }

    [Fact]
    public void Capture_appends_immutable_absolute_supported_transition_and_tracks_generation() {
        Block before = PlainBlock("stone-granite");
        Block after = PlainBlock("stone-andesite");
        Block air = AirBlock();
        Block current = before;
        IBlockAccessor accessor = Proxy<IBlockAccessor>((method, arguments) => method.Name switch {
            nameof(IBlockAccessor.GetBlock) when arguments?.Length == 2 && arguments[1] is int layer => layer == 2 ? air : current,
            nameof(IBlockAccessor.GetSubDecors) => null,
            nameof(IBlockAccessor.GetBlockEntity) => null,
            _ => Default(method.ReturnType)
        });
        IWorldAccessor world = Proxy<IWorldAccessor>((method, _) => method.Name == "get_BlockAccessor" ? accessor : Default(method.ReturnType));
        IPlayer player = (IPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));
        var position = new BlockPos(123456, 77, -654321, 4);
        BlockMutationAppend? observed = null;
        int appendFailures = 0;
        using var capture = new BlockMutationCapture(request => {
            observed = request;
            return Task.FromException<long>(new InvalidOperationException("database failed"));
        }, () => 1234, (_, exception) => {
            Assert.IsType<InvalidOperationException>(exception);
            appendFailures++;
        });
        capture.Subscribe();

        var context = new PlayerPlacementSeamContext(player, world, position, 1, before);
        RollbackSeams.EmitPlacementStarting(context);
        current = after;
        context.AfterBlock = after;
        Assert.True(RollbackSeams.EmitPlacementCompleted(context, RollbackMutationOutcome.Changed));

        BlockMutationAppend append = Assert.IsType<BlockMutationAppend>(observed);
        Assert.Equal(1234, append.TimestampUtc);
        Assert.Null(append.ActorPlayerName);
        Assert.Null(append.ActorPlayerUid);
        Assert.Equal(BlockMutationActionKind.Place, append.ActionKind);
        Assert.Equal((4, 123456, 77, -654321), (append.Dimension, append.X, append.Y, append.Z));
        Assert.Equal("game:stone-granite", append.DecodeEnvelope().Before.AssetCode);
        Assert.Equal("game:stone-andesite", append.DecodeEnvelope().After.AssetCode);
        Assert.Equal(1, capture.GetGeneration(4, 123456, 77, -654321));
        Assert.Equal(1, appendFailures);
    }

    [Fact]
    public void Generation_advances_for_unsupported_changed_player_mutations_but_not_suppressed_paths() {
        IPlayer player = (IPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));
        var position = new BlockPos(8, 9, 10, 2);
        using var capture = new BlockMutationCapture(_ => throw new InvalidOperationException("must not append"), () => 1, (_, _) => { });
        capture.Subscribe();

        var unsupported = new PlayerPlacementSeamContext(player, null!, position, 1, null);
        RollbackSeams.EmitPlacementStarting(unsupported);
        Assert.True(RollbackSeams.EmitPlacementCompleted(unsupported, RollbackMutationOutcome.Changed));
        Assert.Equal(1, capture.GetGeneration(2, 8, 9, 10));

        using (capture.Suppress()) {
            using (capture.Suppress()) {
                var suppressed = new PlayerPlacementSeamContext(player, null!, position, 1, null);
                RollbackSeams.EmitPlacementStarting(suppressed);
                Assert.True(RollbackSeams.EmitPlacementCompleted(suppressed, RollbackMutationOutcome.Changed));
            }
        }
        Assert.Equal(1, capture.GetGeneration(2, 8, 9, 10));
    }

    [Fact]
    public void Allowlist_recognizes_only_exact_plain_air_and_vanilla_1_22_7_microblock_pairs() {
        Block air = AirBlock();
        Assert.True(BlockMutationCapture.IsExplicitAir(air));
        Assert.False(BlockMutationCapture.IsExplicitAir(new Block { Code = new AssetLocation("other", "air") }));
        Assert.True(BlockMutationCapture.IsPlainSolidBlock(PlainBlock("rock-granite")));
        Assert.False(BlockMutationCapture.IsPlainSolidBlock(new DerivedPlainBlock {
            Code = new AssetLocation("game", "rock-granite"),
            SideSolid = new SmallBoolArray(SmallBoolArray.OnAllSides)
        }));

        var position = new BlockPos(1, 2, 3, 6);
        var chiselBlock = new BlockChisel { Code = new AssetLocation("game", "chiseledblock") };
        var chiselEntity = new BlockEntityChisel { Pos = position.Copy() };
        Assert.True(BlockMutationCapture.IsRecognizedMicroblockPair(chiselBlock, chiselEntity, position));
        chiselBlock.Code = new AssetLocation("unknownmod", "chiseledblock");
        Assert.False(BlockMutationCapture.IsRecognizedMicroblockPair(chiselBlock, chiselEntity, position));

        var microBlock = new BlockMicroBlock { Code = new AssetLocation("game", "microblock-snow") };
        var microEntity = new BlockEntityMicroBlock { Pos = position.Copy() };
        Assert.True(BlockMutationCapture.IsRecognizedMicroblockPair(microBlock, microEntity, position));
        Assert.False(BlockMutationCapture.IsRecognizedMicroblockPair(microBlock, chiselEntity, position));
        microEntity.Pos = new BlockPos(1, 2, 3, 7);
        Assert.False(BlockMutationCapture.IsRecognizedMicroblockPair(microBlock, microEntity, position));
    }

    [Fact]
    public void Snapshot_allowlist_rejects_fluid_decor_and_arbitrary_block_entity_state() {
        Block solid = PlainBlock("rock-granite");
        Block air = AirBlock();
        Block fluid = air;
        Dictionary<int, Block>? decors = null;
        BlockEntity? blockEntity = null;
        IBlockAccessor accessor = Proxy<IBlockAccessor>((method, arguments) => method.Name switch {
            nameof(IBlockAccessor.GetBlock) when arguments?.Length == 2 && arguments[1] is int layer => layer == 2 ? fluid : solid,
            nameof(IBlockAccessor.GetSubDecors) => decors,
            nameof(IBlockAccessor.GetBlockEntity) => blockEntity,
            _ => Default(method.ReturnType)
        });
        IWorldAccessor world = Proxy<IWorldAccessor>((method, _) => method.Name == "get_BlockAccessor" ? accessor : Default(method.ReturnType));
        var position = new BlockPos(5, 6, 7, 3);
        Assert.True(BlockMutationCapture.TryCaptureState(world, position, out EnvelopeBlockState supported));
        Assert.Equal("game:rock-granite", supported.AssetCode);

        decors = new Dictionary<int, Block>();
        Assert.True(BlockMutationCapture.TryCaptureState(world, position, out _));

        fluid = PlainBlock("water-still-7");
        Assert.False(BlockMutationCapture.TryCaptureState(world, position, out _));
        fluid = air;
        decors = new Dictionary<int, Block> { [0] = PlainBlock("plaster") };
        Assert.False(BlockMutationCapture.TryCaptureState(world, position, out _));
        decors = null;
        blockEntity = new SyntheticTreeBlockEntity();
        Assert.False(BlockMutationCapture.TryCaptureState(world, position, out _));
    }

    [Fact]
    public void Actual_microblock_and_chisel_trees_canonicalize_material_ids_to_asset_codes_deterministically() {
        Block material = PlainBlock("rock-granite");
        material.BlockId = 41;
        IWorldAccessor world = MaterialRegistryWorld(material);
        var position = new BlockPos(1234, 67, -987, 5);

        foreach (BlockEntityMicroBlock entity in new BlockEntityMicroBlock[] {
            MicroblockEntity(new BlockMicroBlock { Code = new AssetLocation("game", "microblock"), BlockId = 700 }, position, material.Id),
            ChiselEntity(new BlockChisel { Code = new AssetLocation("game", "chiseledblock"), BlockId = 701 }, position, material.Id)
        }) {
            string assetCode = entity.Block.Code.ToString();
            Assert.True(MicroblockTreeCodec.TryCapture(entity, world, position, assetCode, out byte[] first));
            Assert.True(MicroblockTreeCodec.TryCapture(entity, world, position, assetCode, out byte[] second));
            Assert.Equal(first, second);

            TreeAttribute decoded = TreeAttribute.CreateFromBytes(first);
            var materials = Assert.IsType<StringArrayAttribute>(decoded["materials"]);
            Assert.Equal(new[] { "game:rock-granite" }, materials.value);
            Assert.IsNotType<IntArrayAttribute>(decoded["materials"]);
            Assert.False(decoded.HasAttribute("decorIds"));
            Assert.False(decoded.HasAttribute("decorRot"));
            Assert.Equal(position.X, decoded.GetInt("posx"));
            Assert.Equal(position.InternalY, decoded.GetInt("posy"));
            Assert.Equal(position.Z, decoded.GetInt("posz"));
            Assert.Equal(assetCode, decoded.GetString("blockCode"));
            Assert.Equal(new[] { material.Id }, BlockEntityMicroBlock.MaterialIdsFromAttributes(decoded, world));
        }
    }

    [Fact]
    public void Microblock_codec_rejects_decor_registry_references_and_unresolvable_or_excess_materials() {
        Block material = PlainBlock("rock-granite");
        material.BlockId = 41;
        IWorldAccessor world = MaterialRegistryWorld(material);
        var position = new BlockPos(4, 5, 6, 2);
        var owner = new BlockMicroBlock { Code = new AssetLocation("game", "microblock"), BlockId = 700 };

        BlockEntityMicroBlock decorated = MicroblockEntity(owner, position, material.Id);
        decorated.DecorIds = new[] { 0, 0, material.Id, 0, 0, 0 };
        Assert.False(MicroblockTreeCodec.TryCapture(decorated, world, position, "game:microblock", out _));

        BlockEntityMicroBlock rotatedDecor = MicroblockEntity(owner, position, material.Id);
        rotatedDecor.DecorRotations = 1;
        Assert.False(MicroblockTreeCodec.TryCapture(rotatedDecor, world, position, "game:microblock", out _));

        BlockEntityMicroBlock missingMaterial = MicroblockEntity(owner, position, 9999);
        Assert.False(MicroblockTreeCodec.TryCapture(missingMaterial, world, position, "game:microblock", out _));

        BlockEntityMicroBlock invalidMaterialIndex = MicroblockEntity(owner, position, material.Id);
        invalidMaterialIndex.VoxelCuboids.Clear();
        invalidMaterialIndex.VoxelCuboids.Add(BlockEntityMicroBlock.ToUint(0, 0, 0, 16, 16, 16, 1));
        Assert.False(MicroblockTreeCodec.TryCapture(invalidMaterialIndex, world, position, "game:microblock", out _));

        BlockEntityMicroBlock tooManyMaterials = MicroblockEntity(owner, position, material.Id);
        tooManyMaterials.BlockIds = Enumerable.Repeat(material.Id, MicroblockTreeCodec.MaximumMaterials + 1).ToArray();
        Assert.False(MicroblockTreeCodec.TryCapture(tooManyMaterials, world, position, "game:microblock", out _));
    }

    private static int SeamSubscriberCount(string eventName) {
        FieldInfo field = Assert.IsAssignableFrom<FieldInfo>(typeof(RollbackSeams).GetField(eventName, BindingFlags.Static | BindingFlags.NonPublic));
        return (field.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private static Block AirBlock() => new() { Code = new AssetLocation("game", "air") };

    private static Block PlainBlock(string path) => new() {
        Code = new AssetLocation("game", path),
        SideSolid = new SmallBoolArray(SmallBoolArray.OnAllSides),
        MatterState = EnumMatterState.Solid
    };

    private static BlockEntityMicroBlock MicroblockEntity(BlockMicroBlock owner, BlockPos position, int materialId) {
        return PopulateMicroblock(new BlockEntityMicroBlock(), owner, position, materialId);
    }

    private static BlockEntityChisel ChiselEntity(BlockChisel owner, BlockPos position, int materialId) {
        var entity = PopulateMicroblock(new BlockEntityChisel(), owner, position, materialId);
        entity.AvailMaterialQuantities = new ushort[] { 4096 };
        return entity;
    }

    private static T PopulateMicroblock<T>(T entity, Block owner, BlockPos position, int materialId)
        where T : BlockEntityMicroBlock {
        entity.Block = owner;
        entity.Pos = position.Copy();
        entity.BlockIds = new[] { materialId };
        entity.DecorIds = null!;
        entity.DecorRotations = 0;
        entity.BlockName = "fixture";
        entity.VoxelCuboids.Add(BlockEntityMicroBlock.ToUint(0, 0, 0, 16, 16, 16, 0));
        return entity;
    }

    private static IWorldAccessor MaterialRegistryWorld(Block material) {
        return Proxy<IWorldAccessor>((method, arguments) => {
            if (method.Name != nameof(IWorldAccessor.GetBlock) || arguments?.Length != 1) return Default(method.ReturnType);
            return arguments[0] switch {
                int id when id == material.Id => material,
                AssetLocation code when code.Domain == material.Code.Domain && code.Path == material.Code.Path => material,
                _ => null
            };
        });
    }

    private static T Proxy<T>(System.Func<MethodInfo, object?[]?, object?> invoke) where T : class {
        T proxy = DispatchProxy.Create<T, TestDispatchProxy>();
        ((TestDispatchProxy)(object)proxy).InvokeHandler = invoke;
        return proxy;
    }

    private static object? Default(Type type) => type == typeof(void) || !type.IsValueType ? null : RuntimeHelpers.GetUninitializedObject(type);

    public class TestDispatchProxy : DispatchProxy {
        public System.Func<MethodInfo, object?[]?, object?> InvokeHandler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            return InvokeHandler(targetMethod!, args);
        }
    }

    private sealed class DerivedPlainBlock : Block { }

    private sealed class SyntheticTreeBlockEntity : BlockEntity { }

    private static MethodInfo PatchMethod(Assembly assembly, string typeName, string methodName) {
        Type patchType = Assert.IsAssignableFrom<Type>(assembly.GetType(typeName, true));
        return Assert.IsAssignableFrom<MethodInfo>(patchType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static));
    }

    private static void PatchAndAssertOwned(Harmony harmony, MethodInfo? original, MethodInfo prefix, MethodInfo postfix, MethodInfo? finalizer = null) {
        Assert.NotNull(original);
        harmony.Patch(
            original,
            prefix: new HarmonyMethod(prefix),
            postfix: new HarmonyMethod(postfix),
            finalizer: finalizer == null ? null : new HarmonyMethod(finalizer)
        );

        HarmonyLib.Patches? patchInfo = Harmony.GetPatchInfo(original);
        Assert.Contains(patchInfo!.Prefixes, patch => patch.owner == harmony.Id && patch.PatchMethod == prefix);
        Assert.Contains(patchInfo.Postfixes, patch => patch.owner == harmony.Id && patch.PatchMethod == postfix);
        if (finalizer != null) Assert.Contains(patchInfo.Finalizers, patch => patch.owner == harmony.Id && patch.PatchMethod == finalizer);
    }
}
