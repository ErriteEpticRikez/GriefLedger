using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace GriefWarden.Patches;

[HarmonyPatch(typeof(InventoryBase), nameof(InventoryBase.ActivateSlot))]
public class InventoryBasePatch {
    // CHECK FOR CASES:
    // Check item in mouse slot in PREFIX, if same as dropping slot, then dropping into chest = CANCEL out log
    // THEN check if item is in mouse slot in PREFIX and DIFFERENT from dropping slot, if so, then get quantity taken from PREFIX itemstack.stacksize

    [HarmonyPrefix]
    public static void Prefix(InventoryBase __instance, int slotId, ItemSlot sourceSlot, out PatchState __state) {
        __state = new PatchState();
        __state.itemstackTaken = __instance[slotId].Itemstack?.Clone();
        __state.mouseItemstack = sourceSlot.Itemstack?.Clone();
        __state.markForLogging = false;
        __state.quantityBefore = -1;
        __state.actionType = "TAKEN";

        if (__instance is InventoryBasePlayer || // skip if player inventory being taken from
            (__state.itemstackTaken == null && __state.mouseItemstack == null)) // skip if slot being taken from is null and mouse slot is null (click on empty slot)
        {
            return;
        }

        __state.markForLogging = true;

        if (__state.itemstackTaken != null && __state.mouseItemstack == null) {
            __state.actionType = "TAKEN";
            __state.quantityBefore = __state.itemstackTaken.StackSize;
        }
        else if (__state.itemstackTaken == null && __state.mouseItemstack != null) {
            __state.actionType = "PLACED";
            __state.quantityBefore = __state.mouseItemstack.StackSize;
        }
        else if (__state.itemstackTaken != null && __state.mouseItemstack != null && __state.itemstackTaken.GetName() != __state.mouseItemstack.GetName()) {
            // Swap items - we'll log both TAKEN and PLACED in postfix
            __state.actionType = "SWAP";
            __state.quantityBefore = __state.itemstackTaken.StackSize;
        }
        else if (__state.itemstackTaken != null && __state.mouseItemstack != null && __state.itemstackTaken.GetName() == __state.mouseItemstack.GetName()) {
            // Same items - we'll log it as "SAME_ITEM" to figure it out in postfix
            __state.actionType = "SAME_ITEM";
            __state.quantityBefore = __state.itemstackTaken.StackSize;
        }
    }

    [HarmonyPostfix]
    public static void Postfix(InventoryBase __instance, int slotId, ItemSlot sourceSlot, ref ItemStackMoveOperation op, PatchState __state) {
        if (!__state.markForLogging)
            return;

        int quantity = op.MovedQuantity;

        string itemName = "";
        if (__state.actionType == "PLACED") {
            itemName = __state.mouseItemstack.GetName();
        }
        else if (__state.actionType == "TAKEN") {
            itemName = __state.itemstackTaken.GetName();
        }

        if (__state.actionType == "SWAP") {
            // Check if the item in the slot is now the one from the mouse
            // Sometimes it's a direct replacement
            var stackAfter = __instance[slotId].Itemstack;
            if (stackAfter != null && stackAfter.GetName() == __state.mouseItemstack.GetName()) {
                // Swap was successful. Log TAKEN for the original item, and PLACED for the new one.
                Main.Database.AddContainerLog(op.ActingPlayer.PlayerName, op.ActingPlayer.PlayerUID, "TAKEN", __instance.InventoryID, __state.itemstackTaken.GetName(), __state.quantityBefore);
                Main.Database.AddContainerLog(op.ActingPlayer.PlayerName, op.ActingPlayer.PlayerUID, "PLACED", __instance.InventoryID, __state.mouseItemstack.GetName(), __state.mouseItemstack.StackSize);
            }
            return;
        }

        if (__state.actionType == "SAME_ITEM") {
            // Need to figure out if it was placed or taken based on slot quantity after vs before
            int quantityAfter = __instance[slotId].Itemstack?.StackSize ?? 0;
            if (quantityAfter > __state.quantityBefore) {
                __state.actionType = "PLACED";
                quantity = quantityAfter - __state.quantityBefore;
                itemName = __state.itemstackTaken.GetName();
            }
            else if (quantityAfter < __state.quantityBefore) {
                __state.actionType = "TAKEN";
                quantity = __state.quantityBefore - quantityAfter;
                itemName = __state.itemstackTaken.GetName();
            }
            else {
                return; // Nothing happened
            }
        }

        if (quantity == 0) return; // Nothing actually happened

        Main.Database.AddContainerLog(op.ActingPlayer.PlayerName, op.ActingPlayer.PlayerUID, __state.actionType, __instance.InventoryID, itemName, quantity);
    }

    public class PatchState {
        public ItemStack? itemstackTaken;
        public ItemStack? mouseItemstack;
        public bool markForLogging;
        public int quantityBefore;
        public string actionType;
    }
}

// Ground storage patch for the 4 corners general storage
[HarmonyPatch(typeof(BlockEntityGroundStorage), nameof(BlockEntityGroundStorage.putOrGetItemSingle))]
public class BlockEntityGroundStorageGeneralPatch {
    [HarmonyPrefix]
    public static void Prefix(BlockEntityGroundStorage __instance, ItemSlot ourSlot, IPlayer player, BlockSelection bs, out PatchState __state) {
        __state = new PatchState();
        __state.itemstackInSlot = ourSlot.Itemstack?.Clone();
        __state.mouseItemstack = player.InventoryManager.ActiveHotbarSlot.Itemstack?.Clone();
        __state.markForLogging = false;
        __state.actionType = "TAKEN";

        if (__state.itemstackInSlot == null && __state.mouseItemstack == null) return;
        __state.markForLogging = true;

        if (__state.itemstackInSlot != null) {
            __state.actionType = "TAKEN";
        }
        else if (__state.mouseItemstack != null) {
            __state.actionType = "PLACED";
        }
    }

    [HarmonyPostfix]
    public static void Postfix(BlockEntityGroundStorage __instance, ItemSlot ourSlot, IPlayer player, BlockSelection bs, PatchState __state) {
        if (!__state.markForLogging) return;

        int quantity = 0;
        string itemName = "";

        if (__state.actionType == "TAKEN") {
            int quantityAfter = ourSlot.Itemstack?.StackSize ?? 0;
            int quantityBefore = __state.itemstackInSlot?.StackSize ?? 0;
            if (quantityAfter < quantityBefore) {
                quantity = quantityBefore - quantityAfter;
                itemName = __state.itemstackInSlot.GetName();
            }
        }
        else if (__state.actionType == "PLACED") {
            int quantityAfter = ourSlot.Itemstack?.StackSize ?? 0;
            int quantityBefore = __state.itemstackInSlot?.StackSize ?? 0;
            if (quantityAfter > quantityBefore) {
                quantity = quantityAfter - quantityBefore;
                itemName = ourSlot.Itemstack.GetName();
            }
        }

        if (quantity > 0) {
            Main.Database.AddContainerLog(player.PlayerName, player.PlayerUID, __state.actionType, ourSlot.Inventory.InventoryID, itemName, quantity);
        }
    }

    public class PatchState {
        public ItemStack? itemstackInSlot;
        public ItemStack? mouseItemstack;
        public bool markForLogging;
        public string actionType;
    }
}

// Ground storage patch for the one item specific
[HarmonyPatch(typeof(BlockEntityGroundStorage), nameof(BlockEntityGroundStorage.TryTakeItem))]
public class BlockEntityGroundStorageSpecificPatch {
    [HarmonyPrefix]
    public static void Prefix(BlockEntityGroundStorage __instance, IPlayer player, out PatchState __state) {
        __state = new PatchState();
        __state.itemstackTaken = __instance.Inventory[0].Itemstack?.Clone();
    }

    [HarmonyPostfix]
    public static void Postfix(BlockEntityGroundStorage __instance, IPlayer player, PatchState __state) {
        if (__state.itemstackTaken == null) return;

        int quantityBefore = __state.itemstackTaken.StackSize;
        int quantityAfter = __instance.Inventory[0].Itemstack?.StackSize ?? 0;

        if (quantityAfter < quantityBefore) {
            int quantityTaken = quantityBefore - quantityAfter;
            Main.Database.AddContainerLog(player.PlayerName, player.PlayerUID, "TAKEN", __instance.Inventory.InventoryID, __state.itemstackTaken.GetName(), quantityTaken);
        }
    }

    public class PatchState {
        public ItemStack? itemstackTaken;
    }
}

[HarmonyPatch(typeof(BlockEntityGroundStorage), nameof(BlockEntityGroundStorage.TryPutItem))]
public class BlockEntityGroundStoragePutSpecificPatch {
    [HarmonyPrefix]
    public static void Prefix(BlockEntityGroundStorage __instance, IPlayer player, out PatchState __state) {
        __state = new PatchState();
        __state.itemstackBefore = __instance.Inventory[0].Itemstack?.Clone();
    }

    [HarmonyPostfix]
    public static void Postfix(BlockEntityGroundStorage __instance, IPlayer player, PatchState __state) {
        var stackAfter = __instance.Inventory[0].Itemstack;
        int quantityBefore = __state.itemstackBefore?.StackSize ?? 0;
        int quantityAfter = stackAfter?.StackSize ?? 0;

        if (quantityAfter > quantityBefore) {
            int quantityPlaced = quantityAfter - quantityBefore;
            Main.Database.AddContainerLog(player.PlayerName, player.PlayerUID, "PLACED", __instance.Inventory.InventoryID, stackAfter.GetName(), quantityPlaced);
        }
    }

    public class PatchState {
        public ItemStack? itemstackBefore;
    }
}

[HarmonyPatch(typeof(BlockEntityShelf), "TryTake")]
public class BlockEntityShelfPatch {
    [HarmonyPrefix]
    public static void Prefix(BlockEntityShelf __instance, IPlayer byPlayer, BlockSelection blockSel, out PatchState __state) {
        __state = new PatchState();
        __state.itemstacksInPrefix = new ItemStack?[__instance.Inventory.Count];
        for (int i = 0; i < __instance.Inventory.Count; i++) {
            __state.itemstacksInPrefix[i] = __instance.Inventory[i].Itemstack?.Clone();
        }
    }

    [HarmonyPostfix]
    public static void Postfix(BlockEntityShelf __instance, IPlayer byPlayer, BlockSelection blockSel, PatchState __state) {
        for (int i = 0; i < __instance.Inventory.Count; i++) {
            ItemStack? stackBefore = __state.itemstacksInPrefix[i];
            ItemStack? stackAfter = __instance.Inventory[i].Itemstack;

            // Simplified check: if it was there and now it's not (or quantity is less)
            if (stackBefore != null && (stackAfter == null || stackAfter.StackSize < stackBefore.StackSize)) {
                int quantity = stackAfter == null ? stackBefore.StackSize : stackBefore.StackSize - stackAfter.StackSize;
                Main.Database.AddContainerLog(byPlayer.PlayerName, byPlayer.PlayerUID, "TAKEN", __instance.Inventory.InventoryID, stackBefore.GetName(), quantity);
                break;
            }
        }
    }

    public class PatchState {
        public ItemStack?[] itemstacksInPrefix;
    }
}

[HarmonyPatch(typeof(BlockEntityShelf), "TryPut")]
public class BlockEntityShelfPutPatch {
    [HarmonyPrefix]
    public static void Prefix(BlockEntityShelf __instance, IPlayer byPlayer, BlockSelection blockSel, out PatchState __state) {
        __state = new PatchState();
        __state.itemstacksInPrefix = new ItemStack?[__instance.Inventory.Count];
        for (int i = 0; i < __instance.Inventory.Count; i++) {
            __state.itemstacksInPrefix[i] = __instance.Inventory[i].Itemstack?.Clone();
        }
    }

    [HarmonyPostfix]
    public static void Postfix(BlockEntityShelf __instance, IPlayer byPlayer, BlockSelection blockSel, PatchState __state) {
        for (int i = 0; i < __instance.Inventory.Count; i++) {
            ItemStack? stackBefore = __state.itemstacksInPrefix[i];
            ItemStack? stackAfter = __instance.Inventory[i].Itemstack;

            if (stackAfter != null && (stackBefore == null || stackAfter.StackSize > stackBefore.StackSize)) {
                int quantity = stackBefore == null ? stackAfter.StackSize : stackAfter.StackSize - stackBefore.StackSize;
                if (byPlayer != null) {
                    Main.Database.AddContainerLog(byPlayer.PlayerName, byPlayer.PlayerUID, "PLACED", __instance.Inventory.InventoryID, stackAfter.GetName(), quantity);
                }
                break;
            }
        }
    }

    public class PatchState {
        public ItemStack?[] itemstacksInPrefix;
    }
}

[HarmonyPatch(typeof(BlockEntityToolrack), "PutInSlot")]
public class BlockEntityToolrackPutPatch {
    [HarmonyPrefix]
    public static void Prefix(BlockEntityToolrack __instance, IPlayer player, int slot, out PatchState __state) {
        __state = new PatchState();
        __state.itemstacksInPrefix = new ItemStack?[__instance.inventory.Count];
        for (int i = 0; i < __instance.inventory.Count; i++) {
            __state.itemstacksInPrefix[i] = __instance.inventory[i].Itemstack?.Clone();
        }
    }

    [HarmonyPostfix]
    public static void Postfix(BlockEntityToolrack __instance, IPlayer player, int slot, PatchState __state) {
        for (int i = 0; i < __instance.inventory.Count; i++) {
            ItemStack? stackBefore = __state.itemstacksInPrefix[i];
            ItemStack? stackAfter = __instance.inventory[i].Itemstack;

            if (stackAfter != null && stackBefore == null) {
                if (player != null) {
                    Main.Database.AddContainerLog(player.PlayerName, player.PlayerUID, "PLACED", __instance.inventory.InventoryID, stackAfter.GetName(), 1);
                }
                break;
            }
        }
    }

    public class PatchState {
        public ItemStack?[] itemstacksInPrefix;
    }
}

[HarmonyPatch(typeof(BlockEntityToolrack), "TakeFromSlot")]
public class BlockEntityToolrackTakePatch {
    [HarmonyPrefix]
    public static void Prefix(BlockEntityToolrack __instance, IPlayer player, int slot, out PatchState __state) {
        __state = new PatchState();
        __state.itemstacksInPrefix = new ItemStack?[__instance.inventory.Count];
        for (int i = 0; i < __instance.inventory.Count; i++) {
            __state.itemstacksInPrefix[i] = __instance.inventory[i].Itemstack?.Clone();
        }
    }

    [HarmonyPostfix]
    public static void Postfix(BlockEntityToolrack __instance, IPlayer player, int slot, PatchState __state) {
        for (int i = 0; i < __instance.inventory.Count; i++) {
            ItemStack? stackBefore = __state.itemstacksInPrefix[i];
            ItemStack? stackAfter = __instance.inventory[i].Itemstack;

            if (stackBefore != null && stackAfter == null) {
                if (player != null) {
                    Main.Database.AddContainerLog(player.PlayerName, player.PlayerUID, "TAKEN", __instance.inventory.InventoryID, stackBefore.GetName(), 1);
                }
                break;
            }
        }
    }

    public class PatchState {
        public ItemStack?[] itemstacksInPrefix;
    }
}

[HarmonyPatch(typeof(BlockEntityCrate), nameof(BlockEntityCrate.OnBlockInteractStart))]
public class BlockEntityCrateInteractPatch {
    [HarmonyPrefix]
    public static void Prefix(BlockEntityCrate __instance, IPlayer byPlayer, BlockSelection blockSel, out PatchState __state) {
        __state = new PatchState();

        __state.itemstacksInPrefix = new ItemStack?[__instance.Inventory.Count];

        for (int i = 0; i < __instance.Inventory.Count; i++) {
            __state.itemstacksInPrefix[i] = __instance.Inventory[i].Itemstack?.Clone();
        }
    }

    [HarmonyPostfix]
    public static void Postfix(BlockEntityCrate __instance, IPlayer byPlayer, BlockSelection blockSel, PatchState __state) {
        CollectibleObject? itemMovedInOrOut = null;
        int quantity = 0;
        bool goingIn = false;

        for (int i = 0; i < __instance.Inventory.Count; i++) {
            ItemStack? stackBefore = __state.itemstacksInPrefix[i];
            ItemStack? stackAfter = __instance.Inventory[i].Itemstack;

            // Item added to empty slot
            if (stackBefore == null && stackAfter != null) {
                itemMovedInOrOut = stackAfter.Collectible;
                quantity += stackAfter.StackSize;
                goingIn = true;
                continue;
            }
            // Item added to slot
            if (stackAfter != null && stackBefore.StackSize < stackAfter.StackSize) {
                itemMovedInOrOut = stackAfter.Collectible;
                quantity += stackAfter.StackSize - stackBefore.StackSize;
                goingIn = true;
                continue;
            }

            // Item removed ENTIRE from slot
            if (stackBefore != null && stackAfter == null) {
                itemMovedInOrOut = stackBefore.Collectible;
                quantity += stackBefore.StackSize;
                goingIn = false;
                continue;
            }
            // Item removed from slot
            if (stackBefore != null && stackBefore.StackSize > stackAfter.StackSize) {
                itemMovedInOrOut = stackBefore.Collectible;
                quantity += stackBefore.StackSize - stackAfter.StackSize;
                goingIn = false;
                continue;
            }
        }

        if (itemMovedInOrOut == null)
            return;

        string actiontype = goingIn ? "PLACED" : "TAKEN";
        ItemStack stackChange = new(itemMovedInOrOut);

        Main.Database.AddContainerLog(byPlayer.PlayerName, byPlayer.PlayerUID, actiontype, __instance.Inventory.InventoryID, stackChange.GetName(), quantity);
    }

    public class PatchState {
        public ItemStack?[] itemstacksInPrefix;
    }
}