using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace GriefWarden;

public class Commands {
    public Commands() {
        Main.API.Permissions.RegisterPrivilege("griefwarden", "Use GriefWarden commands.", true);

        //Main.API.ChatCommands.Create("blocklog").WithDescription("Inspect block logs at block looked at.").RequiresPrivilege("griefwarden").HandleWith(new CommandDel(this.OnBlockLogCommand));
        Main.API.RegisterCommand("blocklog", "Inspect block logs at block looked at if no radius is specified, or around the player if radius is.", "radius#", new ServerChatCommandDelegate(this.OnBlockLogCommand), "griefwarden");
        Main.API.RegisterCommand("entitylog", "Inspect entity logs in radius around you.", "radius#", new ServerChatCommandDelegate(this.OnEntityLogCommand), "griefwarden");
        Main.API.RegisterCommand("containerlog", "Inspect container logs at container looked at.", "", new ServerChatCommandDelegate(this.OnContainerLogCommand), "griefwarden");
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
        while (args.Length > 0) {
            string argFlag = args.PopWord();
            switch (argFlag) {
                case "-p":
                    pageNum = (int)args.PopInt(1);
                    break;
                case "-r":
                    radiusToUse = (int)args.PopInt(5);
                    break;
            }
        }

        Vec3i playerPosition = player.Entity.Pos.XYZ.AsBlockPos.ToLocalPosition(Main.API);

        Main.Database.CheckEntityLog(pageNum, player, groupId, playerPosition.X, playerPosition.Y, playerPosition.Z, radiusToUse);
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
}
