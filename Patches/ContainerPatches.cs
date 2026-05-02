using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace GriefWarden.Patches;

[HarmonyPatch(typeof(InventoryBase), nameof(InventoryBase.ActivateSlot))]
public class InventoryBasePatch {
    private static bool markForLogging = false;
    private static ItemStack? itemstackTaken = null;
    private static int quantityTaken = -1;

    // CHECK FOR CASES:
    // Check item in mouse slot in PREFIX, if same as dropping slot, then dropping into chest = CANCEL out log
    // THEN check if item is in mouse slot in PREFIX and DIFFERENT from dropping slot, if so, then get quantity taken from PREFIX itemstack.stacksize

    [HarmonyPrefix]
    public static void Prefix(InventoryBase __instance, int slotId, ItemSlot sourceSlot, ref ItemStackMoveOperation op) {
        itemstackTaken = __instance[slotId].Itemstack;
        if (__instance is InventoryBasePlayer || // skip if player inventory being taken from
            itemstackTaken == null || // skip if slot being taken from is null (click on empty slot)
            (sourceSlot.Itemstack != null && sourceSlot.Itemstack.GetName() == itemstackTaken.GetName())) // skip if mouse slot item is same as item being "taken" (dropping item onto item)
            {
            markForLogging = false;
            return;
        }
        markForLogging = true;

        quantityTaken = -1; // Reset quantity taken to test for item already in mouse slot to report correct quantity later if so
        if (sourceSlot.Itemstack != null)
            quantityTaken = itemstackTaken.StackSize;
    }

    [HarmonyPostfix]
    public static void Postfix(InventoryBase __instance, int slotId, ItemSlot sourceSlot, ref ItemStackMoveOperation op) {
        if (!markForLogging)
            return;

        // Set correct quantity if quantityTaken is not set from prefix case check
        if (quantityTaken == -1)
            quantityTaken = op.MovedQuantity;

        Main.Database.AddContainerLog(op.ActingPlayer.PlayerName, op.ActingPlayer.PlayerUID, __instance.InventoryID, itemstackTaken.GetName(), quantityTaken);
    }
}

// Ground storage patch for the 4 corners general storage
[HarmonyPatch(typeof(BlockEntityGroundStorage), nameof(BlockEntityGroundStorage.putOrGetItemSingle))]
public class BlockEntityGroundStorageGeneralPatch {
    private static bool markForLogging = false;
    private static ItemStack? itemstackTaken = null;
    private static int quantityInPrefix = -1;

    [HarmonyPrefix]
    public static void Prefix(BlockEntityGroundStorage __instance, ItemSlot ourSlot, IPlayer player, BlockSelection bs) {
        itemstackTaken = ourSlot.Itemstack;
        if (itemstackTaken == null) {
            markForLogging = false;
            return;
        }
        markForLogging = true;

        quantityInPrefix = itemstackTaken.StackSize;
    }

    [HarmonyPostfix]
    public static void Postfix(BlockEntityGroundStorage __instance, ItemSlot ourSlot, IPlayer player, BlockSelection bs) {
        if (!markForLogging)
            return;

        int quantityTaken = quantityInPrefix;
        if (ourSlot.Itemstack != null) {
            if (ourSlot.Itemstack.StackSize < quantityInPrefix)
                quantityTaken = quantityInPrefix - ourSlot.Itemstack.StackSize;
            else
                return;
        }

        Main.Database.AddContainerLog(player.PlayerName, player.PlayerUID, ourSlot.Inventory.InventoryID, itemstackTaken.GetName(), quantityTaken);
    }
}

// Ground storage patch for the one item specific
[HarmonyPatch(typeof(BlockEntityGroundStorage), nameof(BlockEntityGroundStorage.TryTakeItem))]
public class BlockEntityGroundStorageSpecificPatch {
    private static ItemStack? itemstackTaken = null;
    private static int quantityInPrefix = -1;

    [HarmonyPrefix]
    public static void Prefix(BlockEntityGroundStorage __instance, IPlayer player) {
        itemstackTaken = __instance.Inventory[0].Itemstack;
        quantityInPrefix = itemstackTaken.StackSize;
    }

    [HarmonyPostfix]
    public static void Postfix(BlockEntityGroundStorage __instance, IPlayer player) {
        int quantityTaken = quantityInPrefix;
        if (__instance.Inventory[0].Itemstack != null)
            quantityTaken = quantityInPrefix - __instance.Inventory[0].Itemstack.StackSize;

        Main.Database.AddContainerLog(player.PlayerName, player.PlayerUID, __instance.Inventory.InventoryID, itemstackTaken.GetName(), quantityTaken);
    }
}

[HarmonyPatch(typeof(BlockEntityShelf), "TryTake")]
public class BlockEntityShelfPatch {
    private static ItemStack?[] itemstacksInPrefix = new ItemStack?[8];
    [HarmonyPrefix]
    public static void Prefix(BlockEntityShelf __instance, IPlayer byPlayer, BlockSelection blockSel) {
        for (int i = 0; i < __instance.Inventory.Count; i++)
            itemstacksInPrefix[i] = __instance.Inventory[i].Itemstack;
    }

    [HarmonyPostfix]
    public static void Postfix(BlockEntityShelf __instance, IPlayer byPlayer, BlockSelection blockSel) {
        for (int i = 0; i < __instance.Inventory.Count; i++) {
            if (__instance.Inventory[i].Itemstack != itemstacksInPrefix[i]) {
                Main.Database.AddContainerLog(byPlayer.PlayerName, byPlayer.PlayerUID, __instance.Inventory.InventoryID, itemstacksInPrefix[i].GetName(), 1);
                break;
            }
        }
    }
}