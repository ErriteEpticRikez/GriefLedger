using GriefLedger.Rollback;
using GriefLedger.Patches;
using HarmonyLib;
using System.Reflection;
using Vintagestory.API.Common;
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
