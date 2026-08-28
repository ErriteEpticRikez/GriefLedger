using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using GriefLedger.Rollback;

namespace GriefLedger;

public class Commands : IDisposable {
    internal const int DefaultExactRollbackRadius = 5;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private int disposed;
    public Commands() {
        Main.API.Permissions.RegisterPrivilege("griefledger", "Use GriefLedger commands.", true);

        Main.API.RegisterCommand("rollbackbreaks", "Exactly revert captured block breaks by a player within a radius (default: 5).", "(-u PLAYERUID | -p USERNAME) -r #", new ServerChatCommandDelegate(this.OnRollbackBreaksCommand), "griefledger");
        Main.API.RegisterCommand("rollbackblocks", "Exactly revert captured block breaks, placements, and vanilla chisel mutations by a player within a radius (default: 5).", "(-u PLAYERUID | -p USERNAME) -r #", new ServerChatCommandDelegate(this.OnRollbackBlocksCommand), "griefledger");
        Main.API.RegisterCommand("blocklog", "Inspect block logs at the looked-at block, or around you when a radius is supplied.", "-r # -p #", new ServerChatCommandDelegate(this.OnBlockLogCommand), "griefledger");
        Main.API.RegisterCommand("entitylog", "Inspect entity logs around you or for an entity ID.", "(-r # OR -e ENTITYID) -p #", new ServerChatCommandDelegate(this.OnEntityLogCommand), "griefledger");
        Main.API.RegisterCommand("containerlog", "Inspect logs for the looked-at container.", "-p #", new ServerChatCommandDelegate(this.OnContainerLogCommand), "griefledger");
        Main.API.RegisterCommand("tpboatid", "Teleport a boat to you by entity ID.", "-e ENTITYID", new ServerChatCommandDelegate(this.OnTPBoatID), "griefledger");
    }

    private void OnRollbackBreaksCommand(IServerPlayer player, int groupId, CmdArgs args) {
        BeginExactRollback(player, groupId, args, breakOnly: true);
    }

    private void OnRollbackBlocksCommand(IServerPlayer player, int groupId, CmdArgs args) {
        BeginExactRollback(player, groupId, args, breakOnly: false);
    }

    private void BeginExactRollback(IServerPlayer player, int groupId, CmdArgs args, bool breakOnly) {
        if (Volatile.Read(ref disposed) != 0) return;
        if (!Main.ExactRollbackAvailable || Main.ExactBlockRollbackService == null) {
            Send(player, groupId,
                "Exact rollback is disabled because exact capture is unavailable. Legacy audit records remain available for inspection, but are not rollbackable.",
                EnumChatType.CommandError);
            return;
        }

        var words = new List<string>();
        while (args.Length > 0) words.Add(args.PopWord());
        if (!ExactRollbackCommandParser.TryParse(words, out ExactRollbackCommandOptions? options, out string? error)) {
            Send(player, groupId, error!, EnumChatType.CommandError);
            return;
        }

        // AsBlockPos preserves the entity's dimension and local Y reconstructed from InternalY.
        // Do not use ToLocalPosition(): that is spawn-relative and dimension-unaware.
        BlockPos center = player.Entity.Pos.AsBlockPos;
        BlockRollbackService service = Main.ExactBlockRollbackService;
        string operatorUid = player.PlayerUID;
        string? operatorName = player.PlayerName;
        Send(player, groupId, "Exact rollback queued; the server will report the guarded replay result here.", EnumChatType.Notification);
        _ = ObserveExactRollbackAsync(service, player, groupId, operatorUid, operatorName, center, options!, breakOnly);
    }

    private async Task ObserveExactRollbackAsync(BlockRollbackService service, IServerPlayer player, int groupId,
        string operatorUid, string? operatorName, BlockPos center, ExactRollbackCommandOptions options, bool breakOnly) {
        try {
            string targetUid = options.PlayerUid ?? await ResolveTargetUidAsync(options.PlayerName!, lifetimeCancellation.Token)
                .ConfigureAwait(false);
            var request = new BlockRollbackRequest {
                OperatorPlayerUid = operatorUid,
                OperatorPlayerName = operatorName,
                TargetPlayerUid = targetUid,
                Dimension = center.dimension,
                CenterX = center.X,
                CenterY = center.Y,
                CenterZ = center.Z,
                Radius = options.Radius,
                BreakOnly = breakOnly
            };
            BlockRollbackResult result = await service.RequestAsync(request, lifetimeCancellation.Token).ConfigureAwait(false);
            if (result.OperationFailureCode == BlockRollbackFailureCodes.BatchStopped
                || result.UnprocessedSourceCount != 0) {
                Send(player, groupId, "Exact rollback stopped before every selected source was processed ("
                    + (result.OperationFailureCode ?? BlockRollbackFailureCodes.BatchStopped) + "). "
                    + FormatExactRollbackResult(result), EnumChatType.CommandError);
            }
            else if (result.OperationFailureCode != null) {
                Send(player, groupId, "Exact rollback did not run (" + result.OperationFailureCode + "). "
                    + FormatExactRollbackResult(result), EnumChatType.CommandError);
            }
            else {
                Send(player, groupId, "Exact rollback complete. " + FormatExactRollbackResult(result),
                    result.FailedSourceIds.Count == 0 ? EnumChatType.CommandSuccess : EnumChatType.Notification);
            }
        }
        catch (BlockRollbackOperationalException exception) {
            Send(player, groupId, "Exact rollback stopped after a durable-operation failure ("
                + exception.PartialResult.OperationFailureCode + "). Partial result: "
                + FormatExactRollbackResult(exception.PartialResult), EnumChatType.CommandError);
            LogFailure("Exact rollback command stopped after an operational failure", exception);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) {
            // Mod shutdown/reload intentionally cancels outstanding command work without using a stale API.
        }
        catch (ExactRollbackCommandException exception) {
            Send(player, groupId, exception.Message, EnumChatType.CommandError);
        }
        catch (BlockRollbackLimitExceededException exception) {
            Send(player, groupId, "Exact rollback was not run: " + exception.Message, EnumChatType.CommandError);
        }
        catch (Exception exception) {
            Send(player, groupId, "Exact rollback failed before any guarded replay result was available. See the server log.",
                EnumChatType.CommandError);
            LogFailure("Exact rollback command failed", exception);
        }
    }

    private async Task<string> ResolveTargetUidAsync(string playerName, CancellationToken cancellationToken) {
        BlockMutationPlayerNameResolution resolution = await Main.Database
            .ResolveBlockMutationPlayerByNameAsync(playerName, cancellationToken).ConfigureAwait(false);
        return resolution.Kind switch {
            BlockMutationPlayerNameResolutionKind.Unique when !string.IsNullOrWhiteSpace(resolution.Player?.PlayerUid)
                => resolution.Player.PlayerUid!,
            BlockMutationPlayerNameResolutionKind.Unique => throw new ExactRollbackCommandException(
                "That name resolves only to a legacy audit identity without an immutable UID; it is inspection-only and cannot be rolled back."),
            BlockMutationPlayerNameResolutionKind.NotFound => throw new ExactRollbackCommandException(
                "No exact-ledger player matches that name. Use -u PLAYERUID when possible; legacy audit rows are inspection-only."),
            _ => throw new ExactRollbackCommandException(
                "That player name is ambiguous. Use -u PLAYERUID; names are only accepted when they resolve to one immutable UID.")
        };
    }

    internal static string FormatExactRollbackResult(BlockRollbackResult result) {
        ArgumentNullException.ThrowIfNull(result);
        string reasons = result.ReasonCounts.Count == 0 ? "none" : string.Join(", ", result.ReasonCounts
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + pair.Value.ToString(CultureInfo.InvariantCulture)));
        return "cutoff #" + result.CutoffId.ToString(CultureInfo.InvariantCulture)
            + "; history through #" + result.HistoryThroughId.ToString(CultureInfo.InvariantCulture)
            + "; selected=" + result.TotalSelectedSourceCount.ToString(CultureInfo.InvariantCulture)
            + ", processed=" + result.Attempts.Count.ToString(CultureInfo.InvariantCulture)
            + ", unprocessed=" + result.UnprocessedSourceCount.ToString(CultureInfo.InvariantCulture)
            + "; succeeded=" + result.SucceededSourceIds.Count.ToString(CultureInfo.InvariantCulture)
            + ", failed=" + result.FailedSourceIds.Count.ToString(CultureInfo.InvariantCulture)
            + ", skipped=" + result.SkippedSourceIds.Count.ToString(CultureInfo.InvariantCulture)
            + "; reasons: " + reasons + ".";
    }

    private void Send(IServerPlayer player, int groupId, string message, EnumChatType type) {
        if (Volatile.Read(ref disposed) != 0) return;
        try {
            Main.API.Event.EnqueueMainThreadTask(() => {
                if (Volatile.Read(ref disposed) != 0) return;
                InvokeMessageCallbackSafely(
                    () => Main.API.SendMessage(player, groupId, message, type),
                    exception => LogFailure("Could not send exact rollback command message", exception)
                );
            }, "griefledger-rollback-command-message");
        }
        catch (Exception exception) {
            LogFailure("Could not schedule exact rollback command message", exception);
        }
    }

    private static void LogFailure(string message, Exception exception) {
        try { Main.API.Logger.Error("GriefLedger: {0}: {1}", message, exception); }
        catch { /* A torn-down API cannot safely receive an error callback. */ }
    }

    internal static void InvokeMessageCallbackSafely(Action sendMessage, Action<Exception> reportFailure) {
        ArgumentNullException.ThrowIfNull(sendMessage);
        ArgumentNullException.ThrowIfNull(reportFailure);
        try {
            sendMessage();
        }
        catch (Exception exception) {
            try { reportFailure(exception); }
            catch { /* Reporting a stale API must not throw from the server task either. */ }
        }
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetimeCancellation.Cancel();
        // Do not dispose the CTS here: an already-running async command may still read its
        // token while observing cancellation, and must finish silently during mod teardown.
    }

    private void OnBlockLogCommand(IServerPlayer player, int groupId, CmdArgs args) {
        //int radiusToUse = (int)args.PopInt(0);
        int pageNum = 1;
        int radiusToUse = 0;
        while (args.Length > 0) {
            string argFlag = args.PopWord();
            switch (argFlag) {
                case "-p":
                    pageNum = (int)args.PopInt(1);
                    break;
                case "-r":
                    radiusToUse = (int)args.PopInt(0);
                    break;
            }
        }

        Vec3i positionToUse;
        if (radiusToUse > 0) {
            positionToUse = player.Entity.Pos.XYZ.AsBlockPos.ToLocalPosition(Main.API);
        }
        else {
            BlockSelection blockSel = player.CurrentBlockSelection;
            if (blockSel == null) {
                Main.API.SendMessage(player, groupId, "Look at a block first or specify a radius.", EnumChatType.CommandError);
                return;
            }
            positionToUse = blockSel.Position.ToLocalPosition(Main.API);
        }

        Main.Database.CheckBlockLog(pageNum, player, groupId, positionToUse.X, positionToUse.Y, positionToUse.Z, radiusToUse);
    }

    private void OnEntityLogCommand(IServerPlayer player, int groupId, CmdArgs args) {
        int pageNum = 1;
        int radiusToUse = 5;
        string? entityID = null;
        while (args.Length > 0) {
            string argFlag = args.PopWord();
            switch (argFlag) {
                case "-p":
                    pageNum = (int)args.PopInt(1);
                    break;
                case "-r":
                    radiusToUse = (int)args.PopInt(5);
                    break;
                case "-e":
                    entityID = args.PopWord();
                    break;
            }
        }

        if (entityID == null) {
            Vec3i playerPosition = player.Entity.Pos.XYZ.AsBlockPos.ToLocalPosition(Main.API);

            Main.Database.CheckEntityLog(pageNum, player, groupId, playerPosition.X, playerPosition.Y, playerPosition.Z, radiusToUse);
        }
        else {
            Main.Database.CheckEntityLogWithEntityID(pageNum, player, groupId, entityID);
        }
    }

    private void OnContainerLogCommand(IServerPlayer player, int groupId, CmdArgs args) {
        int pageNum = 1;
        while (args.Length > 0) {
            string argFlag = args.PopWord();
            switch (argFlag) {
                case "-p":
                    pageNum = (int)args.PopInt(1);
                    break;
            }
        }

        BlockSelection blockSel = player.CurrentBlockSelection;
        if (blockSel != null) {
            BlockEntity blockEnt = Main.API.World.BlockAccessor.GetBlockEntity(blockSel.Position);
            if (blockEnt is IBlockEntityContainer container) {
                IInventory inventory = container.Inventory;
                Main.Database.CheckContainerLog(pageNum, player, groupId, inventory.InventoryID);
                return;
            }
            // Special case for toolracks
            if (blockEnt is BlockEntityToolrack toolRack) {
                IInventory inventory = toolRack.inventory;
                Main.Database.CheckContainerLog(pageNum, player, groupId, inventory.InventoryID);
                return;
            }
        }

        // mountedbaginv-(slotnum)-(entityID)
        // elks have slot num 6 for saddlebags
        // sailboats have slot nums 5-12 for chests
        // rafts have slot nums 0-1 for chests
        EntitySelection entitySel = player.CurrentEntitySelection;
        if (entitySel != null) {
            var behavior = entitySel.Entity.GetBehavior<EntityBehaviorAttachable>();
            if (behavior != null) {
                InventoryBase inventory = behavior.Inventory;
                List<string> containerids = new();
                for (int i = 0; i < inventory.Count; i++)
                    containerids.Add("mountedbaginv-" + i + "-" + entitySel.Entity.EntityId);
                Main.Database.CheckContainerLog(pageNum, player, groupId, containerids);
                return;
            }
        }

        Main.API.SendMessage(player, groupId, "Look at a container, or an entity that can have a container, first. If you're looking at a double chest/trunk, try the other block.", EnumChatType.CommandError);
    }

    private void tryTPEntityAsBoat(IServerPlayer player, int groupId, Entity entityToTP) {
        if (entityToTP is EntityBoat boatEntity) {
            boatEntity.TeleportTo(player.Entity.Pos.XYZ);
            Main.API.SendMessage(player, groupId, "Teleported boat with ID " + boatEntity.EntityId + " to your position.", EnumChatType.CommandSuccess);
            return;
        }
        Main.API.SendMessage(player, groupId, "That entity is not a boat.", EnumChatType.CommandError);
    }
    private void OnTPBoatID(IServerPlayer player, int groupId, CmdArgs args) {
        long entityID = 0;
        while (args.Length > 0) {
            string argFlag = args.PopWord();
            switch (argFlag) {
                case "-e":
                    entityID = Convert.ToInt64(args.PopWord());
                    break;
            }
        }
        if (entityID == 0) {
            Main.API.SendMessage(player, groupId, "Could not convert to proper entity ID. Proper usage: /tpboatid -e ENTITYID", EnumChatType.CommandError);
            return;
        }

        if (Main.API.World.LoadedEntities.ContainsKey(entityID)) {
            Entity entityToTP = Main.API.World.LoadedEntities[entityID];

            tryTPEntityAsBoat(player, groupId, entityToTP);
        }
        else {
            (int, int, int)? rawEntityPosition = Main.Database.GetLastEntityCoordsLog(entityID.ToString());
            if (rawEntityPosition == null) {
                Main.API.SendMessage(player, groupId, "No entity logs found with that ID. Did you enter the ID in wrong?", EnumChatType.CommandError);
                return;
            }

            Vec3d entityPosition = new(rawEntityPosition.Value.Item1 + Main.API.World.DefaultSpawnPosition.X, rawEntityPosition.Value.Item2, rawEntityPosition.Value.Item3 + Main.API.World.DefaultSpawnPosition.Z);

            Main.API.WorldManager.LoadChunkColumnPriority((int)entityPosition.X / 32, (int)entityPosition.Z / 32, new ChunkLoadOptions {
                OnLoaded = () => {
                    // Check again to see if entity is loaded, just in case
                    if (!Main.API.World.LoadedEntities.ContainsKey(entityID)) {
                        Main.API.SendMessage(player, groupId, "Entity position found, but entity still not loaded in. Something is wrong.", EnumChatType.CommandError);
                        return;
                    }

                    Entity entityToTP = Main.API.World.LoadedEntities[entityID];

                    tryTPEntityAsBoat(player, groupId, entityToTP);
                }
            });
        }
    }
}

/// <summary>Strict, side-effect-free parser for the two exact rollback commands.</summary>
internal sealed record ExactRollbackCommandOptions(string? PlayerUid, string? PlayerName, int Radius);

internal sealed class ExactRollbackCommandException : Exception {
    internal ExactRollbackCommandException(string message) : base(message) { }
}

internal static class ExactRollbackCommandParser {
    internal static bool TryParse(IReadOnlyList<string> words, out ExactRollbackCommandOptions? options,
        out string? error) {
        ArgumentNullException.ThrowIfNull(words);
        string? playerUid = null;
        string? playerName = null;
        int radius = Commands.DefaultExactRollbackRadius;
        bool radiusSpecified = false;
        for (int index = 0; index < words.Count; index++) {
            string flag = words[index] ?? string.Empty;
            if (flag is not "-u" and not "-p" and not "-r") {
                options = null;
                error = "Unknown rollback option '" + flag + "'. Usage: (-u PLAYERUID | -p USERNAME) -r #.";
                return false;
            }
            if (++index >= words.Count || string.IsNullOrWhiteSpace(words[index])) {
                options = null;
                error = "Rollback option '" + flag + "' requires a value.";
                return false;
            }
            string value = words[index];
            switch (flag) {
                case "-u":
                    if (playerUid != null) {
                        options = null;
                        error = "Specify -u PLAYERUID only once.";
                        return false;
                    }
                    playerUid = value;
                    break;
                case "-p":
                    if (playerName != null) {
                        options = null;
                        error = "Specify -p USERNAME only once.";
                        return false;
                    }
                    playerName = value;
                    break;
                case "-r":
                    if (radiusSpecified) {
                        options = null;
                        error = "Specify -r only once.";
                        return false;
                    }
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out radius)
                        || radius < 0 || radius > BlockRollbackLimits.MaximumRadius) {
                        options = null;
                        error = "Radius must be an integer from 0 to "
                            + BlockRollbackLimits.MaximumRadius.ToString(CultureInfo.InvariantCulture) + ".";
                        return false;
                    }
                    radiusSpecified = true;
                    break;
            }
        }
        if ((playerUid == null && playerName == null) || (playerUid != null && playerName != null)) {
            options = null;
            error = "Specify exactly one target: -u PLAYERUID (recommended) or -p USERNAME.";
            return false;
        }
        options = new ExactRollbackCommandOptions(playerUid, playerName, radius);
        error = null;
        return true;
    }
}
