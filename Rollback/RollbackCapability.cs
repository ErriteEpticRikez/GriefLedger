using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GriefLedger.Patches;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.Server;

namespace GriefLedger.Rollback;

public sealed record RollbackTargetStatus(string Name, string ExpectedSignature, bool Resolved, bool Patched, string? Error);

public sealed record RollbackCapabilityStatus(
    string ExpectedGameVersion,
    string ActualGameVersion,
    bool IsAvailable,
    IReadOnlyList<RollbackTargetStatus> Targets,
    IReadOnlyList<string> Errors
);

public sealed class RollbackCapability : IDisposable {
    public const string SupportedGameVersion = "1.22.7";

    private readonly ICoreServerAPI api;
    private bool subscribedToDidBreak;
    private bool disposed;

    private RollbackCapability(ICoreServerAPI api, RollbackCapabilityStatus status) {
        this.api = api;
        Status = status;
    }

    public RollbackCapabilityStatus Status { get; private set; }
    public bool IsAvailable => !disposed && Status.IsAvailable;

    public static RollbackCapability Initialize(ICoreServerAPI api, Harmony harmony) {
        try {
            return InitializeCore(api, harmony);
        }
        catch (Exception exception) {
            string actualVersion = GameVersion.ShortGameVersion;
            string error = "capability initialization failed: " + exception.GetBaseException().Message;
            api.Logger.Error(
                "GriefLedger: Exact rollback is unavailable; database, legacy audit, and existing commands remain enabled. {0}",
                error
            );
            return new RollbackCapability(api, new RollbackCapabilityStatus(
                SupportedGameVersion,
                actualVersion,
                false,
                Array.Empty<RollbackTargetStatus>(),
                new[] { error }
            ));
        }
    }

    public static RollbackTargetStatus EnsureLegacyChiselAuditPatch(Harmony harmony) {
        TargetBinding binding;
        try {
            binding = ResolveItemChiselConversion();
            ValidatePatchMethods(binding);
            if (binding.Original != null) TryApplyAndVerify(harmony, binding);
        }
        catch (Exception exception) {
            binding = new TargetBinding("item-chisel-legacy-audit", "ItemChisel.OnHeldInteractStart exact 1.22.7 signature") {
                Error = "legacy audit patch initialization failed: " + exception.GetBaseException().Message
            };
        }

        return new RollbackTargetStatus(
            binding.Name,
            binding.ExpectedSignature,
            binding.Original != null,
            binding.Patched,
            binding.Error
        );
    }

    private static RollbackCapability InitializeCore(ICoreServerAPI api, Harmony harmony) {
        string actualVersion = GameVersion.ShortGameVersion;
        var errors = new List<string>();
        if (!string.Equals(actualVersion, SupportedGameVersion, StringComparison.Ordinal)) {
            errors.Add($"expected Vintage Story {SupportedGameVersion}, found {actualVersion}");
        }

        List<TargetBinding> bindings = ResolveBindings();
        foreach (TargetBinding binding in bindings) {
            if (binding.Error != null) errors.Add(binding.Name + ": " + binding.Error);
        }

        foreach (TargetBinding binding in bindings.Where(binding => binding.Original != null)) {
            TryApplyAndVerify(harmony, binding);
            if (binding.Error != null && !errors.Any(error => error.StartsWith(binding.Name + ":", StringComparison.Ordinal))) {
                errors.Add(binding.Name + ": " + binding.Error);
            }
        }

        var targetStatuses = bindings.Select(binding => new RollbackTargetStatus(
            binding.Name,
            binding.ExpectedSignature,
            binding.Original != null,
            binding.Patched,
            binding.Error
        )).ToArray();
        bool available = errors.Count == 0 && targetStatuses.All(target => target.Resolved && target.Patched);
        var capability = new RollbackCapability(api, new RollbackCapabilityStatus(
            SupportedGameVersion,
            actualVersion,
            available,
            targetStatuses,
            errors.ToArray()
        ));

        if (available) {
            api.Event.DidBreakBlock += PlayerBlockMutationPatch.OnDidBreakBlock;
            capability.subscribedToDidBreak = true;
            api.Logger.Notification("GriefLedger: Exact rollback mutation capture is available for Vintage Story {0}.", actualVersion);
        }
        else {
            api.Logger.Error(
                "GriefLedger: Exact rollback is unavailable; database, legacy audit, and existing commands remain enabled. {0}",
                string.Join("; ", errors)
            );
        }

        return capability;
    }

    public static RollbackCapabilityStatus Probe() {
        string actualVersion = GameVersion.ShortGameVersion;
        List<TargetBinding> bindings = ResolveBindings();
        var errors = new List<string>();
        if (!string.Equals(actualVersion, SupportedGameVersion, StringComparison.Ordinal)) {
            errors.Add($"expected Vintage Story {SupportedGameVersion}, found {actualVersion}");
        }
        errors.AddRange(bindings.Where(binding => binding.Error != null).Select(binding => binding.Name + ": " + binding.Error));
        RollbackTargetStatus[] targets = bindings.Select(binding => new RollbackTargetStatus(
            binding.Name,
            binding.ExpectedSignature,
            binding.Original != null,
            false,
            binding.Error
        )).ToArray();
        // Probe resolves signatures only; availability additionally requires owned Harmony patches at server startup.
        return new RollbackCapabilityStatus(SupportedGameVersion, actualVersion, false, targets, errors);
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        PlayerBlockMutationPatch.FlushPendingBreaks();
        if (subscribedToDidBreak) {
            api.Event.DidBreakBlock -= PlayerBlockMutationPatch.OnDidBreakBlock;
            subscribedToDidBreak = false;
        }
        Status = Status with { IsAvailable = false };
    }

    private static List<TargetBinding> ResolveBindings() {
        List<TargetBinding> bindings = [
            ResolvePlayerBlockMutation(),
            ResolveItemChiselConversion(),
            ResolveChiselPacket(),
            ResolveChiselUpdateVoxel()
        ];
        foreach (TargetBinding binding in bindings) ValidatePatchMethods(binding);
        return bindings;
    }

    private static void ValidatePatchMethods(TargetBinding binding) {
        if (binding.Error == null && (binding.Prefix == null || binding.Postfix == null || (binding.RequiresFinalizer && binding.Finalizer == null))) {
            binding.Error = "one or more required GriefLedger Harmony patch methods were not found";
            binding.Original = null;
        }
    }

    private static TargetBinding ResolvePlayerBlockMutation() {
        const string expected = "Vintagestory.Server.ServerSystemBlockSimulation.TryModifyBlockInWorld(Vintagestory.Server.ServerPlayer, Packet_ClientBlockPlaceOrBreak): bool [VintagestoryLib]";
        var binding = new TargetBinding("player-block-mutation", expected) {
            Prefix = AccessTools.Method(typeof(PlayerBlockMutationPatch), nameof(PlayerBlockMutationPatch.Prefix)),
            Postfix = AccessTools.Method(typeof(PlayerBlockMutationPatch), nameof(PlayerBlockMutationPatch.Postfix)),
            Finalizer = AccessTools.Method(typeof(PlayerBlockMutationPatch), nameof(PlayerBlockMutationPatch.Finalizer)),
            RequiresFinalizer = true
        };

        Type owner = typeof(ServerSystemBlockSimulation);
        Type playerType = typeof(ServerPlayer);
        Type commandType = typeof(global::Packet_ClientBlockPlaceOrBreak);
        if (owner.Assembly.GetName().Name != "VintagestoryLib" || playerType.Assembly != owner.Assembly || commandType.Assembly != owner.Assembly) {
            return binding.Fail("declaring or parameter type is not owned by VintagestoryLib");
        }

        MethodInfo? method = AccessTools.DeclaredMethod(owner, "TryModifyBlockInWorld", [playerType, commandType]);
        if (method == null || method.ReturnType != typeof(bool) || method.IsStatic || !method.IsPrivate) return binding.Fail("exact private instance bool signature was not found");
        FieldInfo? x = AccessTools.DeclaredField(commandType, nameof(global::Packet_ClientBlockPlaceOrBreak.X));
        FieldInfo? y = AccessTools.DeclaredField(commandType, nameof(global::Packet_ClientBlockPlaceOrBreak.Y));
        FieldInfo? z = AccessTools.DeclaredField(commandType, nameof(global::Packet_ClientBlockPlaceOrBreak.Z));
        FieldInfo? mode = AccessTools.DeclaredField(commandType, nameof(global::Packet_ClientBlockPlaceOrBreak.Mode));
        if (!IsIntField(x) || !IsIntField(y) || !IsIntField(z) || !IsIntField(mode)) return binding.Fail("packet coordinate/mode fields do not match the 1.22.7 layout");

        binding.Original = method;
        return binding;
    }

    private static TargetBinding ResolveItemChiselConversion() {
        const string expected = "ItemChisel.OnHeldInteractStart(ItemSlot, EntityAgent, BlockSelection, EntitySelection, bool, ref EnumHandHandling): void [VSSurvivalMod]";
        var binding = new TargetBinding("item-chisel-conversion", expected) {
            Prefix = AccessTools.Method(typeof(ItemChiselConversionPatch), nameof(ItemChiselConversionPatch.Prefix)),
            Postfix = AccessTools.Method(typeof(ItemChiselConversionPatch), nameof(ItemChiselConversionPatch.Postfix)),
            Finalizer = AccessTools.Method(typeof(ItemChiselConversionPatch), nameof(ItemChiselConversionPatch.Finalizer)),
            RequiresFinalizer = true
        };
        if (typeof(ItemChisel).Assembly.GetName().Name != "VSSurvivalMod") return binding.Fail("ItemChisel is not owned by VSSurvivalMod");

        MethodInfo? method = AccessTools.DeclaredMethod(typeof(ItemChisel), nameof(ItemChisel.OnHeldInteractStart), [
            typeof(ItemSlot), typeof(EntityAgent), typeof(BlockSelection), typeof(EntitySelection), typeof(bool), typeof(EnumHandHandling).MakeByRefType()
        ]);
        if (method == null || method.ReturnType != typeof(void) || method.IsStatic || !method.IsPublic) return binding.Fail("exact public 1.22.7 signature was not found");
        binding.Original = method;
        return binding;
    }

    private static TargetBinding ResolveChiselPacket() {
        const string expected = "BlockEntityChisel.OnReceivedClientPacket(IPlayer, int, byte[]): void [VSSurvivalMod]";
        var binding = new TargetBinding("blockentity-chisel-server-packet", expected) {
            Prefix = AccessTools.Method(typeof(AuthoritativeChiselPacketPatch), nameof(AuthoritativeChiselPacketPatch.Prefix)),
            Postfix = AccessTools.Method(typeof(AuthoritativeChiselPacketPatch), nameof(AuthoritativeChiselPacketPatch.Postfix)),
            Finalizer = AccessTools.Method(typeof(AuthoritativeChiselPacketPatch), nameof(AuthoritativeChiselPacketPatch.Finalizer)),
            RequiresFinalizer = true
        };
        if (typeof(BlockEntityChisel).Assembly.GetName().Name != "VSSurvivalMod") return binding.Fail("BlockEntityChisel is not owned by VSSurvivalMod");

        MethodInfo? method = AccessTools.DeclaredMethod(typeof(BlockEntityChisel), nameof(BlockEntityChisel.OnReceivedClientPacket), [typeof(IPlayer), typeof(int), typeof(byte[])]);
        if (method == null || method.ReturnType != typeof(void) || method.IsStatic || !method.IsPublic) return binding.Fail("exact public 1.22.7 signature was not found");
        binding.Original = method;
        return binding;
    }

    private static TargetBinding ResolveChiselUpdateVoxel() {
        const string expected = "BlockEntityChisel.UpdateVoxel(IPlayer, ItemSlot, Vec3i, BlockFacing, bool): void [VSSurvivalMod]";
        var binding = new TargetBinding("blockentity-chisel-update-voxel", expected) {
            Prefix = AccessTools.Method(typeof(BlockEntityChiselUpdateVoxelPatch), nameof(BlockEntityChiselUpdateVoxelPatch.Prefix)),
            Postfix = AccessTools.Method(typeof(BlockEntityChiselUpdateVoxelPatch), nameof(BlockEntityChiselUpdateVoxelPatch.Postfix)),
            Finalizer = AccessTools.Method(typeof(BlockEntityChiselUpdateVoxelPatch), nameof(BlockEntityChiselUpdateVoxelPatch.Finalizer)),
            RequiresFinalizer = true
        };
        if (typeof(BlockEntityChisel).Assembly.GetName().Name != "VSSurvivalMod") return binding.Fail("BlockEntityChisel is not owned by VSSurvivalMod");

        MethodInfo? method = AccessTools.DeclaredMethod(typeof(BlockEntityChisel), "UpdateVoxel", [typeof(IPlayer), typeof(ItemSlot), typeof(Vec3i), typeof(BlockFacing), typeof(bool)]);
        if (method == null || method.ReturnType != typeof(void) || method.IsStatic || !method.IsAssembly) return binding.Fail("exact internal 1.22.7 signature was not found");
        binding.Original = method;
        return binding;
    }

    private static void TryApplyAndVerify(Harmony harmony, TargetBinding binding) {
        try {
            if (binding.Original == null) return;
            HarmonyLib.Patches? before = Harmony.GetPatchInfo(binding.Original);
            MethodInfo? missingPrefix = HasOwnedPatch(before?.Prefixes, harmony.Id, binding.Prefix) ? null : binding.Prefix;
            MethodInfo? missingPostfix = HasOwnedPatch(before?.Postfixes, harmony.Id, binding.Postfix) ? null : binding.Postfix;
            MethodInfo? missingFinalizer = HasOwnedPatch(before?.Finalizers, harmony.Id, binding.Finalizer) ? null : binding.Finalizer;
            if (missingPrefix != null || missingPostfix != null || missingFinalizer != null) {
                harmony.Patch(
                    binding.Original,
                    missingPrefix == null ? null : new HarmonyMethod(missingPrefix),
                    missingPostfix == null ? null : new HarmonyMethod(missingPostfix),
                    null,
                    missingFinalizer == null ? null : new HarmonyMethod(missingFinalizer)
                );
            }

            HarmonyLib.Patches? after = Harmony.GetPatchInfo(binding.Original);
            bool verified = HasOwnedPatch(after?.Prefixes, harmony.Id, binding.Prefix)
                && HasOwnedPatch(after?.Postfixes, harmony.Id, binding.Postfix)
                && HasOwnedPatch(after?.Finalizers, harmony.Id, binding.Finalizer);
            if (!verified) {
                binding.Error = "Harmony ownership verification failed after patching";
                return;
            }
            binding.Patched = true;
        }
        catch (Exception exception) {
            binding.Error = "Harmony patch failed: " + exception.GetBaseException().Message;
        }
    }

    private static bool HasOwnedPatch(IEnumerable<Patch>? patches, string owner, MethodInfo? patchMethod) {
        if (patchMethod == null) return true;
        return patches?.Any(patch => patch.owner == owner && patch.PatchMethod == patchMethod) == true;
    }

    private static bool IsIntField(FieldInfo? field) {
        return field != null && !field.IsStatic && field.FieldType == typeof(int);
    }

    private sealed class TargetBinding {
        public TargetBinding(string name, string expectedSignature) {
            Name = name;
            ExpectedSignature = expectedSignature;
        }

        public string Name { get; }
        public string ExpectedSignature { get; }
        public MethodInfo? Original { get; set; }
        public MethodInfo? Prefix { get; init; }
        public MethodInfo? Postfix { get; init; }
        public MethodInfo? Finalizer { get; init; }
        public bool RequiresFinalizer { get; init; }
        public bool Patched { get; set; }
        public string? Error { get; set; }

        public TargetBinding Fail(string error) {
            Error = error;
            return this;
        }
    }
}
