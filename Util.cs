using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;

namespace GriefWarden;

public static class Util {
    public static string? GetPlayerCurrentItemstackName(IPlayer player) {
        IPlayerInventoryManager invManager = player.InventoryManager;
        if (invManager != null) {
            ItemSlot activeSlot = invManager.ActiveHotbarSlot;
            if (activeSlot != null && activeSlot.Itemstack != null)
                return activeSlot.Itemstack.GetName();
        }
        return null;
    }
}
