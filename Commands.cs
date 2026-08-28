using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace GriefLedger;

public class Commands {
    public Commands() {
        Main.API.Permissions.RegisterPrivilege("griefledger", "Use GriefLedger commands.", true);

        Main.API.RegisterCommand("rollbackbreaks", "Revert logged block breaks by a player within a radius (default: 5).", "-p USERNAME -r #", new ServerChatCommandDelegate(this.OnRollbackBreaksCommand), "griefledger");
        Main.API.RegisterCommand("blocklog", "Inspect block logs at the looked-at block, or around you when a radius is supplied.", "-r # -p #", new ServerChatCommandDelegate(this.OnBlockLogCommand), "griefledger");
        Main.API.RegisterCommand("entitylog", "Inspect entity logs around you or for an entity ID.", "(-r # OR -e ENTITYID) -p #", new ServerChatCommandDelegate(this.OnEntityLogCommand), "griefledger");
        Main.API.RegisterCommand("containerlog", "Inspect logs for the looked-at container.", "-p #", new ServerChatCommandDelegate(this.OnContainerLogCommand), "griefledger");
        Main.API.RegisterCommand("tpboatid", "Teleport a boat to you by entity ID.", "-e ENTITYID", new ServerChatCommandDelegate(this.OnTPBoatID), "griefledger");
    }

    private void OnRollbackBreaksCommand(IServerPlayer player, int groupId, CmdArgs args) {
        string? playerName = null;
        int radiusToUse = 5;
        while (args.Length > 0) {
            string argFlag = args.PopWord();
            switch (argFlag) {
                case "-p":
                    playerName = args.PopWord();
                    break;
                case "-r":
                    radiusToUse = (int)args.PopInt(5);
                    break;
            }
        }
        if (playerName == null) {
            Main.API.SendMessage(player, groupId, "You need to specify a player's username with \"-p USERNAME\".", EnumChatType.CommandError);
            return;
        }

        Vec3i playerPosition = player.Entity.Pos.XYZ.AsBlockPos.ToLocalPosition(Main.API);

        Main.Database.RollbackBreaks(player, groupId, playerPosition.X, playerPosition.Y, playerPosition.Z, radiusToUse, playerName);
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
