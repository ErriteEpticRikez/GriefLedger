using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace GriefWarden.Hooks;

public class BlockHooks {
    public BlockHooks() {
        Main.API.Event.DidBreakBlock += this.OnDidBlockBreak;
        Main.API.Event.DidPlaceBlock += this.OnDidBlockPlace;
        Main.API.Event.DidUseBlock += this.OnDidBlockUse;
    }

    private void OnDidBlockBreak(IServerPlayer player, int oldBlockID, BlockSelection blockSel) {
        if (blockSel == null)
            return;

        Block block = Main.API.World.BlockAccessor.GetBlock(oldBlockID);

        string? playerName = null;
        string? playerUID = null;
        string? itemstack = null;
        if (player != null) {
            playerName = player.PlayerName;
            playerUID = player.PlayerUID;

            IPlayerInventoryManager invManager = player.InventoryManager;
            if (invManager != null) {
                ItemSlot activeSlot = invManager.ActiveHotbarSlot;
                if (activeSlot != null && activeSlot.Itemstack != null)
                    itemstack = activeSlot.Itemstack.GetName();
            }
        }

        Vec3i blockPosition = blockSel.Position.ToLocalPosition(Main.API);
        
        Main.Database.AddBlockLog(playerName, playerUID, "BROKE", block.ToString(), itemstack, blockPosition.X, blockPosition.Y, blockPosition.Z, null);
    }

    private void OnDidBlockPlace(IServerPlayer player, int oldBlockID, BlockSelection blockSel, ItemStack withItemStack) {
        if (blockSel == null)
            return;

        Block block = Main.API.World.BlockAccessor.GetBlock(blockSel.Position);

        string? playerName = null;
        string? playerUID = null;
        if (player != null) {
            playerName = player.PlayerName;
            playerUID = player.PlayerUID;
        }

        Vec3i blockPosition = blockSel.Position.ToLocalPosition(Main.API);
        Main.Database.AddBlockLog(playerName, playerUID, "PLACED", block.ToString(), null, blockPosition.X, blockPosition.Y, blockPosition.Z, oldBlockID);
    }

    private void OnDidBlockUse(IServerPlayer player, BlockSelection blockSel) {
        if (blockSel == null)
            return;

        Block block = Main.API.World.BlockAccessor.GetBlock(blockSel.Position);
        if (block == null)
            return;

        string? playerName = null;
        string? playerUID = null;
        string? itemstack = null;
        if (player != null) {
            playerName = player.PlayerName;
            playerUID = player.PlayerUID;

            IPlayerInventoryManager invManager = player.InventoryManager;
            if (invManager != null) {
                ItemSlot activeSlot = invManager.ActiveHotbarSlot;
                if (activeSlot != null && activeSlot.Itemstack != null)
                    itemstack = activeSlot.Itemstack.GetName();
            }
        }

        Vec3i blockPosition = blockSel.Position.ToLocalPosition(Main.API);
        Main.Database.AddBlockLog(playerName, playerUID, "USED", block.ToString(), itemstack, blockPosition.X, blockPosition.Y, blockPosition.Z, null);
    }
}
